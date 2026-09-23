using System.Collections.Generic;
using Game.Areas;
using Game.Common;
using Game.Prefabs;
using Game.Tools;
using Unity.Entities;
using Unity.Mathematics;
using Colossal.Mathematics;
using CS2MultiplayerMod.Core.Sync;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.Infrastructure;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Replicates street, district, line and building names. Typed names live in the game's naming
    /// system and are found by a 1 Hz diff. Auto-names are per-machine random draws, so the host's
    /// draw is published and republished whenever a street's edge set changes. Names are cosmetic: a
    /// missing target is dropped, never escalated.
    /// </summary>
    public partial class NameSyncSystem : CommandSyncSystem
    {
        private const long ScanIntervalMs = 1000;
        private const long RetryIntervalMs = 500;
        private const long TargetRetryWindowMs = 15000;
        private const int MaxPendingTargets = 512;

        // A street regroups over several frames; publish once it stands still.
        private const long AutoNameSettleMs = 400;

        /// <summary>How long a receiver keeps defending an applied draw against its own regrouping.</summary>
        private const long AutoNameHoldMs = 20000;

        private const long PublishedPruneIntervalMs = 10000;

        // A street is found through a point on one of its edges; the tolerance absorbs float noise
        // and keeps a bridge above from answering.
        private const float StreetSearchRadius = 8f;
        private const float StreetTolXZ = 4f;
        private const float StreetTolY = 4f;

        // Districts differ more: their centroid moves when redrawn.
        private const float ObjectSearchRadius = 8f;
        private const float ObjectMatchDistance = 4f;
        private const float RouteMatchDistance = 16f;
        private const float DistrictMatchDistance = 500f;

        private readonly LatestTargetRetryQueue<string, (EntityNameCommand cmd, int origin)> _targetRetry =
            new LatestTargetRetryQueue<string, (EntityNameCommand, int)>(MaxPendingTargets, TargetRetryWindowMs);

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

        /// <summary>
        /// An adopted auto-name draw, held for <see cref="AutoNameHoldMs"/>; keyed by street so a newer draw
        /// replaces it, and by anchor so it survives a merge.
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

            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _prefabIndex = new PrefabIndex(_prefabSystem, GetEntityQuery(ComponentType.ReadOnly<PrefabData>()));
            _nameSystem = World.GetOrCreateSystemManaged<global::Game.UI.NameSystem>();
            _netSearch = World.GetOrCreateSystemManaged<global::Game.Net.SearchSystem>();
            _objectSearch = new ObjectSearch(
                World.GetOrCreateSystemManaged<global::Game.Objects.SearchSystem>());

            // Entities with a typed name; the marker keeps this as small as the number of renames.
            _namedEntities = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<global::Game.UI.CustomName, PrefabRef>(),
                None = SyncQuery.ReadOnly<Temp, Deleted>(),
            });

            // Published whenever the edge set changes: which aggregate survives a merge is per machine.
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

            // Districts never merge, so their draw travels once; loaded worlds are not tagged Created.
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

            ListenFor(new[] { EntityNameCommand.Id }, EntityNameCommand.MaxEncodedBytes);
        }

        protected override void DrainQueue()
        {
            if (!_incoming.IsEmpty) SyncInbox.Clear(_incoming);
            if (_targetRetry.Count > 0) _targetRetry.Clear();
            if (_autoHold.Count > 0) _autoHold.Clear();
            if (_dirtyStreets.Count > 0) _dirtyStreets.Clear();
            // A replaced world: prime again instead of reporting everything as renamed.
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
                    // Queued during a load is already in that world.
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
            // Citizens, vehicles and animals are spawned per machine and have no shared identity.
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
        /// A street's identity: the midpoint of its first edge by position, order-independent because
        /// each machine builds the edge list from a different end.
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
