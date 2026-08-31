using System.Collections.Concurrent;
using System.Collections.Generic;
using Game;
using Game.Common;
using Game.Net;
using Game.Objects;
using Game.Prefabs;
using Game.Tools;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;

using CS2MultiplayerMod.Game.Sync.Infrastructure;
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.Systems.Net;
namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Replicates deletions (bulldozing) bidirectionally: detect <see cref="Deleted"/> at
    /// ModificationEnd, broadcast delete command (prefab + position). Realize at ToolUpdate
    /// via <see cref="SyncRealizeSystem"/>, add <see cref="Deleted"/> tag. Echo-guarded.
    /// Objects whose lifecycle belongs to the simulation are excluded, mirroring the rule
    /// BuildSyncSystem already applies to their creation — see <see cref="IsSimulationOwnedLifecycle"/>.
    /// </summary>
    public partial class DeleteSyncSystem : GameSystemBase
    {
        public bool DeferNetForTerrain;

        /// <summary>
        /// Set by <see cref="SyncRealizeSystem"/> while a remote placement is still waiting for the
        /// road it anchors to. A bulldoze can only ever take that road away, and applying one here
        /// inverts the order the two edits were made in - which is how a placement came to be
        /// rejected for a "missing" target this system had removed a moment earlier.
        /// </summary>
        public bool DeferNetForPendingPlacement;
        private readonly ConcurrentQueue<SimulationCommandMessage> _incoming =
            new ConcurrentQueue<SimulationCommandMessage>();
        private readonly ReplicationGuard _guard = new ReplicationGuard();

        // Edge deletes whose armed commit never materialised (apply window expired — see
        // NetSyncSystem._onCommitLost). Replayed ahead of fresh arrivals next cycle.
        private readonly List<NetDeleteCommand> _replayEdgeDeletes = new List<NetDeleteCommand>();

        /// <summary>Unmatched remote deletes wait this long for their build to land locally.</summary>
        private const long DeleteRetryWindowMs = 10000;

        /// <summary>Ceiling on each pending-delete list, so a peer can never grow them without bound.</summary>
        private const int MaxPendingDeletes = 256;

        // Remote deletes that matched nothing yet. Builds and deletes travel in separate queues with
        // different draining rules, so under backlog a delete can be processed BEFORE the build it
        // targets has realized locally; dropping it (the old behaviour) resurrected the street or
        // building on one machine only. Retried every cycle, ahead of fresh arrivals, until the
        // deadline — by then either the build has landed (the retry matches and deletes it) or the
        // geometry genuinely diverged.
        private readonly List<(ObjectDeleteCommand cmd, long deadline)> _objectRetry =
            new List<(ObjectDeleteCommand, long)>();
        private readonly List<(NetDeleteCommand cmd, long deadline)> _edgeRetry =
            new List<(NetDeleteCommand, long)>();

        // Originals named by a tool's Temp transaction this frame — the difference between a
        // player bulldozing something and the simulation retiring it on its own.
        private readonly HashSet<Entity> _toolDeleteOriginals = new HashSet<Entity>();

        /// <summary>
        /// Whether a player's tool is what removed this entity in the frame being captured. The two
        /// removal paths travel on different commands, and <see cref="GrowableSyncSystem"/> asks so
        /// that a bulldozed zoned building is not also announced as a simulation removal.
        /// Only meaningful during the same ModificationEnd pass that collected it.
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
        private CommandObserver _observer;

        protected override void OnCreate()
        {
            base.OnCreate();

            Mod.log.Info(nameof(DeleteSyncSystem) + " ready.");
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _prefabIndex = new PrefabIndex(_prefabSystem, GetEntityQuery(ComponentType.ReadOnly<PrefabData>()));
            // Edge deletes are committed through NetSync's ApplyTool pipeline (see RealizeEdgeDeletes).
            _netSync = World.GetOrCreateSystemManaged<NetSyncSystem>();
            // Realizing a remote delete asks "what stands at this point" — see ObjectSearch.
            _objectSearch = new ObjectSearch(
                World.GetOrCreateSystemManaged<global::Game.Objects.SearchSystem>());

            // Tool output still standing this frame. A bulldoze (and any tool that removes an
            // object as part of its transaction) reaches the victim through a Temp carrying
            // TempFlags.Delete; a simulation-driven removal has no such lineage.
            _toolDeleteTemps = GetEntityQuery(ComponentType.ReadOnly<Temp>());

            // Top-level objects being deleted this frame. Temp excludes tool previews;
            // Owner keeps the dependent object graph off the wire because realization traverses
            // InstalledUpgrade/SubObject ownership and deletes that graph with this single root.
            // Vehicles/creatures are per-sim churn each machine despawns on its own; a
            // replicated despawn can only mis-match remotely (the two sims never agree
            // on where a vehicle is), so they stay off the wire entirely.
            _deletedObjects = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<Deleted, PrefabRef, Transform>(),
                None = SyncQuery.ReadOnly<Temp, Owner, Edge, global::Game.Vehicles.Vehicle,
                    global::Game.Creatures.Creature>(),
            });

            // Owned service upgrades removed on their own (the building properties panel tags just
            // that entity Deleted - no tool involved). See IsStandaloneUpgradeRemoval for why a host
            // delete's children are not published here.
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

            // Edges freshly Created this frame — used to tell a mid-span SPLIT (the original edge is
            // deleted and its two halves are created on its centreline) from a genuine bulldoze.
            _createdEdges = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<Edge, Curve, Created, PrefabRef>(),
                None = SyncQuery.ReadOnly<Temp, Owner, Deleted>(),
            });

            // Pre-existing edges whose geometry CHANGED this frame (Updated but NOT freshly Created).
            // Used to spot node-reduction side-effects: when a bulldoze frees a "false node" between
            // two collinear same-prefab edges, the game merges them — one neighbour survives with the
            // JOINED curve (Updated), the other is Deleted. That victim's delete is a LOCAL side
            // effect the receiver reproduces natively, so it must never go on the wire (see
            // CaptureDeletedEdges).
            _updatedEdges = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<Updated, Edge, Curve, PrefabRef>(),
                None = SyncQuery.ReadOnly<Created, Temp, Deleted, Owner>(),
            });

            // Remote object deletes match against the game's object search tree, not a query —
            // see RealizeObjectDeletes. Edges have no equivalent pool small enough to matter.
            _liveEdges = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<Edge, Curve, PrefabRef>(),
                None = SyncQuery.ReadOnly<Temp, Owner, Deleted>(),
            });

            _observer = SyncObserverBinding.Bind(
                () => new CommandObserver(_incoming, ObjectDeleteCommand.Id, NetDeleteCommand.Id), DrainQueue);
        }

        protected override void OnDestroy()
        {
            SyncObserverBinding.Unbind(_observer, DrainQueue);
            base.OnDestroy();
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

        private void DrainQueue()
        {
            SyncInbox.Clear(_incoming);
            _lastDeleteRealizeMs = 0;
            _replayEdgeDeletes.Clear();
            _objectRetry.Clear();
            _edgeRetry.Clear();
            DeferNetForTerrain = false;
            DeferNetForPendingPlacement = false;
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

            // Drain everything first, then do one scan per category — bulldozing tends to
            // arrive in bursts and the match scan is the expensive part.
            //
            // Edge deletes go through NetSync's isolated net commit (a real bulldoze — props, lanes,
            // terrain and node recombination, which a raw Deleted tag skips). That pipeline handles ONE
            // net batch at a time; while a batch is in flight (or on the frame the player's own gesture
            // applies) we leave incoming edge deletes queued and retry next cycle. A selected build
            // tool only blocks on its actual Apply/Clear frame. Object deletes (a raw Deleted tag on
            // a real entity) always proceed.
            bool netBusy = DeferNetForTerrain || DeferNetForPendingPlacement ||
                           _netSync == null || !_netSync.CanBuildDefinitions;
            // Object deletion also tears down SubObject/SubNet/SubArea ownership. Keep it behind the
            // same graph lock as network work so it cannot invalidate an original while an isolated
            // building/connector transaction is being generated, applied, or drained.
            if (netBusy)
            {
                // Time spent locked out is not the delete's fault. A pending delete's window is for
                // waiting on its own build to arrive; letting it burn down while this system is not
                // even allowed to look would drop a bulldoze that would have matched, and a dropped
                // bulldoze is exactly the divergence that ends in a world reload later.
                ExtendPendingDeleteWindows(service.NowMs);
                return;
            }
            _lastDeleteRealizeMs = service.NowMs;
            long now = service.NowMs;
            long freshDeadline = now + DeleteRetryWindowMs;
            List<(ObjectDeleteCommand cmd, long deadline)> objects = null;
            List<(NetDeleteCommand cmd, long deadline)> edges = null;
            List<SimulationCommandMessage> deferredEdges = null;
            SimulationCommandMessage message;
            while (_incoming.TryDequeue(out message))
            {
                if (message.OriginPlayerId == session.LocalPlayerId) continue;
                try
                {
                    if (message.CommandId == ObjectDeleteCommand.Id)
                        (objects ?? (objects = new List<(ObjectDeleteCommand, long)>()))
                            .Add((ObjectDeleteCommand.Decode(message.Body), freshDeadline));
                    else if (message.CommandId == NetDeleteCommand.Id)
                    {
                        if (netBusy)
                            (deferredEdges ?? (deferredEdges = new List<SimulationCommandMessage>())).Add(message);
                        else
                            (edges ?? (edges = new List<(NetDeleteCommand, long)>()))
                                .Add((NetDeleteCommand.Decode(message.Body), freshDeadline));
                    }
                }
                catch (System.Exception ex) { Mod.log.Warn("[MP] DeleteSync: dropping malformed command: " + ex.Message); }
            }

            // Re-queue edge deletes that arrived while the net pipeline was mid-commit (the drain loop
            // has already emptied the queue, so re-enqueuing is safe — they run next cycle).
            if (deferredEdges != null)
                for (int i = 0; i < deferredEdges.Count; i++) _incoming.Enqueue(deferredEdges[i]);

            // Deletes handed back by NetSync (their armed commit was wiped before it could run) replay
            // ahead of fresh arrivals once the pipeline is idle again.
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




        // Bulldoze targets rarely land on the exact same coordinate on both machines:
        // the two cities drift (each runs its own simulation between world resyncs) and
        // growables level up — which CHANGES their prefab. So matching has to be tolerant:
        // pick the nearest object of the requested prefab within this radius. Only when
        // BOTH the command and the candidate are growable buildings may the prefab differ
        // (the same lot at another level); everything else — and every ploppable — matches
        // by exact prefab or not at all, so a stray delete can never widen into a nearby
        // hospital. The radius is well below lot spacing, so "nearest" never reaches a
        // neighbour.
        private const float ObjectMatchRadius = 8f;



        private static string DeleteKey(string prefabName, float3 position) =>
            "del|" + ReplicationGuard.Key(prefabName, position);

    }
}
