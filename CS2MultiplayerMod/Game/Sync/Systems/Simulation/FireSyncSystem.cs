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
    /// (<see cref="GateLocalIgnitions"/>) and applies the host's. How a fire ends is just as local: the fire
    /// engines run in each machine's own traffic, so one machine saves a house another lets burn down. The
    /// host therefore also reports each fire's size and damage while it burns and whether it burned its
    /// target down; a client destroys nothing by fire on its own. The flames and the engines still run
    /// natively everywhere.
    /// </summary>
    public partial class FireSyncSystem : CommandSyncSystem, IRealizeStage
    {
        private const int MaxRealizePerFrame = 32;
        private const int MaxRetries = 256;

        /// <summary>A target the host burns but this machine lacks may still be arriving.</summary>
        private const long RetryWindowMs = 5000;

        /// <summary>Buildings and trees do not move; this only absorbs float noise.</summary>
        private const float MatchRadius = 2f;

        /// <summary>Per target: how often the host reports a fire that keeps changing.</summary>
        private const long UpdateIntervalMs = 1000;

        /// <summary>Per target: an unchanged fire is re-stated, relighting one a client's engines put out.</summary>
        private const long KeepAliveMs = 10000;

        /// <summary>Spreads the reports of a large wildfire over frames.</summary>
        private const int MaxUpdatesPerFrame = 32;

        /// <summary>Smaller changes wait for the keep-alive.</summary>
        private const float IntensityStep = 2f;
        private const float DamageStep = 0.01f;

        private struct Burning
        {
            public string Prefab;
            public float3 Position;
            public string EventPrefab;
            public long EventKey;

            /// <summary>What the last report said, and when it went out.</summary>
            public float Intensity;
            public float3 Damage;
            public long SentMs;
            public bool BurnedDown;

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
        private readonly HashSet<Entity> _ownDestroys = new HashSet<Entity>();
        private long _droppedLocal;
        private long _droppedBurnDowns;

        private PrefabIndex _prefabIndex;
        private ObjectSearch _objectSearch;
        private EntityArchetype _igniteArchetype;
        private EntityArchetype _destroyArchetype;
        private EntityQuery _burningQuery;
        private EntityQuery _liveEvents;
        private EntityQuery _createdFires;
        private EntityQuery _ignites;
        private EntityQuery _destroys;

        protected override void OnCreate()
        {
            base.OnCreate();

            _prefabIndex = new PrefabIndex(World.GetOrCreateSystemManaged<PrefabSystem>(),
                GetEntityQuery(ComponentType.ReadOnly<PrefabData>()));
            _objectSearch = new ObjectSearch(World.GetOrCreateSystemManaged<global::Game.Objects.SearchSystem>());
            _igniteArchetype = EntityManager.CreateArchetype(
                ComponentType.ReadWrite<global::Game.Common.Event>(), ComponentType.ReadWrite<Ignite>());
            _destroyArchetype = EntityManager.CreateArchetype(
                ComponentType.ReadWrite<global::Game.Common.Event>(),
                ComponentType.ReadWrite<global::Game.Objects.Destroy>());

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
            _destroys = GetEntityQuery(ComponentType.ReadOnly<global::Game.Objects.Destroy>(),
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
                CaptureFires(service.Session, service.NowMs);
            }
        }

        // ---- Host ---------------------------------------------------------------

        private void CaptureFires(MultiplayerSession session, long now)
        {
            if (_burning.Count == 0 && _burningQuery.IsEmptyIgnoreFilter) return;

            _pass++;
            int updates = MaxUpdatesPerFrame;
            NativeArray<Entity> burning = _burningQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < burning.Length; i++)
                {
                    Entity entity = burning[i];
                    OnFire fire = EntityManager.GetComponentData<OnFire>(entity);
                    if (_burning.TryGetValue(entity, out Burning known))
                    {
                        known.Pass = _pass;
                        Report(session, entity, fire.m_Intensity, ref known, now, ref updates);
                        _burning[entity] = known;
                        continue;
                    }

                    // Zero is a fire going out, and a fire without its event is zeroed by the burn itself.
                    if (fire.m_Intensity <= 0f || fire.m_Event == Entity.Null ||
                        !EntityManager.Exists(fire.m_Event) || !EntityManager.HasComponent<PrefabRef>(fire.m_Event))
                        continue;

                    string targetPrefab = _prefabIndex.NameOf(EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab);
                    string eventPrefab = _prefabIndex.NameOf(EntityManager.GetComponentData<PrefabRef>(fire.m_Event).m_Prefab);
                    if (string.IsNullOrEmpty(targetPrefab) || string.IsNullOrEmpty(eventPrefab)) continue;

                    var tracked = new Burning
                    {
                        Prefab = targetPrefab,
                        Position = EntityManager.GetComponentData<global::Game.Objects.Transform>(entity).m_Position,
                        EventPrefab = eventPrefab,
                        EventKey = ((long)fire.m_Event.Index << 32) | (uint)fire.m_Event.Version,
                        Pass = _pass,
                    };
                    float3 damage = ReadDamage(entity);
                    if (!Send(session, Command(FireOp.Ignite, tracked, fire.m_Intensity, damage))) continue;
                    Remember(ref tracked, fire.m_Intensity, damage, now);

                    // Already rubble (a spread into a ruin): say so, in case this client's copy still stands.
                    if (EntityManager.HasComponent<Destroyed>(entity))
                    {
                        tracked.BurnedDown = true;
                        Send(session, Command(FireOp.BurnDown, tracked, fire.m_Intensity, damage));
                    }
                    _burning[entity] = tracked;
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
                Entity entity = _ended[i];
                Burning ended = _burning[entity];
                _burning.Remove(entity);

                float3 damage = ended.Damage;
                if (EntityManager.Exists(entity) && !EntityManager.HasComponent<Deleted>(entity))
                {
                    damage = ReadDamage(entity);
                    // Burned down in the same update that put the fire out.
                    if (!ended.BurnedDown && EntityManager.HasComponent<Destroyed>(entity))
                        Send(session, Command(FireOp.BurnDown, ended, 0f, damage));
                }
                Send(session, Command(FireOp.Extinguish, ended, 0f, damage));
            }
        }

        /// <summary>A fire still burning: a burn-down at once, its size and damage at a bounded rate.</summary>
        private void Report(MultiplayerSession session, Entity entity, float intensity, ref Burning known,
            long now, ref int updates)
        {
            if (!known.BurnedDown && EntityManager.HasComponent<Destroyed>(entity))
            {
                known.BurnedDown = true;
                float3 ruin = ReadDamage(entity);
                if (Send(session, Command(FireOp.BurnDown, known, intensity, ruin)))
                    Remember(ref known, intensity, ruin, now);
                return;
            }

            if (updates <= 0 || now - known.SentMs < UpdateIntervalMs) return;
            float3 damage = ReadDamage(entity);
            bool changed = math.abs(intensity - known.Intensity) >= IntensityStep ||
                           math.cmax(math.abs(damage - known.Damage)) >= DamageStep ||
                           (intensity <= 0f) != (known.Intensity <= 0f);
            if (!changed && now - known.SentMs < KeepAliveMs) return;

            updates--;
            if (Send(session, Command(FireOp.Update, known, intensity, damage)))
                Remember(ref known, intensity, damage, now);
        }

        private static void Remember(ref Burning burning, float intensity, float3 damage, long now)
        {
            burning.Intensity = intensity;
            burning.Damage = damage;
            burning.SentMs = now;
        }

        private static FireCommand Command(FireOp op, Burning burning, float intensity, float3 damage) =>
            new FireCommand
            {
                Op = op,
                TargetPrefab = burning.Prefab,
                X = burning.Position.x,
                Y = burning.Position.y,
                Z = burning.Position.z,
                EventPrefab = burning.EventPrefab,
                EventKey = burning.EventKey,
                Intensity = math.clamp(intensity, 0f, FireCommand.MaxIntensity),
                DamageX = damage.x,
                DamageY = damage.y,
                DamageZ = damage.z,
            };

        /// <summary>The target's damage, zero when it has none; clamped to what the wire accepts.</summary>
        private float3 ReadDamage(Entity entity)
        {
            if (!EntityManager.HasComponent<global::Game.Objects.Damaged>(entity)) return float3.zero;
            float3 damage = EntityManager.GetComponentData<global::Game.Objects.Damaged>(entity).m_Damage;
            return new float3(CleanDamage(damage.x), CleanDamage(damage.y), CleanDamage(damage.z));
        }

        private static float CleanDamage(float value) =>
            float.IsNaN(value) ? 0f : math.clamp(value, 0f, FireCommand.MaxDamage);

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
            // Updates repeat every second per fire; the edges are what a report needs.
            if (command.Op != FireOp.Update)
                SyncLog.Detail(LogTopic.City, "FireSync sent " + command.Op + " of '" + command.TargetPrefab +
                    "' at (" + command.X + ", " + command.Y + ", " + command.Z + ").");
            return true;
        }

        // ---- Client -------------------------------------------------------------

        /// <summary>
        /// Called by <see cref="FireIgniteGateSystem"/> just before IgniteSystem. On a client every building or
        /// tree ignition this machine drew itself is emptied, a fire event it rolled is deleted, and so is a
        /// burn-down its own fire decided.
        /// </summary>
        public void GateLocalIgnitions()
        {
            MultiplayerService service = Mod.Service;
            if (service != null && service.GameplaySyncReady && service.Session.Role == SessionRole.Client)
            {
                DropLocalFireEvents();
                DropLocalIgnites();
                DropLocalBurnDowns();
            }
            _ownIgnites.Clear();
            _ownEvents.Clear();
            _ownDestroys.Clear();
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

        /// <summary>
        /// A client's fire burns at its own pace and under its own engines, so whether it destroys its target
        /// is the host's call (<see cref="FireOp.BurnDown"/>). Runs at ToolUpdate, ahead of every modification
        /// phase, for the burn-downs the previous frame's simulation queued, and again before IgniteSystem for
        /// any queued since. The event is removed whole; nothing else refers to it yet.
        /// </summary>
        private void DropLocalBurnDowns()
        {
            if (_destroys.IsEmptyIgnoreFilter) return;
            NativeArray<Entity> destroys = _destroys.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < destroys.Length; i++)
                {
                    Entity entity = destroys[i];
                    if (_ownDestroys.Contains(entity) || !EntityManager.Exists(entity)) continue;
                    if (!IsFireBurnDown(EntityManager.GetComponentData<global::Game.Objects.Destroy>(entity)))
                        continue;

                    EntityManager.DestroyEntity(entity);
                    if (++_droppedBurnDowns == 1 || _droppedBurnDowns % 100 == 0)
                        SyncLog.Detail(LogTopic.City, "FireSync: kept a building or tree this machine's own fire " +
                            "would have destroyed (" + _droppedBurnDowns + " so far); the host decides burn-downs.");
                }
            }
            finally
            {
                destroys.Dispose();
            }
        }

        /// <summary>A destruction by fire of a building or tree; vehicles keep their local fires.</summary>
        private bool IsFireBurnDown(global::Game.Objects.Destroy destroy)
        {
            Entity target = destroy.m_Object;
            if (target == Entity.Null || !EntityManager.Exists(target) ||
                EntityManager.HasComponent<global::Game.Vehicles.Vehicle>(target)) return false;
            if (EntityManager.HasComponent<OnFire>(target)) return true;
            Entity cause = destroy.m_Event;
            return cause != Entity.Null && EntityManager.Exists(cause) &&
                   EntityManager.HasComponent<global::Game.Events.Fire>(cause);
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

            // Before this frame's own burn-downs exist, so none of them can be mistaken for local.
            DropLocalBurnDowns();

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

        /// <summary>False only while the target is missing; a missed update is superseded within seconds.</summary>
        private bool Realize(FireCommand command, NativeList<Entity> candidates)
        {
            if (!_prefabIndex.TryResolve(command.TargetPrefab, out Entity targetPrefab)) return true;
            Entity target = FindTarget(targetPrefab, new float3(command.X, command.Y, command.Z), candidates);
            if (target == Entity.Null) return command.Op == FireOp.Update;

            ApplyDamage(target, command);
            switch (command.Op)
            {
                case FireOp.Extinguish:
                    PutOut(target, command);
                    break;
                case FireOp.BurnDown:
                    BurnDown(target, command);
                    break;
                default:
                    Burn(target, command);
                    break;
            }
            return true;
        }

        private void PutOut(Entity target, FireCommand command)
        {
            if (!EntityManager.HasComponent<OnFire>(target)) return;
            // The burn removes a zero fire on its next update, the same way as one the engines put out.
            OnFire fire = EntityManager.GetComponentData<OnFire>(target);
            fire.m_Intensity = 0f;
            EntityManager.SetComponentData(target, fire);
            SyncLog.Detail(LogTopic.City, "FireSync: put out '" + command.TargetPrefab + "' as the host did.");
        }

        /// <summary>
        /// Ignite lights the target unless it already burns; Update pins a burning fire to the host's size, and
        /// relights one this machine's engines put out while the host's still burns.
        /// </summary>
        private void Burn(Entity target, FireCommand command)
        {
            if (EntityManager.HasComponent<OnFire>(target))
            {
                OnFire fire = EntityManager.GetComponentData<OnFire>(target);
                if (fire.m_Intensity > 0f)
                {
                    if (command.Op == FireOp.Update && fire.m_Intensity != command.Intensity)
                    {
                        fire.m_Intensity = command.Intensity;
                        EntityManager.SetComponentData(target, fire);
                    }
                    return;
                }
            }

            // The host's is going out as well.
            if (command.Intensity <= 0f) return;
            if (!_prefabIndex.TryResolve(command.EventPrefab, out Entity eventPrefab)) return;

            Entity fireEvent = LocalEvent(command.EventKey, eventPrefab);
            if (fireEvent == Entity.Null)
            {
                // Its initialization ignites the target with the prefab's start intensity, as on the host.
                if (CreateFireEvent(eventPrefab, target, out fireEvent))
                    _localEvents[command.EventKey] = fireEvent;
                return;
            }

            Entity ignite = EntityManager.CreateEntity(_igniteArchetype);
            EntityManager.SetComponentData(ignite, new Ignite
            {
                m_Target = target,
                m_Event = fireEvent,
                m_Intensity = command.Intensity,
            });
            _ownIgnites.Add(ignite);
            SyncLog.Detail(LogTopic.City, "FireSync: " + (command.Op == FireOp.Update ? "relit '" : "ignited '") +
                command.TargetPrefab + "' as the host did.");
        }

        /// <summary>
        /// The host's fire destroyed the target: the same destruction event the burn raises, so the native
        /// collapse, evacuation and notification follow.
        /// </summary>
        private void BurnDown(Entity target, FireCommand command)
        {
            if (EntityManager.HasComponent<Destroyed>(target)) return;

            Entity fireEvent = EntityManager.HasComponent<OnFire>(target)
                ? EntityManager.GetComponentData<OnFire>(target).m_Event
                : Entity.Null;
            if ((fireEvent == Entity.Null || !EntityManager.Exists(fireEvent)) &&
                _prefabIndex.TryResolve(command.EventPrefab, out Entity eventPrefab))
                fireEvent = LocalEvent(command.EventKey, eventPrefab);

            Entity destroy = EntityManager.CreateEntity(_destroyArchetype);
            EntityManager.SetComponentData(destroy, new global::Game.Objects.Destroy
            {
                m_Object = target,
                m_Event = fireEvent,
            });
            _ownDestroys.Add(destroy);
            SyncLog.Detail(LogTopic.City, "FireSync: '" + command.TargetPrefab + "' burned down, as on the host.");
        }

        /// <summary>
        /// The host's damage, so this machine's copy looks and counts the same. Only an existing record is
        /// written: the local burn adds it, with what goes with it, once the fire does damage here.
        /// </summary>
        private void ApplyDamage(Entity target, FireCommand command)
        {
            if (!EntityManager.HasComponent<global::Game.Objects.Damaged>(target)) return;
            var host = new float3(command.DamageX, command.DamageY, command.DamageZ);
            global::Game.Objects.Damaged damaged = EntityManager.GetComponentData<global::Game.Objects.Damaged>(target);
            if (math.all(damaged.m_Damage == host)) return;
            damaged.m_Damage = host;
            EntityManager.SetComponentData(target, damaged);
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
