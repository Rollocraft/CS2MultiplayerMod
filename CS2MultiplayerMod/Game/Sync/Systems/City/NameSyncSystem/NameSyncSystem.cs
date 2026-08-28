using System.Collections.Concurrent;
using System.Collections.Generic;
using Game;
using Game.Areas;
using Game.Common;
using Game.Prefabs;
using Game.Tools;
using Unity.Entities;
using Unity.Mathematics;
using Colossal.Mathematics;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.Infrastructure;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Replicates what things are called: street, district, transport-line and building names.
    ///
    /// Two separate mechanisms produce a name. A player's typed name is held by the game's naming
    /// system, which keeps it outside the entity itself - so it is detected by a 1 Hz diff of that
    /// system's own lookup and applied through it. An untouched entity is instead named from its
    /// prefab's name list by an index drawn when the entity appears, from a seed that differs per
    /// machine: a road built on one machine got a different name on the other. The host's draw is
    /// the city's, and is republished whenever a street's edge set changes - a street is an
    /// aggregate, and aggregates are created, merged and dropped again as roads are drawn.
    ///
    /// Names are cosmetic, so a target that never appears is dropped with a warning rather than
    /// escalated to a world resync.
    /// </summary>
    public partial class NameSyncSystem : GameSystemBase
    {
        private const long ScanIntervalMs = 1000;
        private const long RetryIntervalMs = 500;
        private const long TargetRetryWindowMs = 15000;
        private const int MaxPendingTargets = 512;

        // A street regroups for several frames while an operation's courses keep arriving, so its
        // draw is published only once it has stood still - one command per street per gesture
        // instead of one per intermediate grouping.
        private const long AutoNameSettleMs = 400;

        /// <summary>How long a receiver keeps defending an applied draw against its own regrouping.</summary>
        private const long AutoNameHoldMs = 20000;

        private const long PublishedPruneIntervalMs = 10000;

        // A street's identity on the wire is a point on one of its edges, so resolving it means
        // finding that edge. Same-edge geometry is identical on both machines; the tolerance only
        // absorbs float noise and keeps a road stacked above another (bridge) from answering.
        private const float StreetSearchRadius = 8f;
        private const float StreetTolXZ = 4f;
        private const float StreetTolY = 4f;

        // Placed objects sit at the same position on every machine; districts can differ far more,
        // because their centroid moves whenever the polygon is redrawn.
        private const float ObjectSearchRadius = 8f;
        private const float ObjectMatchDistance = 4f;
        private const float RouteMatchDistance = 16f;
        private const float DistrictMatchDistance = 500f;

        private readonly ConcurrentQueue<SimulationCommandMessage> _incoming =
            new ConcurrentQueue<SimulationCommandMessage>();
        private readonly List<(EntityNameCommand cmd, int origin, long deadline)> _targetRetry =
            new List<(EntityNameCommand, int, long)>();

        /// <summary>Last observed typed name per entity - the baseline the 1 Hz diff works against.</summary>
        private readonly Dictionary<Entity, string> _knownNames = new Dictionary<Entity, string>();
        private readonly HashSet<Entity> _seen = new HashSet<Entity>();
        private readonly List<Entity> _dropped = new List<Entity>();
        private bool _primed;
        private long _lastScanMs;
        private long _lastRetryMs;

        /// <summary>Streets whose edge set changed, and when they will have stood still long enough.</summary>
        private readonly Dictionary<Entity, long> _dirtyStreets = new Dictionary<Entity, long>();
        private readonly List<Entity> _dirtyDue = new List<Entity>();

        /// <summary>What each street was last published as; suppresses re-sending an unchanged one.</summary>
        private readonly Dictionary<Entity, string> _publishedAuto = new Dictionary<Entity, string>();
        private readonly List<Entity> _publishedDead = new List<Entity>();
        private readonly List<AutoNameHold> _autoHold = new List<AutoNameHold>();
        private readonly HashSet<Entity> _heldTargets = new HashSet<Entity>();
        private bool _autoBaselined;
        private long _lastPruneMs;

        private PrefabSystem _prefabSystem;
        private PrefabIndex _prefabIndex;
        private global::Game.UI.NameSystem _nameSystem;
        private global::Game.Net.SearchSystem _netSearch;
        private ObjectSearch _objectSearch;
        private EntityQuery _namedEntities;
        private EntityQuery _changedStreets;
        private EntityQuery _streets;
        private EntityQuery _createdDistricts;
        private EntityQuery _districts;
        private EntityQuery _routes;
        private CommandObserver _observer;

        /// <summary>
        /// An auto-name draw this machine adopted, kept until <see cref="AutoNameHoldMs"/> passes.
        /// The street it was written to is remembered so a newer draw for the same street replaces
        /// it instead of fighting it, and the anchor so the hold survives that street being merged
        /// away.
        /// </summary>
        private sealed class AutoNameHold
        {
            public Entity Target;
            public byte Kind;
            public string PrefabName;
            public float3 Anchor;
            public int[] Indices;
            public long Deadline;
        }

        protected override void OnCreate()
        {
            base.OnCreate();

            Mod.log.Info(nameof(NameSyncSystem) + " ready.");
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _prefabIndex = new PrefabIndex(_prefabSystem, GetEntityQuery(ComponentType.ReadOnly<PrefabData>()));
            _nameSystem = World.GetOrCreateSystemManaged<global::Game.UI.NameSystem>();
            _netSearch = World.GetOrCreateSystemManaged<global::Game.Net.SearchSystem>();
            _objectSearch = new ObjectSearch(
                World.GetOrCreateSystemManaged<global::Game.Objects.SearchSystem>());

            // Everything that carries a typed name, whatever kind it is. The marker component is
            // what the game adds alongside the name itself, so this query stays as small as the
            // number of things a player has actually renamed.
            _namedEntities = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<global::Game.UI.CustomName, PrefabRef>(),
                None = SyncQuery.ReadOnly<Temp, Deleted>(),
            });

            // A street's draw is published whenever its edge set changes, not only when the street
            // first appears: an aggregate that swallows another keeps its own draw, and which of the
            // two survives is decided per machine. Updated covers both cases - the archetype a new
            // aggregate is built from carries it alongside Created.
            _changedStreets = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<Updated, global::Game.Net.Aggregate,
                    RandomLocalizationIndex, PrefabRef>(),
                None = SyncQuery.ReadOnly<Temp, Deleted>(),
            });
            _streets = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<global::Game.Net.Aggregate, RandomLocalizationIndex,
                    PrefabRef>(),
                None = SyncQuery.ReadOnly<Temp, Deleted>(),
            });

            // Districts are never merged, so their draw still travels from the one frame the area
            // appears. Loading a world does not tag entities Created, so a join never re-broadcasts.
            _createdDistricts = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<Created, District, RandomLocalizationIndex, Node, PrefabRef>(),
                None = SyncQuery.ReadOnly<Temp, Deleted>(),
            });

            _districts = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<District, Node, PrefabRef>(),
                None = SyncQuery.ReadOnly<Temp, Deleted>(),
            });
            _routes = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<global::Game.Routes.Route, PrefabRef>(),
                None = SyncQuery.ReadOnly<Temp, Deleted>(),
            });

            if (Mod.Service != null)
            {
                _observer = new CommandObserver(_incoming, EntityNameCommand.Id);
                _observer.MaxBodyBytes = EntityNameCommand.MaxEncodedBytes;
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
            if (!_incoming.IsEmpty) SyncInbox.Clear(_incoming);
            if (_targetRetry.Count > 0) _targetRetry.Clear();
            if (_autoHold.Count > 0) _autoHold.Clear();
            if (_dirtyStreets.Count > 0) _dirtyStreets.Clear();
            // A replaced world invalidates every entity the baseline holds; the next scan primes
            // against the installed one instead of reporting all of it as renamed.
            if (_knownNames.Count > 0) _knownNames.Clear();
            if (_publishedAuto.Count > 0) _publishedAuto.Clear();
            _primed = false;
            _autoBaselined = false;
        }

        protected override void OnUpdate()
        {
            using (Diagnostics.SyncProfiler.Measure("NameSync"))
            {
                MultiplayerService service = Mod.Service;
                if (service == null) return;

                MultiplayerSession session = service.Session;
                if (!service.GameplaySyncReady)
                {
                    // Anything queued while a world is loading is already part of that world; holding it
                    // would only fill the inbox until it overflowed.
                    DrainQueue();
                    return;
                }

                long now = service.NowMs;
                ApplyIncoming(session, now);
                CaptureAutoNames(session, now);

                if (now - _lastPruneMs >= PublishedPruneIntervalMs)
                {
                    _lastPruneMs = now;
                    PrunePublished();
                }

                if (now - _lastScanMs < ScanIntervalMs) return;
                _lastScanMs = now;
                ScanCustomNames(session);
            }
        }

        /// <summary>Which of the four wire identities this entity is named through.</summary>
        private bool TryClassify(Entity entity, out byte kind)
        {
            if (EntityManager.HasComponent<global::Game.Net.Aggregate>(entity))
            {
                kind = EntityNameCommand.KindStreet;
                return true;
            }
            if (EntityManager.HasComponent<District>(entity))
            {
                kind = EntityNameCommand.KindDistrict;
                return true;
            }
            if (EntityManager.HasComponent<global::Game.Routes.Route>(entity))
            {
                kind = EntityNameCommand.KindRoute;
                return true;
            }
            // Static excludes citizens, vehicles and animals: they can be renamed too, but the
            // simulation spawns them independently on each machine, so nothing identifies them
            // across the wire.
            if (EntityManager.HasComponent<global::Game.Objects.Transform>(entity) &&
                EntityManager.HasComponent<global::Game.Objects.Static>(entity))
            {
                kind = EntityNameCommand.KindObject;
                return true;
            }
            kind = 0;
            return false;
        }

        private bool TryIdentify(Entity entity, out byte kind, out string prefabName, out float3 anchor)
        {
            kind = 0;
            prefabName = null;
            anchor = default(float3);
            if (entity == Entity.Null || !EntityManager.Exists(entity) ||
                !EntityManager.HasComponent<PrefabRef>(entity)) return false;
            if (!TryClassify(entity, out kind)) return false;

            prefabName = _prefabIndex.NameOf(
                EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab);
            if (string.IsNullOrEmpty(prefabName)) return false;
            return TryAnchor(kind, entity, out anchor);
        }

        /// <summary>Cross-machine identity per kind (entity ids differ per machine).</summary>
        private bool TryAnchor(byte kind, Entity entity, out float3 anchor)
        {
            anchor = default(float3);
            switch (kind)
            {
                case EntityNameCommand.KindStreet:
                    return TryStreetAnchor(entity, out anchor);
                case EntityNameCommand.KindDistrict:
                {
                    if (!EntityManager.HasBuffer<Node>(entity)) return false;
                    DynamicBuffer<Node> nodes = EntityManager.GetBuffer<Node>(entity, true);
                    if (nodes.Length == 0) return false;
                    float3 sum = float3.zero;
                    for (int i = 0; i < nodes.Length; i++) sum += nodes[i].m_Position;
                    anchor = sum / nodes.Length;
                    anchor.y = 0f;
                    return true;
                }
                case EntityNameCommand.KindRoute:
                {
                    if (!EntityManager.HasBuffer<global::Game.Routes.RouteWaypoint>(entity)) return false;
                    DynamicBuffer<global::Game.Routes.RouteWaypoint> waypoints =
                        EntityManager.GetBuffer<global::Game.Routes.RouteWaypoint>(entity, true);
                    if (waypoints.Length == 0 ||
                        !EntityManager.HasComponent<global::Game.Routes.Position>(
                            waypoints[0].m_Waypoint)) return false;
                    anchor = EntityManager.GetComponentData<global::Game.Routes.Position>(
                        waypoints[0].m_Waypoint).m_Position;
                    return true;
                }
                default:
                {
                    if (!EntityManager.HasComponent<global::Game.Objects.Transform>(entity)) return false;
                    anchor = EntityManager.GetComponentData<global::Game.Objects.Transform>(entity)
                        .m_Position;
                    return true;
                }
            }
        }

        /// <summary>
        /// A street is a road aggregate: a set of edges with no geometry of its own. Its identity is
        /// therefore a point on one of those edges - the midpoint of the edge that sorts first by
        /// position. The choice has to be order-independent because the aggregate's own edge list is
        /// built by walking the road from whichever end it grew from, which differs per machine.
        /// </summary>
        private bool TryStreetAnchor(Entity aggregate, out float3 anchor)
        {
            anchor = default(float3);
            if (!EntityManager.HasBuffer<global::Game.Net.AggregateElement>(aggregate)) return false;

            DynamicBuffer<global::Game.Net.AggregateElement> elements =
                EntityManager.GetBuffer<global::Game.Net.AggregateElement>(aggregate, true);
            bool found = false;
            for (int i = 0; i < elements.Length; i++)
            {
                Entity edge = elements[i].m_Edge;
                if (edge == Entity.Null || !EntityManager.Exists(edge) ||
                    !EntityManager.HasComponent<global::Game.Net.Curve>(edge) ||
                    EntityManager.HasComponent<Deleted>(edge)) continue;

                float3 midpoint = MathUtils.Position(
                    EntityManager.GetComponentData<global::Game.Net.Curve>(edge).m_Bezier, 0.5f);
                if (found && !SortsFirst(midpoint, anchor)) continue;
                anchor = midpoint;
                found = true;
            }
            return found;
        }

        private static bool SortsFirst(float3 candidate, float3 current)
        {
            if (candidate.x != current.x) return candidate.x < current.x;
            if (candidate.z != current.z) return candidate.z < current.z;
            return candidate.y < current.y;
        }

        private static string KindName(byte kind) =>
            kind == EntityNameCommand.KindStreet ? "street" :
            kind == EntityNameCommand.KindDistrict ? "district" :
            kind == EntityNameCommand.KindRoute ? "line" : "object";
    }
}
