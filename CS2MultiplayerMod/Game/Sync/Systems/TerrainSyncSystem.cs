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
    /// <c>Temp + Brush</c> entity (tagged <see cref="RemoteTerrainBrush"/>) and apply the isolated
    /// brush domain through <c>ApplyBrushesSystem</c>. This runs the height/material/resource change
    /// on the normal path and tags each sample
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
        private global::Game.Simulation.TerrainSystem _terrainSystem;
        private EntityQuery _appliedBrushes;
        private CommandObserver _observer;
        private bool _awaitingHeightReadback;
        private bool _commitApplyFailureLogged;

        private long _diagStartMs = -1;
        private int _diagCaptured, _diagRealized;

        protected override void OnCreate()
        {
            base.OnCreate();

            Mod.log.Info(nameof(TerrainSyncSystem) + " ready.");
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _prefabIndex = new PrefabIndex(_prefabSystem, GetEntityQuery(ComponentType.ReadOnly<PrefabData>()));
            _netSync = World.GetOrCreateSystemManaged<NetSyncSystem>();
            _terrainSystem = World.GetOrCreateSystemManaged<global::Game.Simulation.TerrainSystem>();

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
            _awaitingHeightReadback = false;
            _commitApplyFailureLogged = false;
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
        /// the sender's. Own-origin echoes may defer one frame until they are discarded; treating any
        /// queued command as backlog prevents a remote edit hidden behind that echo from being missed.
        /// </summary>
        public bool HasBacklog()
        {
            if (_awaitingHeightReadback) return true;
            if (_pending.Count > 0) return true;
            return !_incoming.IsEmpty;
        }

        /// <summary>
        /// Complete the asynchronous heightmap readback from the previous remote brush pass before
        /// dependent roads and objects are allowed to sample terrain. Queue drain alone is not a
        /// terrain-consistency barrier: the CPU height array can still contain the pre-edit surface.
        /// </summary>
        public void CompletePendingHeightReadback()
        {
            if (!_awaitingHeightReadback || _terrainSystem == null) return;
            try
            {
                // A batch contains many ApplyBrush calls. If the first call already started a GPU
                // request, later samples mark that request out-of-date; completing it immediately
                // schedules one consolidated follow-up request. The second call waits that follow-up
                // (or is a cheap no-op when there was only one request).
                _terrainSystem.GetHeightData(waitForPending: true);
                _terrainSystem.GetHeightData(waitForPending: true);
                _awaitingHeightReadback = false;
                Diagnostics.FlightRecorder.Note("terrain height readback complete");
            }
            catch (System.Exception ex)
            {
                // Do not wedge all subsequent construction forever if a future game build changes
                // the readback contract. The next authoritative world sync remains the repair path.
                _awaitingHeightReadback = false;
                Mod.log.Warn("[MP] TerrainSync: height readback barrier failed: " + ex.Message);
            }
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
            // Leave room for a maximum-size command before dequeueing it. This preserves every
            // sample in that command instead of accepting a prefix and silently dropping its tail.
            while (_pending.Count <= MaxPendingSamples - TerrainBrushCommand.MaxSamples &&
                   scanned < MaxDecodePerFrame
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

                for (int i = 0; i < command.Samples.Length; i++)
                    _pending.Add((tool, brush, command.Samples[i]));
            }

            if (_pending.Count == 0) return;

            // A local Apply frame gets priority because ToolOutputSystem would run the brush pass
            // again later this frame. Clear is safe: the isolated remote brush is applied here
            // first, while the standing local brush preview is disabled only for that direct pass.
            if (_netSync == null || !_netSync.CanApplyAuxiliaryTemps) return;

            _netSync.PrepareAuxiliaryTemps();

            int candidateCount = System.Math.Min(MaxApplyPerFrame, _pending.Count);
            var created = new List<Entity>(candidateCount);
            bool changesHeight = false;
            try
            {
                for (int i = 0; i < candidateCount; i++)
                {
                    var item = _pending[i];
                    bool sampleChangesHeight;
                    created.Add(CreateRemoteBrush(item.tool, item.brush, item.sample,
                        out sampleChangesHeight));
                    changesHeight |= sampleChangesHeight;
                }
            }
            catch (System.Exception ex)
            {
                DestroyUncommittedBrushes(created);
                Mod.log.Warn("[MP] TerrainSync: could not create remote brush batch: " + ex.Message);
                return;
            }

            bool committed = false;
            string commitError = null;
            try { committed = created.Count > 0 && _netSync.CommitAuxiliaryTempsNow(); }
            catch (System.Exception ex)
            {
                commitError = ex.Message;
            }

            if (!committed)
            {
                DestroyUncommittedBrushes(created);
                if (!_commitApplyFailureLogged)
                {
                    _commitApplyFailureLogged = true;
                    Mod.log.Warn("[MP] TerrainSync: brush apply unavailable; remote samples remain queued" +
                                 (string.IsNullOrEmpty(commitError) ? "." : ": " + commitError));
                }
                return;
            }

            _commitApplyFailureLogged = false;
            _pending.RemoveRange(0, created.Count);
            if (changesHeight) _awaitingHeightReadback = true;
            _diagRealized += created.Count;
            Diagnostics.FlightRecorder.Note("terrain realize n=" + created.Count +
                (_pending.Count > 0 ? " held=" + _pending.Count : ""));
        }

        private Entity CreateRemoteBrush(Entity tool, Entity brushPrefab, TerrainBrushCommand.Sample s,
            out bool changesHeight)
        {
            TerraformingData toolData = EntityManager.GetComponentData<TerraformingData>(tool);
            changesHeight = toolData.m_Target == TerraformingTarget.Height;
            float adjustedStrength = s.Strength;
            if (changesHeight)
            {
                float receiverDelta = UnityEngine.Time.unscaledDeltaTime;
                if (receiverDelta <= 0f || float.IsNaN(receiverDelta) || float.IsInfinity(receiverDelta))
                    receiverDelta = 0.0001f;
                adjustedStrength = s.Strength * s.DeltaTime / receiverDelta;
            }
            Entity brush = EntityManager.CreateEntity();
            try
            {
                EntityManager.AddComponentData(brush, new Brush
                {
                    m_Tool = tool,
                    m_Position = new float3(s.PosX, s.PosY, s.PosZ),
                    m_Target = new float3(s.TargetX, s.TargetY, s.TargetZ),
                    m_Start = new float3(s.StartX, s.StartY, s.StartZ),
                    m_Size = s.Size,
                    m_Angle = s.Angle,
                    m_Strength = adjustedStrength,
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
                return brush;
            }
            catch
            {
                if (EntityManager.Exists(brush)) EntityManager.DestroyEntity(brush);
                throw;
            }
        }

        private void DestroyUncommittedBrushes(List<Entity> brushes)
        {
            for (int i = 0; i < brushes.Count; i++)
                if (EntityManager.Exists(brushes[i])) EntityManager.DestroyEntity(brushes[i]);
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
                float sourceDelta = UnityEngine.Time.unscaledDeltaTime;
                if (sourceDelta <= 0f || sourceDelta > 10f ||
                    float.IsNaN(sourceDelta) || float.IsInfinity(sourceDelta)) return;
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
                        DeltaTime = sourceDelta,
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
