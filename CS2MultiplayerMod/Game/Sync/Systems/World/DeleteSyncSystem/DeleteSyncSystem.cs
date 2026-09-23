using System.Collections.Generic;
using Game.Common;
using Game.Net;
using Game.Objects;
using Game.Prefabs;
using Game.Tools;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Infrastructure;
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.Systems.Net;
namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Replicates bulldozing: captured at ModificationEnd, realized in ToolUpdate. Simulation-owned
    /// lifecycles are excluded, mirroring BuildSync (see <see cref="IsSimulationOwnedLifecycle"/>).
    /// </summary>
    public partial class DeleteSyncSystem : CommandSyncSystem, IRealizeStage
    {
        private readonly ReplicationGuard _guard = new ReplicationGuard();

        // Edge deletes whose armed commit never materialised; replayed ahead of fresh arrivals.
        private readonly List<NetDeleteCommand> _replayEdgeDeletes = new List<NetDeleteCommand>();

        /// <summary>Unmatched remote deletes wait this long for their build to land locally.</summary>
        private const long DeleteRetryWindowMs = 10000;

        /// <summary>Ceiling on each pending-delete list, so a peer can never grow them without bound.</summary>
        private const int MaxPendingDeletes = 256;

        // Deletes that matched nothing yet: under backlog a delete can arrive before the build it
        // targets. Retried each cycle until the deadline.
        private readonly List<(ObjectDeleteCommand cmd, long deadline)> _objectRetry =
            new List<(ObjectDeleteCommand, long)>();
        private readonly List<(NetDeleteCommand cmd, long deadline)> _edgeRetry =
            new List<(NetDeleteCommand, long)>();

        // Originals named by a tool's Temp transaction this frame: player bulldoze vs simulation.
        private readonly HashSet<Entity> _toolDeleteOriginals = new HashSet<Entity>();

        /// <summary>
        /// Whether a player's tool removed this entity this frame, so a bulldozed zoned building is not
        /// also sent as a simulation removal. Valid only during the same ModificationEnd pass.
        /// </summary>
        internal bool IsToolDeleteOriginal(Entity entity) => _toolDeleteOriginals.Contains(entity);

        private PrefabSystem _prefabSystem;
        private PrefabIndex _prefabIndex;
        private NetSyncSystem _netSync;
        private ObjectSearch _objectSearch;
        private EntityQuery _toolDeleteTemps;
        private EntityQuery _deletedObjects;
        private EntityQuery _deletedOwnedUpgrades;
        private EntityQuery _deletedEdges;
        private EntityQuery _createdEdges;
        private EntityQuery _updatedEdges;
        private EntityQuery _liveEdges;

        protected override void OnCreate()
        {
            base.OnCreate();

            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _prefabIndex = new PrefabIndex(_prefabSystem, GetEntityQuery(ComponentType.ReadOnly<PrefabData>()));
            // Edge deletes are committed through NetSync's ApplyTool pipeline (see RealizeEdgeDeletes).
            _netSync = World.GetOrCreateSystemManaged<NetSyncSystem>();
            // Realizing a remote delete asks "what stands at this point" — see ObjectSearch.
            _objectSearch = new ObjectSearch(
                World.GetOrCreateSystemManaged<global::Game.Objects.SearchSystem>());

            // A tool reaches its victim through a Temp with TempFlags.Delete; the simulation does not.
            _toolDeleteTemps = GetEntityQuery(ComponentType.ReadOnly<Temp>());

            // Top-level objects only: realization deletes the owned graph with its root. Vehicles and
            // creatures are per-machine simulation churn and never travel.
            _deletedObjects = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<Deleted, PrefabRef, Transform>(),
                None = SyncQuery.ReadOnly<Temp, Owner, Edge, global::Game.Vehicles.Vehicle,
                    global::Game.Creatures.Creature>(),
            });

            // Upgrades removed on their own from the properties panel (see IsStandaloneUpgradeRemoval).
            _deletedOwnedUpgrades = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<Deleted, PrefabRef, Transform, Owner>(),
                Any = SyncQuery.ReadOnly<global::Game.Buildings.ServiceUpgrade,
                    global::Game.Buildings.Extension>(),
                None = SyncQuery.ReadOnly<Temp, Edge, global::Game.Vehicles.Vehicle,
                    global::Game.Creatures.Creature>(),
            });

            _deletedEdges = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<Deleted, Edge, Curve, PrefabRef>(),
                None = SyncQuery.ReadOnly<Temp, Owner>(),
            });

            // Tells a mid-span split from a bulldoze.
            _createdEdges = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<Edge, Curve, Created, PrefabRef>(),
                None = SyncQuery.ReadOnly<Temp, Owner, Deleted>(),
            });

            // Updated-not-Created edges: spots node-reduction victims (see CaptureDeletedEdges).
            _updatedEdges = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<Updated, Edge, Curve, PrefabRef>(),
                None = SyncQuery.ReadOnly<Created, Temp, Deleted, Owner>(),
            });

            _liveEdges = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<Edge, Curve, PrefabRef>(),
                None = SyncQuery.ReadOnly<Temp, Owner, Deleted>(),
            });

            ListenFor(new[] { ObjectDeleteCommand.Id, ObjectDeleteBatchCommand.Id, NetDeleteCommand.Id },
                ObjectDeleteBatchCommand.MaxEncodedBytes);
        }

        /// <summary>When this system last got to run its match pass. See ExtendPendingDeleteWindows.</summary>
        private long _lastDeleteRealizeMs;

        private void ExtendPendingDeleteWindows(long now)
        {
            long frozenMs = _lastDeleteRealizeMs == 0 ? 0 : now - _lastDeleteRealizeMs;
            _lastDeleteRealizeMs = now;
            if (frozenMs <= 0) return;
            for (int i = 0; i < _objectRetry.Count; i++)
                _objectRetry[i] = (_objectRetry[i].cmd, _objectRetry[i].deadline + frozenMs);
            for (int i = 0; i < _edgeRetry.Count; i++)
                _edgeRetry[i] = (_edgeRetry[i].cmd, _edgeRetry[i].deadline + frozenMs);
        }

        protected override void DrainQueue()
        {
            SyncInbox.Clear(_incoming);
            _lastDeleteRealizeMs = 0;
            _replayEdgeDeletes.Clear();
            _objectRetry.Clear();
            _edgeRetry.Clear();
        }

        protected override void OnUpdate()
        {
            using (Diagnostics.SyncProfiler.Measure("DeleteSync"))
            {
                MultiplayerService service = Mod.Service;
                if (service == null) return;

                MultiplayerSession session = service.Session;
                if (!service.GameplaySyncReady)
                {
                    DrainQueue();
                    return;
                }

                long now = service.NowMs;
                _guard.Prune(now);
                CaptureDeletedObjects(session, now);
                CaptureDeletedEdges(session, now);
            }
        }

        /// <summary>Called by <see cref="SyncRealizeSystem"/> during ToolUpdate (see there for why).</summary>
        public void RealizePending()
        {
            MultiplayerService service = Mod.Service;
            if (service == null) return;

            MultiplayerSession session = service.Session;
            if (!service.GameplaySyncReady) return;

            // Edge deletes commit through NetSync's single isolated batch (a real bulldoze: props, lanes,
            // terrain, node recombination), so they wait while it is busy. They also wait behind a
            // placement still waiting on its road, or that placement loses its target.
            bool netBusy = RealizeGate.TerrainBacklog || RealizeGate.NetMutationHeld ||
                           _netSync == null || !_netSync.CanBuildDefinitions;
            // Object deletes tear down owned graphs, so they share the same lock.
            if (netBusy)
            {
                // Locked-out time does not count against a pending delete's window.
                ExtendPendingDeleteWindows(service.NowMs);
                return;
            }
            _lastDeleteRealizeMs = service.NowMs;
            long now = service.NowMs;
            long freshDeadline = now + DeleteRetryWindowMs;
            List<(ObjectDeleteCommand cmd, long deadline)> objects = null;
            List<(NetDeleteCommand cmd, long deadline)> edges = null;
            List<SimulationCommandMessage> deferredEdges = null;
            while (_incoming.TryDequeue(out SimulationCommandMessage message))
            {
                if (message.OriginPlayerId == session.LocalPlayerId) continue;
                try
                {
                    if (message.CommandId == ObjectDeleteCommand.Id)
                        (objects ?? (objects = new List<(ObjectDeleteCommand, long)>()))
                            .Add((ObjectDeleteCommand.Decode(message.Body), freshDeadline));
                    else if (message.CommandId == ObjectDeleteBatchCommand.Id)
                        AppendObjectDeleteBatch(ObjectDeleteBatchCommand.Decode(message.Body),
                            freshDeadline, ref objects);
                    else if (message.CommandId == NetDeleteCommand.Id)
                    {
                        if (netBusy)
                            (deferredEdges ?? (deferredEdges = new List<SimulationCommandMessage>())).Add(message);
                        else
                            (edges ?? (edges = new List<(NetDeleteCommand, long)>()))
                                .Add((NetDeleteCommand.Decode(message.Body), freshDeadline));
                    }
                }
                catch (System.Exception ex) { SyncLog.Warn(LogTopic.Buildings, "DeleteSync: dropping malformed command: " + ex.Message); }
            }

            if (deferredEdges != null)
                for (int i = 0; i < deferredEdges.Count; i++) _incoming.Enqueue(deferredEdges[i]);

            if (!netBusy && _replayEdgeDeletes.Count > 0)
            {
                if (edges == null) edges = new List<(NetDeleteCommand, long)>();
                for (int i = _replayEdgeDeletes.Count - 1; i >= 0; i--)
                    edges.Insert(0, (_replayEdgeDeletes[i], freshDeadline));
                _replayEdgeDeletes.Clear();
            }

            // Unmatched deletes still inside their retry window run ahead of everything fresh.
            if (_objectRetry.Count > 0)
            {
                if (objects == null) objects = new List<(ObjectDeleteCommand, long)>(_objectRetry);
                else objects.InsertRange(0, _objectRetry);
                _objectRetry.Clear();
            }
            if (!netBusy && _edgeRetry.Count > 0)
            {
                if (edges == null) edges = new List<(NetDeleteCommand, long)>(_edgeRetry);
                else edges.InsertRange(0, _edgeRetry);
                _edgeRetry.Clear();
            }

            if (objects != null) RealizeObjectDeletes(objects, now);
            if (edges != null) RealizeEdgeDeletes(edges, now);
        }

        // The cities drift and growables change prefab when they level, so match the nearest object
        // of the requested prefab within this radius (below lot spacing). Only growable-to-growable
        // may cross prefabs.
        private const float ObjectMatchRadius = 8f;

        private static string DeleteKey(string prefabName, float3 position) =>
            "del|" + ReplicationGuard.Key(prefabName, position);

        /// <summary>Keeps a directly realized remote tree/prop delete from being recaptured as local.</summary>
        internal void MarkRemoteObjectDelete(string prefabName, float3 position, long now)
        {
            if (!string.IsNullOrEmpty(prefabName))
                _guard.Mark(DeleteKey(prefabName, position), now);
        }
    }
}
