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
    /// via <see cref="SyncRealizeSystem"/>, add <see cref="Deleted"/> tag. Echo-guarded,
    /// includes sim-triggered demolitions.
    /// </summary>
    public partial class DeleteSyncSystem : GameSystemBase
    {
        public bool DeferNetForTerrain;
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

        private PrefabSystem _prefabSystem;
        private PrefabIndex _prefabIndex;
        private NetSyncSystem _netSync;
        private EntityQuery _deletedObjects;
        private EntityQuery _deletedEdges;
        private EntityQuery _createdEdges;
        private EntityQuery _updatedEdges;
        private EntityQuery _liveObjects;
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

            // Top-level objects being deleted this frame. Temp excludes tool previews;
            // Owner excludes sub-objects (they die with their owner on both machines).
            // Vehicles/creatures are per-sim churn each machine despawns on its own; a
            // replicated despawn can only mis-match remotely (the two sims never agree
            // on where a vehicle is), so they stay off the wire entirely.
            _deletedObjects = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Transform>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Owner>(),
                    ComponentType.ReadOnly<Edge>(),
                    ComponentType.ReadOnly<global::Game.Vehicles.Vehicle>(),
                    ComponentType.ReadOnly<global::Game.Creatures.Creature>(),
                },
            });

            _deletedEdges = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Edge>(),
                    ComponentType.ReadOnly<Curve>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Owner>(),
                },
            });

            // Edges freshly Created this frame — used to tell a mid-span SPLIT (the original edge is
            // deleted and its two halves are created on its centreline) from a genuine bulldoze.
            _createdEdges = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Edge>(),
                    ComponentType.ReadOnly<Curve>(),
                    ComponentType.ReadOnly<Created>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Owner>(),
                    ComponentType.ReadOnly<Deleted>(),
                },
            });

            // Pre-existing edges whose geometry CHANGED this frame (Updated but NOT freshly Created).
            // Used to spot node-reduction side-effects: when a bulldoze frees a "false node" between
            // two collinear same-prefab edges, the game merges them — one neighbour survives with the
            // JOINED curve (Updated), the other is Deleted. That victim's delete is a LOCAL side
            // effect the receiver reproduces natively, so it must never go on the wire (see
            // CaptureDeletedEdges).
            _updatedEdges = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Updated>(),
                    ComponentType.ReadOnly<Edge>(),
                    ComponentType.ReadOnly<Curve>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Created>(),
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Owner>(),
                },
            });

            // Lookup pools for realizing remote deletes (scan + match by prefab/position).
            // Vehicles/creatures excluded to mirror the capture query above.
            _liveObjects = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Transform>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Owner>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Edge>(),
                    ComponentType.ReadOnly<global::Game.Vehicles.Vehicle>(),
                    ComponentType.ReadOnly<global::Game.Creatures.Creature>(),
                },
            });

            _liveEdges = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Edge>(),
                    ComponentType.ReadOnly<Curve>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Owner>(),
                    ComponentType.ReadOnly<Deleted>(),
                },
            });

            if (Mod.Service != null)
            {
                _observer = new CommandObserver(_incoming, ObjectDeleteCommand.Id, NetDeleteCommand.Id);
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

        private void DrainQueue()
        {
            SyncInbox.Clear(_incoming);
            _replayEdgeDeletes.Clear();
            _objectRetry.Clear();
            _edgeRetry.Clear();
            DeferNetForTerrain = false;
        }

        protected override void OnUpdate()
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
            bool netBusy = DeferNetForTerrain || _netSync == null || !_netSync.CanBuildDefinitions;
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
