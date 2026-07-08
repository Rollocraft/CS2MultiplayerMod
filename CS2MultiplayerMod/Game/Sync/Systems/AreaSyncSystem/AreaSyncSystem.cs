using System.Collections.Concurrent;
using System.Collections.Generic;
using Game;
using Game.Areas;
using Game.Common;
using Game.Prefabs;
using Game.Tools;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;

using CS2MultiplayerMod.Game.Sync.Infrastructure;
using CS2MultiplayerMod.Game.Sync.Commands;
namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Replicates player-drawn areas: <see cref="AreaCreateCommand"/> with polygon ring and
    /// <see cref="CreationDefinition"/>, excluded map tiles. 1 Hz scan sends redraws as
    /// <see cref="AreaUpdateCommand"/> at old centroid, rewrites Node buffer in place.
    /// </summary>
    public partial class AreaSyncSystem : GameSystemBase
    {
        private const long EditScanIntervalMs = 1000;

        private readonly ConcurrentQueue<SimulationCommandMessage> _incoming =
            new ConcurrentQueue<SimulationCommandMessage>();
        private readonly ReplicationGuard _guard = new ReplicationGuard();
        private Dictionary<Entity, float3[]> _knownRings = new Dictionary<Entity, float3[]>();
        private Dictionary<Entity, float3[]> _nextRings = new Dictionary<Entity, float3[]>();
        private long _lastEditScanMs;

        private PrefabSystem _prefabSystem;
        private PrefabIndex _prefabIndex;
        private EntityQuery _createdAreas;
        private EntityQuery _deletedAreas;
        private EntityQuery _liveAreas;
        private CommandObserver _observer;

        protected override void OnCreate()
        {
            base.OnCreate();

            Mod.log.Info(nameof(AreaSyncSystem) + " ready.");
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _prefabIndex = new PrefabIndex(_prefabSystem, GetEntityQuery(ComponentType.ReadOnly<PrefabData>()));

            _createdAreas = GetEntityQuery(AreaQuery(ComponentType.ReadOnly<Created>()));
            _deletedAreas = GetEntityQuery(AreaQuery(ComponentType.ReadOnly<Deleted>()));
            _liveAreas = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Area>(),
                    ComponentType.ReadOnly<Node>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Owner>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<MapTile>(),
                },
            });

            if (Mod.Service != null)
            {
                _observer = new CommandObserver(_incoming, AreaCreateCommand.Id, AreaUpdateCommand.Id, AreaDeleteCommand.Id);
                Mod.Service.Session.AddObserver(_observer);
            }
        }

        private static EntityQueryDesc AreaQuery(ComponentType lifecycleTag) => new EntityQueryDesc
        {
            All = new[]
            {
                lifecycleTag,
                ComponentType.ReadOnly<Area>(),
                ComponentType.ReadOnly<Node>(),
                ComponentType.ReadOnly<PrefabRef>(),
            },
            None = new[]
            {
                ComponentType.ReadOnly<Temp>(),
                // Owned areas (building lots) live and die with their owner on both sides.
                ComponentType.ReadOnly<Owner>(),
                ComponentType.ReadOnly<MapTile>(),
            },
        };

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
            if (!service.GameplaySyncReady)
            {
                if (_knownRings.Count > 0) _knownRings.Clear();
                return;
            }

            long now = service.NowMs;
            _guard.Prune(now);
            CaptureCreated(session, now);
            CaptureDeleted(session, now);
            ScanForEdits(session, now);
        }

        /// <summary>Called by <see cref="SyncRealizeSystem"/> during ToolUpdate (see there for why).</summary>
        public void RealizePending()
        {
            MultiplayerService service = Mod.Service;
            if (service == null) return;

            MultiplayerSession session = service.Session;
            if (!service.GameplaySyncReady) return;

            List<AreaDeleteCommand> deletes = null;
            long now = service.NowMs;
            SimulationCommandMessage message;
            while (_incoming.TryDequeue(out message))
            {
                if (message.OriginPlayerId == session.LocalPlayerId) continue;
                try
                {
                    if (message.CommandId == AreaCreateCommand.Id)
                        RealizeCreate(AreaCreateCommand.Decode(message.Body), message.OriginPlayerId, now);
                    else if (message.CommandId == AreaUpdateCommand.Id)
                        RealizeUpdate(AreaUpdateCommand.Decode(message.Body), message.OriginPlayerId, now);
                    else if (message.CommandId == AreaDeleteCommand.Id)
                        (deletes ?? (deletes = new List<AreaDeleteCommand>())).Add(AreaDeleteCommand.Decode(message.Body));
                }
                catch (System.Exception ex) { Mod.log.Warn("[MP] AreaSync: dropping malformed command: " + ex.Message); }
            }
            if (deletes != null) RealizeDeletes(deletes, now);
        }

        // ---- Polygon edits (redraws) -------------------------------------------










        private static string AreaKey(string prefabName, float3 firstNode) =>
            "area|" + ReplicationGuard.Key(prefabName, firstNode);

        private static string AreaDeleteKey(string prefabName, float3 firstNode) =>
            "areadel|" + ReplicationGuard.Key(prefabName, firstNode);

        private static string AreaUpdateKey(string prefabName, float3 centroid) =>
            "areaupd|" + ReplicationGuard.Key(prefabName, centroid);

    }
}
