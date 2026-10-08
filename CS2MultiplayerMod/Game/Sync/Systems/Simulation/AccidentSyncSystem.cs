using System.Collections.Generic;
using Colossal.Collections;
using Colossal.Mathematics;
using Game.Common;
using Game.Events;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.Infrastructure;
namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Host-authoritative traffic accidents. Like fires, which road gets an accident is drawn per machine
    /// from a wall-clock seed, so every player had different crashes. A client now drops the accidents it
    /// rolled itself before they are initialized and stages the host's on the same road instead. The crash
    /// then runs natively with this machine's own traffic: which cars collide, the police response and the
    /// clean-up stay local, as vehicles are not replicated.
    /// </summary>
    public partial class AccidentSyncSystem : CommandSyncSystem, IRealizeStage
    {
        private const int MaxRealizePerFrame = 8;
        private const int MaxRetries = 64;

        /// <summary>The host's road may still be arriving.</summary>
        private const long RetryWindowMs = 5000;

        /// <summary>Box around the host's road midpoint searched in the net tree.</summary>
        private static readonly float3 SearchExtent = new float3(4f, 4f, 4f);

        /// <summary>Roads are replicated exactly; this only absorbs float noise.</summary>
        private const float MatchDistance = 2f;

        private struct Pending
        {
            public AccidentCommand Command;
            public long Deadline;
        }

        // Host: sent while still Created, so the ModificationEnd pass skips what ToolUpdate already sent.
        private readonly HashSet<Entity> _sent = new HashSet<Entity>();

        // Client.
        private readonly List<Pending> _retry = new List<Pending>();
        private readonly HashSet<Entity> _ownEvents = new HashSet<Entity>();
        private long _droppedLocal;
        private long _slippedLocal;

        private readonly List<Entity> _roads = new List<Entity>();
        private readonly List<Entity> _settled = new List<Entity>();

        private PrefabIndex _prefabIndex;
        private global::Game.Net.SearchSystem _netSearch;
        private EntityQuery _createdAccidents;

        protected override void OnCreate()
        {
            base.OnCreate();

            _prefabIndex = new PrefabIndex(World.GetOrCreateSystemManaged<PrefabSystem>(),
                GetEntityQuery(ComponentType.ReadOnly<PrefabData>()));
            _netSearch = World.GetOrCreateSystemManaged<global::Game.Net.SearchSystem>();

            _createdAccidents = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<global::Game.Common.Event, global::Game.Events.TrafficAccident,
                    PrefabRef, Created>(),
                None = SyncQuery.ReadOnly<Deleted, Temp>(),
            });

            ListenFor(new[] { AccidentCommand.Id }, AccidentCommand.MaxEncodedBytes);
        }

        /// <summary>
        /// ModificationEnd. Host: an accident whose road was only chosen during initialization. Client: one that
        /// was rolled after the ToolUpdate gate, only reported (it is already under way).
        /// </summary>
        protected override void OnUpdate()
        {
            using (Diagnostics.SyncProfiler.Measure("AccidentSync"))
            {
                MultiplayerService service = Mod.Service;
                if (service != null && service.GameplaySyncReady)
                {
                    MultiplayerSession session = service.Session;
                    if (session.Role == SessionRole.Host) CaptureAccidents(session, final: true);
                    else if (session.Role == SessionRole.Client) ReportSlippedAccidents();
                }
                // Kept while still Created: a replica must never be taken for a local roll, nor an accident
                // sent twice.
                ForgetSettled(_sent);
                ForgetSettled(_ownEvents);
            }
        }

        private void ForgetSettled(HashSet<Entity> events)
        {
            if (events.Count == 0) return;
            _settled.Clear();
            foreach (Entity entity in events)
                if (!EntityManager.Exists(entity) || !EntityManager.HasComponent<Created>(entity))
                    _settled.Add(entity);
            for (int i = 0; i < _settled.Count; i++) events.Remove(_settled[i]);
            _settled.Clear();
        }

        /// <summary>
        /// ToolUpdate, before event initialization: the host reports what its game just rolled, a client drops
        /// what its own game rolled and creates the host's accidents, which initialize later this frame.
        /// </summary>
        public void RealizePending()
        {
            MultiplayerService service = Mod.Service;
            if (service == null) return;
            if (!service.GameplaySyncReady)
            {
                DrainQueue();
                return;
            }

            MultiplayerSession session = service.Session;
            if (session.Role == SessionRole.Host)
            {
                SyncInbox.Clear(_incoming);
                CaptureAccidents(session, final: false);
                return;
            }
            if (session.Role != SessionRole.Client)
            {
                DrainQueue();
                return;
            }

            DropLocalAccidents();

            long now = service.NowMs;
            int budget = MaxRealizePerFrame;
            var candidates = new NativeList<Entity>(16, Allocator.Temp);
            try
            {
                for (int i = 0; i < _retry.Count && budget > 0;)
                {
                    budget--;
                    Pending pending = _retry[i];
                    bool done = Realize(pending.Command, candidates);
                    if (!done && pending.Deadline >= now)
                    {
                        i++;
                        continue;
                    }
                    if (!done)
                        SyncLog.Detail(LogTopic.City, "AccidentSync: none of the host's roads for '" +
                            pending.Command.EventPrefab + "' exists here; dropping the accident.");
                    _retry.RemoveAt(i);
                }

                while (budget > 0 && _incoming.TryDequeue(out SimulationCommandMessage message))
                {
                    // Accidents are the host's alone; a relayed client copy is never applied.
                    if (message.OriginPlayerId != MultiplayerSession.HostPlayerId) continue;
                    if (!CommandDecode.TryDecode(message, AccidentCommand.Decode, LogTopic.City, "AccidentSync",
                            out AccidentCommand command)) continue;

                    budget--;
                    if (Realize(command, candidates)) continue;
                    if (_retry.Count >= MaxRetries) _retry.RemoveAt(0);
                    _retry.Add(new Pending { Command = command, Deadline = now + RetryWindowMs });
                }
            }
            finally
            {
                candidates.Dispose();
            }
        }

        // ---- Host ---------------------------------------------------------------

        /// <param name="final">
        /// ModificationEnd: the last frame the accident is Created. Before initialization its road may not be
        /// chosen yet, so ToolUpdate leaves such an accident for this pass.
        /// </param>
        private void CaptureAccidents(MultiplayerSession session, bool final)
        {
            if (_createdAccidents.IsEmptyIgnoreFilter) return;

            NativeArray<Entity> accidents = _createdAccidents.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < accidents.Length; i++)
                {
                    Entity accident = accidents[i];
                    if (_sent.Contains(accident)) continue;

                    AccidentCommand command = Describe(accident);
                    if (command == null)
                    {
                        if (final)
                            SyncLog.Detail(LogTopic.City, "AccidentSync: an accident of '" +
                                _prefabIndex.NameOf(EntityManager.GetComponentData<PrefabRef>(accident).m_Prefab) +
                                "' names no road; the other players do not get it.");
                        continue;
                    }

                    _sent.Add(accident);
                    Send(session, command);
                }
            }
            finally
            {
                accidents.Dispose();
            }
        }

        /// <summary>The accident's prefab and the road under each of its targets; null when there is none.</summary>
        private AccidentCommand Describe(Entity accident)
        {
            string eventPrefab = _prefabIndex.NameOf(EntityManager.GetComponentData<PrefabRef>(accident).m_Prefab);
            if (string.IsNullOrEmpty(eventPrefab) || !EntityManager.HasBuffer<TargetElement>(accident)) return null;

            _roads.Clear();
            DynamicBuffer<TargetElement> targets = EntityManager.GetBuffer<TargetElement>(accident, true);
            for (int i = 0; i < targets.Length && _roads.Count < AccidentCommand.MaxRoads; i++)
            {
                Entity road = RoadOf(targets[i].m_Entity);
                if (road != Entity.Null && !_roads.Contains(road)) _roads.Add(road);
            }
            if (_roads.Count == 0) return null;

            var command = new AccidentCommand { EventPrefab = eventPrefab };
            for (int i = 0; i < _roads.Count; i++)
            {
                Entity road = _roads[i];
                string roadPrefab = _prefabIndex.NameOf(EntityManager.GetComponentData<PrefabRef>(road).m_Prefab);
                if (string.IsNullOrEmpty(roadPrefab)) continue;
                float3 midpoint = MathUtils.Position(
                    EntityManager.GetComponentData<global::Game.Net.Curve>(road).m_Bezier, 0.5f);
                command.Roads.Add(new AccidentCommand.Road
                {
                    Prefab = roadPrefab,
                    X = midpoint.x,
                    Y = midpoint.y,
                    Z = midpoint.z,
                });
            }
            return command.Roads.Count > 0 ? command : null;
        }

        /// <summary>A road segment, or the segment owning a lane; vehicles are local and name nothing.</summary>
        private Entity RoadOf(Entity target)
        {
            if (IsEdge(target)) return target;
            if (target == Entity.Null || !EntityManager.Exists(target) ||
                !EntityManager.HasComponent<Owner>(target)) return Entity.Null;
            Entity owner = EntityManager.GetComponentData<Owner>(target).m_Owner;
            return IsEdge(owner) ? owner : Entity.Null;
        }

        private bool IsEdge(Entity entity) =>
            entity != Entity.Null && EntityManager.Exists(entity) &&
            EntityManager.HasComponent<global::Game.Net.Edge>(entity) &&
            EntityManager.HasComponent<global::Game.Net.Curve>(entity) &&
            EntityManager.HasComponent<PrefabRef>(entity) &&
            !EntityManager.HasComponent<Temp>(entity) && !EntityManager.HasComponent<Deleted>(entity);

        private static void Send(MultiplayerSession session, AccidentCommand command)
        {
            try
            {
                session.SendCommand(0, AccidentCommand.Id, command.Encode());
            }
            catch (ProtocolException ex)
            {
                SyncLog.Warn(LogTopic.City, "AccidentSync: not sending '" + command.EventPrefab + "': " + ex.Message);
                return;
            }
            AccidentCommand.Road first = command.Roads[0];
            SyncLog.Detail(LogTopic.City, "AccidentSync sent '" + command.EventPrefab + "' on '" + first.Prefab +
                "' at (" + first.X + ", " + first.Y + ", " + first.Z + ").");
        }

        // ---- Client -------------------------------------------------------------

        /// <summary>
        /// This machine's own rolls, removed whole before initialization stages them: nothing refers to them
        /// yet. The host's arrive as commands.
        /// </summary>
        private void DropLocalAccidents()
        {
            if (_createdAccidents.IsEmptyIgnoreFilter) return;

            NativeArray<Entity> accidents = _createdAccidents.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < accidents.Length; i++)
                {
                    if (_ownEvents.Contains(accidents[i])) continue;
                    EntityManager.DestroyEntity(accidents[i]);
                    if (++_droppedLocal == 1 || _droppedLocal % 50 == 0)
                        SyncLog.Detail(LogTopic.City, "AccidentSync: dropped this machine's own traffic accidents (" +
                            _droppedLocal + " so far); the host's are staged instead.");
                }
            }
            finally
            {
                accidents.Dispose();
            }
        }

        /// <summary>A local accident created after the gate is already under way; it is only counted.</summary>
        private void ReportSlippedAccidents()
        {
            if (_createdAccidents.IsEmptyIgnoreFilter) return;

            NativeArray<Entity> accidents = _createdAccidents.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < accidents.Length; i++)
                {
                    if (_ownEvents.Contains(accidents[i])) continue;
                    if (++_slippedLocal == 1)
                        SyncLog.Warn(LogTopic.City, "AccidentSync: a traffic accident this game rolled itself " +
                            "started before it could be dropped; it may show only for this player.");
                    else if (_slippedLocal % 50 == 0)
                        SyncLog.Detail(LogTopic.City, "AccidentSync: " + _slippedLocal +
                            " local traffic accidents started before they could be dropped.");
                }
            }
            finally
            {
                accidents.Dispose();
            }
        }

        /// <summary>False while none of the host's roads is found here yet.</summary>
        private bool Realize(AccidentCommand command, NativeList<Entity> candidates)
        {
            if (!_prefabIndex.TryResolve(command.EventPrefab, out Entity prefab) ||
                !EntityManager.HasComponent<EventData>(prefab))
            {
                SyncLog.Warn(LogTopic.City, "AccidentSync: no local accident event named '" + command.EventPrefab +
                    "'; ignoring the host's accident.");
                return true;
            }

            _roads.Clear();
            for (int i = 0; i < command.Roads.Count; i++)
            {
                Entity road = FindRoad(command.Roads[i], candidates);
                if (road != Entity.Null && !_roads.Contains(road)) _roads.Add(road);
            }
            if (_roads.Count == 0) return false;

            Entity accident = EntityManager.CreateEntity(EntityManager.GetComponentData<EventData>(prefab).m_Archetype);
            if (!EntityManager.HasComponent<global::Game.Events.TrafficAccident>(accident) ||
                !EntityManager.HasComponent<PrefabRef>(accident) || !EntityManager.HasBuffer<TargetElement>(accident))
            {
                EntityManager.DestroyEntity(accident);
                SyncLog.Warn(LogTopic.City, "AccidentSync: '" + command.EventPrefab +
                    "' is not a traffic accident here; ignoring the host's accident.");
                return true;
            }

            // Created with the archetype's Created tag: initialization stages it on these roads, as on the host.
            EntityManager.SetComponentData(accident, new PrefabRef(prefab));
            DynamicBuffer<TargetElement> targets = EntityManager.GetBuffer<TargetElement>(accident);
            for (int i = 0; i < _roads.Count; i++) targets.Add(new TargetElement(_roads[i]));
            _ownEvents.Add(accident);

            AccidentCommand.Road first = command.Roads[0];
            SyncLog.Detail(LogTopic.City, "AccidentSync: staged the host's '" + command.EventPrefab + "' on '" +
                first.Prefab + "' at (" + first.X + ", " + first.Z + ").");
            return true;
        }

        /// <summary>The road segment of that prefab whose midpoint matches the host's.</summary>
        private Entity FindRoad(AccidentCommand.Road road, NativeList<Entity> candidates)
        {
            if (!_prefabIndex.TryResolve(road.Prefab, prefab => EntityManager.HasComponent<NetData>(prefab),
                    out Entity roadPrefab)) return Entity.Null;

            var anchor = new float3(road.X, road.Y, road.Z);
            NativeQuadTree<Entity, QuadTreeBoundsXZ> tree =
                _netSearch.GetNetSearchTree(readOnly: true, out JobHandle dependencies);
            dependencies.Complete();

            candidates.Clear();
            var iterator = new Bounds3Collector
            {
                Bounds = new Bounds3(anchor - SearchExtent, anchor + SearchExtent),
                Results = candidates,
            };
            tree.Iterate(ref iterator);

            Entity best = Entity.Null;
            float bestDistance = MatchDistance;
            for (int i = 0; i < candidates.Length; i++)
            {
                // Tree entries are only as fresh as its last update, so start from existence.
                Entity edge = candidates[i];
                if (!IsEdge(edge) || EntityManager.GetComponentData<PrefabRef>(edge).m_Prefab != roadPrefab) continue;
                float distance = math.distance(anchor, MathUtils.Position(
                    EntityManager.GetComponentData<global::Game.Net.Curve>(edge).m_Bezier, 0.5f));
                if (distance > bestDistance) continue;
                best = edge;
                bestDistance = distance;
            }
            return best;
        }

        protected override void DrainQueue()
        {
            SyncInbox.Clear(_incoming);
            _retry.Clear();
            _sent.Clear();
            _ownEvents.Clear();
        }
    }
}
