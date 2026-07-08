using System.Collections.Concurrent;
using System.Collections.Generic;
using Game;
using Game.Common;
using Game.Prefabs;
using Game.Routes;
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
    /// Replicates transit lines: <see cref="Route"/> entity with waypoint ring, matched by
    /// first waypoint position. 1 Hz scan sends edits via <see cref="RouteUpdateCommand"/>;
    /// recolor sets <see cref="Color"/>, stop changes rebuild via definition pipeline.
    /// </summary>
    public partial class RouteSyncSystem : GameSystemBase
    {
        private const long EditScanIntervalMs = 1000;

        private readonly ConcurrentQueue<SimulationCommandMessage> _incoming =
            new ConcurrentQueue<SimulationCommandMessage>();
        private readonly ReplicationGuard _guard = new ReplicationGuard();
        private Dictionary<Entity, RouteSnapshot> _knownRoutes = new Dictionary<Entity, RouteSnapshot>();
        private Dictionary<Entity, RouteSnapshot> _nextRoutes = new Dictionary<Entity, RouteSnapshot>();
        private long _lastEditScanMs;

        private struct RouteSnapshot
        {
            public float3[] Ring;
            public uint Rgba;
        }

        private PrefabSystem _prefabSystem;
        private PrefabIndex _prefabIndex;
        private EntityQuery _createdRoutes;
        private EntityQuery _deletedRoutes;
        private EntityQuery _liveRoutes;
        private CommandObserver _observer;

        protected override void OnCreate()
        {
            base.OnCreate();

            Mod.log.Info(nameof(RouteSyncSystem) + " ready.");
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _prefabIndex = new PrefabIndex(_prefabSystem, GetEntityQuery(ComponentType.ReadOnly<PrefabData>()));

            _createdRoutes = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Created>(),
                    ComponentType.ReadOnly<Route>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                },
            });

            _deletedRoutes = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Route>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                },
            });

            _liveRoutes = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Route>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                },
            });

            if (Mod.Service != null)
            {
                _observer = new CommandObserver(_incoming, RouteCreateCommand.Id, RouteUpdateCommand.Id, RouteDeleteCommand.Id);
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
            if (!service.GameplaySyncReady)
            {
                if (_knownRoutes.Count > 0) _knownRoutes.Clear();
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

            List<RouteDeleteCommand> deletes = null;
            long now = service.NowMs;
            SimulationCommandMessage message;
            while (_incoming.TryDequeue(out message))
            {
                if (message.OriginPlayerId == session.LocalPlayerId) continue;
                try
                {
                    if (message.CommandId == RouteCreateCommand.Id)
                        RealizeCreate(RouteCreateCommand.Decode(message.Body), message.OriginPlayerId, now);
                    else if (message.CommandId == RouteUpdateCommand.Id)
                        RealizeUpdate(RouteUpdateCommand.Decode(message.Body), message.OriginPlayerId, now);
                    else if (message.CommandId == RouteDeleteCommand.Id)
                        (deletes ?? (deletes = new List<RouteDeleteCommand>())).Add(RouteDeleteCommand.Decode(message.Body));
                }
                catch (System.Exception ex) { Mod.log.Warn("[MP] RouteSync: dropping malformed command: " + ex.Message); }
            }
            if (deletes != null) RealizeDeletes(deletes, now);
        }





        // ---- Line edits (stops / color) ----------------------------------------






        private static string RouteKey(string prefabName, float3 firstWaypoint) =>
            "route|" + ReplicationGuard.Key(prefabName, firstWaypoint);

        private static string RouteDeleteKey(string prefabName, float3 firstWaypoint) =>
            "routedel|" + ReplicationGuard.Key(prefabName, firstWaypoint);

        private static string RouteUpdateKey(string prefabName, float3 firstWaypoint) =>
            "routeupd|" + ReplicationGuard.Key(prefabName, firstWaypoint);

    }
}
