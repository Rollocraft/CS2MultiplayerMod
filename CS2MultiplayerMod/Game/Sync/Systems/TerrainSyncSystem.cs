using System.Collections.Concurrent;
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
namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Replicates terraforming as brush strokes: detect live <see cref="Brush"/> entities
    /// at ModificationEnd, broadcast prefab + position/size/angle/strength. Realize at
    /// ToolUpdate via <see cref="SyncRealizeSystem"/>, recreating the brush entity for
    /// the game pipeline. Strokes are rate-identical but float-fuzzy; periodic resync trues.
    /// </summary>
    public partial class TerrainSyncSystem : GameSystemBase
    {
        private readonly ConcurrentQueue<SimulationCommandMessage> _incoming =
            new ConcurrentQueue<SimulationCommandMessage>();

        private PrefabSystem _prefabSystem;
        private PrefabIndex _prefabIndex;
        private EntityQuery _brushes;
        private CommandObserver _observer;

        private long _diagStartMs = -1;
        private int _diagCaptured, _diagRealized;

        protected override void OnCreate()
        {
            base.OnCreate();

            Mod.log.Info(nameof(TerrainSyncSystem) + " ready.");
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _prefabIndex = new PrefabIndex(_prefabSystem, GetEntityQuery(ComponentType.ReadOnly<PrefabData>()));

            _brushes = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Brush>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Updated>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                },
            });

            if (Mod.Service != null)
            {
                _observer = new CommandObserver(_incoming, TerrainBrushCommand.Id);
                Mod.Service.Session.AddObserver(_observer);
            }
        }

        protected override void OnDestroy()
        {
            if (_observer != null && Mod.Service != null)
                Mod.Service.Session.RemoveObserver(_observer);
            base.OnDestroy();
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

        /// <summary>Called by <see cref="SyncRealizeSystem"/> during ToolUpdate (see there for why).</summary>
        public void RealizePending()
        {
            MultiplayerService service = Mod.Service;
            if (service == null) return;

            MultiplayerSession session = service.Session;
            if (!service.GameplaySyncReady) return;

            SimulationCommandMessage message;
            while (_incoming.TryDequeue(out message))
            {
                if (message.OriginPlayerId == session.LocalPlayerId) continue;

                TerrainBrushCommand command;
                try { command = TerrainBrushCommand.Decode(message.Body); }
                catch (System.Exception ex) { Mod.log.Warn("[MP] TerrainSync: dropping malformed command: " + ex.Message); continue; }
                Entity prefab;
                if (!_prefabIndex.TryResolve(command.BrushPrefabName, out prefab)) continue;

                Entity brush = EntityManager.CreateEntity();
                EntityManager.AddComponentData(brush, new Brush
                {
                    m_Position = new float3(command.PosX, command.PosY, command.PosZ),
                    m_Size = command.Size,
                    m_Angle = command.Angle,
                    m_Strength = command.Strength,
                });
                EntityManager.AddComponentData(brush, new PrefabRef { m_Prefab = prefab });
                EntityManager.AddComponent<Updated>(brush);
                EntityManager.AddComponent<Deleted>(brush);
                _diagRealized++;
            }
        }

        private void CaptureBrushes(MultiplayerSession session)
        {
            if (_brushes.IsEmptyIgnoreFilter) return;

            NativeArray<Entity> entities = _brushes.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Brush brush = EntityManager.GetComponentData<Brush>(entities[i]);

                    // Brushes we just realized carry Deleted (they die at Cleanup) — those
                    // are replicas, never echo them back.
                    if (EntityManager.HasComponent<Deleted>(entities[i])) continue;

                    Entity brushPrefab = EntityManager.GetComponentData<PrefabRef>(entities[i]).m_Prefab;
                    string name = _prefabSystem.GetPrefabName(brushPrefab);
                    if (string.IsNullOrEmpty(name)) continue;

                    var command = new TerrainBrushCommand
                    {
                        BrushPrefabName = name,
                        PosX = brush.m_Position.x,
                        PosY = brush.m_Position.y,
                        PosZ = brush.m_Position.z,
                        Size = brush.m_Size,
                        Angle = brush.m_Angle,
                        Strength = brush.m_Strength,
                    };
                    session.SendCommand(0, TerrainBrushCommand.Id, command.Encode());
                    _diagCaptured++;
                }
            }
            finally
            {
                entities.Dispose();
            }
        }

        private void FlushDiagnostics(long now)
        {
            if (_diagStartMs < 0) { _diagStartMs = now; return; }
            if (now - _diagStartMs < 5000) return;
            if (_diagCaptured > 0 || _diagRealized > 0)
                Mod.Verbose("[MP] TerrainSync/5s: captured " + _diagCaptured + " stroke(s), realized " + _diagRealized + ".");
            _diagCaptured = _diagRealized = 0;
            _diagStartMs = now;
        }

    }
}
