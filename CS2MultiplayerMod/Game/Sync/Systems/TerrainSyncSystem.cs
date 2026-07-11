using System.Collections.Concurrent;
using System.Collections.Generic;
using Game;
using Game.Common;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;

using CS2MultiplayerMod.Game.Sync.Infrastructure;
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.Systems.Net;
namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>Private tag on brush samples we realized, so capture never echoes them back.</summary>
    internal struct RemoteTerrainBrush : IComponentData { }

    /// <summary>
    /// Replicates terraforming as the stream of applied brush samples the game itself produces.
    ///
    /// Capture (ModificationEnd): read every <c>Brush + PrefabRef + Temp + Applied</c> entity that is
    /// not one of ours, and broadcast the terraforming tool prefab, the brush prefab and each
    /// sample's complete applied <see cref="Brush"/> state. Preview and cancelled brushes carry no
    /// <see cref="Applied"/> tag and are ignored. Consecutive samples sharing a tool+brush batch into
    /// one <see cref="TerrainBrushCommand"/>.
    ///
    /// Realize (ToolUpdate, via <see cref="SyncRealizeSystem"/>): recreate each sample as a real
    /// <c>Temp + Brush</c> entity (tagged <see cref="RemoteTerrainBrush"/>) and reserve the same
    /// frame's ApplyTool pass through the net commit coordinator, so <c>ApplyBrushesSystem</c> runs
    /// the height/material/resource change on the normal path and tags each sample
    /// <c>Applied + Deleted</c>. Independent bounds cap samples-per-frame, decode scan and inbox size;
    /// residual GPU/float drift is trued by the periodic world resync.
    /// </summary>
    public partial class TerrainSyncSystem : GameSystemBase
    {
        // Receiver apply budget: brush samples materialised per frame. Terrain's capture rate
        // (≤ ~60 samples/s) is far below this, so the budget only bites on a backlog drain.
        private const int MaxApplyPerFrame = 64;

        // Decoder scan budget: commands pulled off the inbox per frame, so a malformed/unknown-prefab
        // flood cannot create an unbounded main-thread loop.
        private const int MaxDecodePerFrame = 64;

        // Ceiling on decoded-but-not-yet-applied samples, so a burst that outruns the apply budget
        // stays bounded in memory.
        private const int MaxPendingSamples = 4096;

        private readonly ConcurrentQueue<SimulationCommandMessage> _incoming =
            new ConcurrentQueue<SimulationCommandMessage>();

        // Samples resolved and waiting for a safe ApplyTool frame. A partially-applied batch keeps
        // its remaining samples here and continues in order next frame.
        private readonly List<(Entity tool, Entity brush, TerrainBrushCommand.Sample sample)> _pending =
            new List<(Entity, Entity, TerrainBrushCommand.Sample)>();

        private PrefabSystem _prefabSystem;
        private PrefabIndex _prefabIndex;
        private NetSyncSystem _netSync;
        private EntityQuery _appliedBrushes;
        private CommandObserver _observer;

        private long _diagStartMs = -1;
        private int _diagCaptured, _diagRealized;

        protected override void OnCreate()
        {
            base.OnCreate();

            Mod.log.Info(nameof(TerrainSyncSystem) + " ready.");
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _prefabIndex = new PrefabIndex(_prefabSystem, GetEntityQuery(ComponentType.ReadOnly<PrefabData>()));
            _netSync = World.GetOrCreateSystemManaged<NetSyncSystem>();

            // Applied brush samples the local player just laid down: the ApplyTool pass tags each
            // consumed sample Applied (+Deleted). RemoteTerrainBrush excludes the ones we realized.
            _appliedBrushes = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Brush>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Applied>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<RemoteTerrainBrush>(),
                },
            });

            if (Mod.Service != null)
            {
                _observer = new CommandObserver(_incoming, TerrainBrushCommand.Id)
                {
                    MaxBodyBytes = TerrainBrushCommand.MaxEncodedBytes,
                };
                Mod.Service.Session.AddObserver(_observer);
            }
            SyncInbox.RegisterDrain(DrainQueue);
        }

        protected override void OnDestroy()
        {
            SyncInbox.UnregisterDrain(DrainQueue);
            if (_observer != null && Mod.Service != null)
                Mod.Service.Session.RemoveObserver(_observer);
            base.OnDestroy();
        }

        /// <summary>World reload purges queued and half-applied strokes (see <see cref="SyncInbox"/>).</summary>
        private void DrainQueue()
        {
            SyncInbox.Clear(_incoming);
            _pending.Clear();
        }

        protected override void OnUpdate()
        {
            MultiplayerService service = Mod.Service;
            if (service == null) return;

            MultiplayerSession session = service.Session;
            if (!service.GameplaySyncReady) return;

            CaptureBrushes(session);
            FlushDiagnostics(service.NowMs);
        }

        /// <summary>
        /// True while remote terrain work is still queued. Read once per frame by
        /// <see cref="SyncRealizeSystem"/> to hold new net/object realizes until the surface matches
        /// the sender's. Own-origin echoes at the queue head (the host loops its own sends back) are
        /// not backlog - they never realize here.
        /// </summary>
        public bool HasBacklog()
        {
            if (_pending.Count > 0) return true;
            SimulationCommandMessage head;
            if (!_incoming.TryPeek(out head)) return false;
            MultiplayerService service = Mod.Service;
            if (service != null && head.OriginPlayerId == service.Session.LocalPlayerId) return false;
            return true;
        }

        /// <summary>Called by <see cref="SyncRealizeSystem"/> during ToolUpdate (see there for why).</summary>
        public void RealizePending()
        {
            MultiplayerService service = Mod.Service;
            if (service == null) return;

            MultiplayerSession session = service.Session;
            if (!service.GameplaySyncReady) return;

            // Refill the pending list from the inbox (bounded scan), then apply from it.
            int scanned = 0;
            SimulationCommandMessage message;
            while (_pending.Count < MaxPendingSamples && scanned < MaxDecodePerFrame
                   && _incoming.TryDequeue(out message))
            {
                scanned++;
                if (message.OriginPlayerId == session.LocalPlayerId) continue;

                TerrainBrushCommand command;
                try { command = TerrainBrushCommand.Decode(message.Body); }
                catch (System.Exception ex) { Mod.log.Warn("[MP] TerrainSync: dropping malformed command: " + ex.Message); continue; }

                Entity tool, brush;
                if (!_prefabIndex.TryResolve(command.ToolPrefabName, out tool) ||
                    !_prefabIndex.TryResolve(command.BrushPrefabName, out brush)) continue;
                // ApplyBrushesSystem dereferences TerraformingData[tool] and BrushData/BrushCell[brush]
                // with no existence check — a wrong prefab type there is a native crash, not an
                // exception. Only queue a sample whose prefabs carry them.
                if (!EntityManager.HasComponent<TerraformingData>(tool)) continue;
                if (!EntityManager.HasComponent<BrushData>(brush) || !EntityManager.HasBuffer<BrushCell>(brush)) continue;

                for (int i = 0; i < command.Samples.Length && _pending.Count < MaxPendingSamples; i++)
                    _pending.Add((tool, brush, command.Samples[i]));
            }

            if (_pending.Count == 0) return;

            // The ApplyTool flip must not fight a net commit or the player's own apply: gate on the
            // coordinator, exactly like the delete/replace feeders.
            if (_netSync == null || !_netSync.CanBuildDefinitions) return;

            // Clear the player's preview for one frame so the flip commits only our brush samples,
            // then materialise up to the budget and drive the pass this same frame.
            _netSync.PrepareDefinitionFrame();

            int applied = 0;
            while (applied < MaxApplyPerFrame && _pending.Count > 0)
            {
                var item = _pending[0];
                _pending.RemoveAt(0);
                CreateRemoteBrush(item.tool, item.brush, item.sample);
                applied++;
            }

            if (applied > 0)
            {
                _netSync.CommitAuxiliaryTempsNow();
                _diagRealized += applied;
                Diagnostics.FlightRecorder.Note("terrain realize n=" + applied +
                    (_pending.Count > 0 ? " held=" + _pending.Count : ""));
            }
        }

        private void CreateRemoteBrush(Entity tool, Entity brushPrefab, TerrainBrushCommand.Sample s)
        {
            Entity brush = EntityManager.CreateEntity();
            EntityManager.AddComponentData(brush, new Brush
            {
                m_Tool = tool,
                m_Position = new float3(s.PosX, s.PosY, s.PosZ),
                m_Target = new float3(s.TargetX, s.TargetY, s.TargetZ),
                m_Start = new float3(s.StartX, s.StartY, s.StartZ),
                m_Size = s.Size,
                m_Angle = s.Angle,
                m_Strength = s.Strength,
                m_Opacity = s.Opacity,
            });
            EntityManager.AddComponentData(brush, new PrefabRef { m_Prefab = brushPrefab });
            // A real applied brush is Temp + Brush; Essential|Create is the non-delete recipe
            // GenerateBrushesSystem stamps. ApplyBrushesSystem consumes it and adds Applied+Deleted.
            EntityManager.AddComponentData(brush, new Temp
            {
                m_Original = Entity.Null,
                m_Flags = TempFlags.Essential | TempFlags.Create,
            });
            EntityManager.AddComponent<RemoteTerrainBrush>(brush);
        }

        private void CaptureBrushes(MultiplayerSession session)
        {
            if (_appliedBrushes.IsEmptyIgnoreFilter) return;

            NativeArray<Entity> entities = _appliedBrushes.ToEntityArray(Allocator.Temp);
            try
            {
                // Batch consecutive samples that share a tool+brush into one command (a fast small
                // brush drag applies many samples per frame).
                var batches = new Dictionary<(string tool, string brush), List<TerrainBrushCommand.Sample>>();
                for (int i = 0; i < entities.Length; i++)
                {
                    Brush brush = EntityManager.GetComponentData<Brush>(entities[i]);

                    string toolName = _prefabSystem.GetPrefabName(brush.m_Tool);
                    if (string.IsNullOrEmpty(toolName)) continue;
                    string brushName = _prefabSystem.GetPrefabName(
                        EntityManager.GetComponentData<PrefabRef>(entities[i]).m_Prefab);
                    if (string.IsNullOrEmpty(brushName)) continue;

                    // A cancelled/preview brush carries opacity outside (0,1] or no real edit — the
                    // wire guard would reject it anyway; skip so a batch never fails to encode.
                    if (brush.m_Opacity <= 0f || brush.m_Opacity > 1f) continue;

                    var key = (toolName, brushName);
                    List<TerrainBrushCommand.Sample> list;
                    if (!batches.TryGetValue(key, out list))
                    {
                        list = new List<TerrainBrushCommand.Sample>();
                        batches[key] = list;
                    }
                    list.Add(new TerrainBrushCommand.Sample
                    {
                        PosX = brush.m_Position.x, PosY = brush.m_Position.y, PosZ = brush.m_Position.z,
                        TargetX = brush.m_Target.x, TargetY = brush.m_Target.y, TargetZ = brush.m_Target.z,
                        StartX = brush.m_Start.x, StartY = brush.m_Start.y, StartZ = brush.m_Start.z,
                        Size = brush.m_Size,
                        Angle = brush.m_Angle,
                        Strength = brush.m_Strength,
                        Opacity = brush.m_Opacity,
                    });
                }

                foreach (var batch in batches)
                    SendBatch(session, batch.Key.tool, batch.Key.brush, batch.Value);
            }
            finally
            {
                entities.Dispose();
            }
        }

        private void SendBatch(MultiplayerSession session, string tool, string brush,
            List<TerrainBrushCommand.Sample> samples)
        {
            // Split a batch bigger than the per-command sample cap across several commands.
            for (int offset = 0; offset < samples.Count; offset += TerrainBrushCommand.MaxSamples)
            {
                int count = System.Math.Min(TerrainBrushCommand.MaxSamples, samples.Count - offset);
                var chunk = new TerrainBrushCommand.Sample[count];
                samples.CopyTo(offset, chunk, 0, count);
                var command = new TerrainBrushCommand
                {
                    ToolPrefabName = tool,
                    BrushPrefabName = brush,
                    Samples = chunk,
                };
                session.SendCommand(0, TerrainBrushCommand.Id, command.Encode());
                _diagCaptured += count;
            }
        }

        private void FlushDiagnostics(long now)
        {
            if (_diagStartMs < 0) { _diagStartMs = now; return; }
            if (now - _diagStartMs < 5000) return;
            if (_diagCaptured > 0 || _diagRealized > 0)
                Mod.Verbose("[MP] TerrainSync/5s: captured " + _diagCaptured + " sample(s), realized " + _diagRealized + ".");
            _diagCaptured = _diagRealized = 0;
            _diagStartMs = now;
        }
    }
}
