using System.Collections.Generic;
using Game;
using Game.Common;
using Game.Events;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
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
    /// Host-authoritative fires. Which building or tree catches fire is drawn per machine from a wall-clock
    /// seed - starts, spreads and lightning alike - so a client drops its own ignitions
    /// (<see cref="GateLocalIgnitions"/>) and applies the host's. The host reports every building or tree
    /// that starts or stops burning; the burn, its damage and the fire engines run natively everywhere.
    /// </summary>
    public partial class FireSyncSystem : CommandSyncSystem, IRealizeStage
    {
        private const int MaxRealizePerFrame = 32;
        private const int MaxRetries = 256;

        /// <summary>A target the host burns but this machine lacks may still be arriving.</summary>
        private const long RetryWindowMs = 5000;

        /// <summary>Buildings and trees do not move; this only absorbs float noise.</summary>
        private const float MatchRadius = 2f;

        private struct Burning
        {
            public string Prefab;
            public float3 Position;
            public int Pass;
        }

        private struct Pending
        {
            public FireCommand Command;
            public long Deadline;
        }

        // Host: what the last scan saw burning.
        private readonly Dictionary<Entity, Burning> _burning = new Dictionary<Entity, Burning>();
        private readonly List<Entity> _ended = new List<Entity>();
        private int _pass;

        // Client.
        private readonly List<Pending> _retry = new List<Pending>();
        private readonly Dictionary<long, Entity> _localEvents = new Dictionary<long, Entity>();
        private readonly HashSet<Entity> _ownIgnites = new HashSet<Entity>();
        private readonly HashSet<Entity> _ownEvents = new HashSet<Entity>();
        private long _droppedLocal;

        private PrefabIndex _prefabIndex;
        private ObjectSearch _objectSearch;
        private EntityArchetype _igniteArchetype;
        private EntityQuery _burningQuery;
        private EntityQuery _liveEvents;
        private EntityQuery _createdFires;
        private EntityQuery _ignites;

        protected override void OnCreate()
        {
            base.OnCreate();

            _prefabIndex = new PrefabIndex(World.GetOrCreateSystemManaged<PrefabSystem>(),
                GetEntityQuery(ComponentType.ReadOnly<PrefabData>()));
            _objectSearch = new ObjectSearch(World.GetOrCreateSystemManaged<global::Game.Objects.SearchSystem>());
            _igniteArchetype = EntityManager.CreateArchetype(
                ComponentType.ReadWrite<global::Game.Common.Event>(), ComponentType.ReadWrite<Ignite>());

            // Vehicles burn after accidents; they are local on every machine and keep their own fires.
            _burningQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<OnFire, PrefabRef, global::Game.Objects.Transform>(),
                None = SyncQuery.ReadOnly<global::Game.Vehicles.Vehicle, Deleted, Temp>(),
            });
            _liveEvents = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<global::Game.Events.Event, PrefabRef>(),
                None = SyncQuery.ReadOnly<Deleted, Temp>(),
            });
            _createdFires = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<global::Game.Events.Event, global::Game.Events.Fire, Created>(),
                None = SyncQuery.ReadOnly<Deleted>(),
            });
            _ignites = GetEntityQuery(ComponentType.ReadWrite<Ignite>(),
                ComponentType.ReadOnly<global::Game.Common.Event>());

            ListenFor(new[] { FireCommand.Id }, FireCommand.MaxEncodedBytes);
        }

        /// <summary>ModificationEnd: IgniteSystem has put this frame's fires on their targets.</summary>
        protected override void OnUpdate()
        {
            using (Diagnostics.SyncProfiler.Measure("FireSync"))
            {
                MultiplayerService service = Mod.Service;
                if (service == null || !service.GameplaySyncReady || service.Session.Role != SessionRole.Host)
                {
                    if (_burning.Count > 0) _burning.Clear();
                    return;
                }
                CaptureFires(service.Session);
            }
        }

        // ---- Host ---------------------------------------------------------------

        private void CaptureFires(MultiplayerSession session)
        {
            if (_burning.Count == 0 && _burningQuery.IsEmptyIgnoreFilter) return;

            _pass++;
            NativeArray<Entity> burning = _burningQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < burning.Length; i++)
                {
                    Entity entity = burning[i];
                    if (_burning.TryGetValue(entity, out Burning known))
                    {
                        known.Pass = _pass;
                        _burning[entity] = known;
                        continue;
                    }

                    OnFire fire = EntityManager.GetComponentData<OnFire>(entity);
                    // Zero is a fire going out, and a fire without its event is zeroed by the burn itself.
                    if (fire.m_Intensity <= 0f || fire.m_Event == Entity.Null ||
                        !EntityManager.Exists(fire.m_Event) || !EntityManager.HasComponent<PrefabRef>(fire.m_Event))
                        continue;

                    string targetPrefab = _prefabIndex.NameOf(EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab);
                    string eventPrefab = _prefabIndex.NameOf(EntityManager.GetComponentData<PrefabRef>(fire.m_Event).m_Prefab);
                    if (string.IsNullOrEmpty(targetPrefab) || string.IsNullOrEmpty(eventPrefab)) continue;

                    float3 position = EntityManager.GetComponentData<global::Game.Objects.Transform>(entity).m_Position;
                    var command = new FireCommand
                    {
                        Op = FireOp.Ignite,
                        TargetPrefab = targetPrefab,
                        X = position.x,
                        Y = position.y,
                        Z = position.z,
                        EventPrefab = eventPrefab,
                        EventKey = ((long)fire.m_Event.Index << 32) | (uint)fire.m_Event.Version,
                        Intensity = math.clamp(fire.m_Intensity, 0f, FireCommand.MaxIntensity),
                    };
                    if (!Send(session, command)) continue;
                    _burning[entity] = new Burning { Prefab = targetPrefab, Position = position, Pass = _pass };
                }
            }
            finally
            {
                burning.Dispose();
            }

            _ended.Clear();
            foreach (KeyValuePair<Entity, Burning> pair in _burning)
                if (pair.Value.Pass != _pass) _ended.Add(pair.Key);
            for (int i = 0; i < _ended.Count; i++)
            {
                Burning ended = _burning[_ended[i]];
                _burning.Remove(_ended[i]);
                Send(session, new FireCommand
                {
                    Op = FireOp.Extinguish,
                    TargetPrefab = ended.Prefab,
                    X = ended.Position.x,
                    Y = ended.Position.y,
                    Z = ended.Position.z,
                });
            }
        }

        private static bool Send(MultiplayerSession session, FireCommand command)
        {
            try
            {
                session.SendCommand(0, FireCommand.Id, command.Encode());
            }
            catch (ProtocolException ex)
            {
                SyncLog.Warn(LogTopic.City, "FireSync: not sending " + command.Op + " of '" +
                    command.TargetPrefab + "': " + ex.Message);
                return false;
            }
            SyncLog.Detail(LogTopic.City, "FireSync sent " + command.Op + " of '" + command.TargetPrefab +
                "' at (" + command.X + ", " + command.Y + ", " + command.Z + ").");
            return true;
        }

        // ---- Client -------------------------------------------------------------

        /// <summary>
        /// Called by <see cref="FireIgniteGateSystem"/> just before IgniteSystem. On a client every building or
        /// tree ignition this machine drew itself is emptied, and a fire event it rolled is deleted.
        /// </summary>
        public void GateLocalIgnitions()
        {
            MultiplayerService service = Mod.Service;
            if (service != null && service.GameplaySyncReady && service.Session.Role == SessionRole.Client)
            {
                DropLocalFireEvents();
                DropLocalIgnites();
            }
            _ownIgnites.Clear();
            _ownEvents.Clear();
        }

        private void DropLocalFireEvents()
        {
            if (_createdFires.IsEmptyIgnoreFilter) return;
            NativeArray<Entity> events = _createdFires.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < events.Length; i++)
                    if (!_ownEvents.Contains(events[i])) EntityManager.AddComponent<Deleted>(events[i]);
            }
            finally
            {
                events.Dispose();
            }
        }

        private void DropLocalIgnites()
        {
            if (_ignites.IsEmptyIgnoreFilter) return;
            NativeArray<Entity> ignites = _ignites.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < ignites.Length; i++)
                {
                    Entity entity = ignites[i];
                    if (_ownIgnites.Contains(entity)) continue;
                    Ignite ignite = EntityManager.GetComponentData<Ignite>(entity);
                    // A fire event realized this frame: its initialization is the host's ignition.
                    if (_ownEvents.Contains(ignite.m_Event)) continue;
                    if (ignite.m_Target == Entity.Null || !EntityManager.Exists(ignite.m_Target) ||
                        EntityManager.HasComponent<global::Game.Vehicles.Vehicle>(ignite.m_Target)) continue;

                    // IgniteSystem skips a target without a PrefabRef.
                    ignite.m_Target = Entity.Null;
                    EntityManager.SetComponentData(entity, ignite);
                    if (++_droppedLocal == 1 || _droppedLocal % 100 == 0)
                        SyncLog.Detail(LogTopic.City, "FireSync: dropped this machine's own ignitions (" +
                            _droppedLocal + " so far); the host's fires are applied instead.");
                }
            }
            finally
            {
                ignites.Dispose();
            }
        }

        /// <summary>ToolUpdate, so a fire event created here is initialized later this frame.</summary>
        public void RealizePending()
        {
            MultiplayerService service = Mod.Service;
            if (service == null) return;
            if (!service.GameplaySyncReady || service.Session.Role != SessionRole.Client)
            {
                DrainQueue();
                return;
            }

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
                        SyncLog.Detail(LogTopic.City, "FireSync: no '" + pending.Command.TargetPrefab +
                            "' at (" + pending.Command.X + ", " + pending.Command.Z + ") here; dropping its " +
                            pending.Command.Op + ".");
                    _retry.RemoveAt(i);
                }

                while (budget > 0 && _incoming.TryDequeue(out SimulationCommandMessage message))
                {
                    // Fires are the host's alone; a relayed client copy is never applied.
                    if (message.OriginPlayerId != MultiplayerSession.HostPlayerId) continue;
                    if (!CommandDecode.TryDecode(message, FireCommand.Decode, LogTopic.City, "FireSync",
                            out FireCommand command)) continue;

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

        /// <summary>False only while the target is missing.</summary>
        private bool Realize(FireCommand command, NativeList<Entity> candidates)
        {
            if (!_prefabIndex.TryResolve(command.TargetPrefab, out Entity targetPrefab)) return true;
            Entity target = FindTarget(targetPrefab, new float3(command.X, command.Y, command.Z), candidates);
            if (target == Entity.Null) return false;

            bool burning = EntityManager.HasComponent<OnFire>(target);
            if (command.Op == FireOp.Extinguish)
            {
                if (!burning) return true;
                // The burn removes a zero fire on its next update, the same way as one the engines put out.
                OnFire fire = EntityManager.GetComponentData<OnFire>(target);
                fire.m_Intensity = 0f;
                EntityManager.SetComponentData(target, fire);
                SyncLog.Detail(LogTopic.City, "FireSync: put out '" + command.TargetPrefab + "' as the host did.");
                return true;
            }

            if (burning && EntityManager.GetComponentData<OnFire>(target).m_Intensity > 0f) return true;
            if (!_prefabIndex.TryResolve(command.EventPrefab, out Entity eventPrefab)) return true;

            Entity fireEvent = LocalEvent(command.EventKey, eventPrefab);
            if (fireEvent == Entity.Null)
            {
                // Its initialization ignites the target with the prefab's start intensity, as on the host.
                if (CreateFireEvent(eventPrefab, target, out fireEvent))
                    _localEvents[command.EventKey] = fireEvent;
                return true;
            }

            Entity ignite = EntityManager.CreateEntity(_igniteArchetype);
            EntityManager.SetComponentData(ignite, new Ignite
            {
                m_Target = target,
                m_Event = fireEvent,
                m_Intensity = command.Intensity,
            });
            _ownIgnites.Add(ignite);
            SyncLog.Detail(LogTopic.City, "FireSync: ignited '" + command.TargetPrefab + "' as the host did.");
            return true;
        }

        private Entity FindTarget(Entity prefab, float3 wanted, NativeList<Entity> candidates)
        {
            _objectSearch.CollectNear(wanted, MatchRadius, candidates);
            Entity best = Entity.Null;
            float bestDistance = MatchRadius * MatchRadius;
            for (int i = 0; i < candidates.Length; i++)
            {
                Entity candidate = candidates[i];
                if (!EntityManager.Exists(candidate) || !EntityManager.HasComponent<PrefabRef>(candidate) ||
                    !EntityManager.HasComponent<global::Game.Objects.Transform>(candidate) ||
                    EntityManager.HasComponent<Deleted>(candidate) || EntityManager.HasComponent<Temp>(candidate) ||
                    EntityManager.GetComponentData<PrefabRef>(candidate).m_Prefab != prefab) continue;
                float distance = math.distancesq(
                    EntityManager.GetComponentData<global::Game.Objects.Transform>(candidate).m_Position, wanted);
                if (distance > bestDistance) continue;
                best = candidate;
                bestDistance = distance;
            }
            return best;
        }

        /// <summary>
        /// The event this client made for the host's, or else any live event of that prefab: a joiner loads
        /// the host's burning fires, and a storm's lightning belongs to the storm replicated here.
        /// </summary>
        private Entity LocalEvent(long hostKey, Entity prefab)
        {
            if (_localEvents.TryGetValue(hostKey, out Entity known) && EntityManager.Exists(known) &&
                !EntityManager.HasComponent<Deleted>(known) && EntityManager.HasComponent<PrefabRef>(known) &&
                EntityManager.GetComponentData<PrefabRef>(known).m_Prefab == prefab)
                return known;

            NativeArray<Entity> events = _liveEvents.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < events.Length; i++)
                {
                    if (EntityManager.GetComponentData<PrefabRef>(events[i]).m_Prefab != prefab) continue;
                    _localEvents[hostKey] = events[i];
                    return events[i];
                }
            }
            finally
            {
                events.Dispose();
            }
            return Entity.Null;
        }

        /// <summary>
        /// A new fire event aimed at <paramref name="target"/>, as FireHazardSystem creates one. Only a fire can
        /// be started this way; recreating a storm for its lightning would be a new disaster.
        /// </summary>
        private bool CreateFireEvent(Entity prefab, Entity target, out Entity fireEvent)
        {
            fireEvent = Entity.Null;
            if (!EntityManager.HasComponent<EventData>(prefab) || !EntityManager.HasComponent<FireData>(prefab) ||
                EntityManager.HasComponent<WeatherPhenomenonData>(prefab)) return false;

            Entity entity = EntityManager.CreateEntity(EntityManager.GetComponentData<EventData>(prefab).m_Archetype);
            if (!EntityManager.HasComponent<global::Game.Events.Fire>(entity) || !EntityManager.HasComponent<PrefabRef>(entity) ||
                !EntityManager.HasBuffer<TargetElement>(entity))
            {
                EntityManager.DestroyEntity(entity);
                return false;
            }
            EntityManager.SetComponentData(entity, new PrefabRef(prefab));
            EntityManager.GetBuffer<TargetElement>(entity).Add(new TargetElement(target));
            _ownEvents.Add(entity);
            fireEvent = entity;
            SyncLog.Detail(LogTopic.City, "FireSync: started the host's fire on '" +
                _prefabIndex.NameOf(EntityManager.GetComponentData<PrefabRef>(target).m_Prefab) + "'.");
            return true;
        }

        protected override void DrainQueue()
        {
            SyncInbox.Clear(_incoming);
            _retry.Clear();
            _localEvents.Clear();
        }
    }

    /// <summary>Runs <see cref="FireSyncSystem.GateLocalIgnitions"/> right before IgniteSystem.</summary>
    public partial class FireIgniteGateSystem : GameSystemBase
    {
        private FireSyncSystem _fireSync;

        protected override void OnCreate()
        {
            base.OnCreate();
            _fireSync = World.GetOrCreateSystemManaged<FireSyncSystem>();
        }

        protected override void OnUpdate() => _fireSync.GateLocalIgnitions();
    }
}
