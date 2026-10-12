using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Unity.Entities;
using Unity.Mathematics;

using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.Infrastructure;
namespace CS2MultiplayerMod.Game.Sync.Systems.Net
{
    public partial class NetSyncSystem
    {
        // Ceiling for fallback searches; native-intent commands use an anchor instead.
        private const float MaxEndpointSearch = 64f;

        /// <summary>Fallback accept radius from both widths and the snap distance.</summary>
        private static float SnapRadius(NetPrefabInfo placed, float targetHalf, float floor) =>
            math.max(floor, placed.HalfWidth + targetHalf + placed.SnapDistance);

        /// <summary>
        /// Native rule: each prefab's required layers must be in the other's connect layers (not a mask
        /// intersection).
        /// </summary>
        private static bool LayersCanConnect(NetPrefabInfo placed, NetPrefabInfo target) =>
            (placed.RequiredLayers & target.ConnectLayers) == placed.RequiredLayers ||
            (target.RequiredLayers & placed.ConnectLayers) == target.RequiredLayers;

        /// <summary>
        /// Nearest standalone node within the snap radius, ranked in XZ; nodes beyond
        /// <see cref="VerticalSnapTol"/> are another level. Skips a node whose edges are all being deleted
        /// this frame: reusing it makes ApplyNetSystem dereference a stale entity and crash natively.
        /// </summary>
        private Entity FindNodeAt(float3 position, NetPrefabInfo placedInfo, ref NodePool nodes)
        {
            float2 p = position.xz;
            float bestSq = MaxEndpointSearch * MaxEndpointSearch;
            Entity best = Entity.Null;
            NetCellIndex.Enumerator candidates = nodes.Index.Near(p, MaxEndpointSearch);
            while (candidates.MoveNext())
            {
                int i = candidates.Current;
                float dSq = math.distancesq(p, nodes.Data[i].m_Position.xz);
                if (dSq >= bestSq) continue;
                if (math.abs(nodes.Data[i].m_Position.y - position.y) > VerticalSnapTol) continue; // other level
                Entity entity = nodes.Entities[i];
                NetPrefabInfo targetInfo = default;
                if (EntityManager.HasComponent<PrefabRef>(entity))
                    targetInfo = NetInfoOf(EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab);
                float radius = SnapRadius(placedInfo, NodeHalfWidth(entity, targetInfo.HalfWidth), NodeSnapDistance);
                if (dSq >= radius * radius) continue;
                if (!LayersCanConnect(placedInfo, targetInfo)) continue;
                if (IsNodeBeingDeleted(entity)) continue;
                bestSq = dSq;
                best = entity;
            }
            return best;
        }

        /// <summary>
        /// <see cref="FindNodeAt"/> over owned nodes, for utility nets only: a building's connector stub
        /// the line was drawn onto. Roads never match, so driveways stay untouchable.
        /// </summary>
        private Entity FindUtilityNodeAt(float3 position, ref NodePool owned,
            NetPrefabInfo placedInfo)
        {
            float2 p = position.xz;
            float bestSq = MaxEndpointSearch * MaxEndpointSearch;
            Entity best = Entity.Null;
            NetCellIndex.Enumerator candidates = owned.Index.Near(p, MaxEndpointSearch);
            while (candidates.MoveNext())
            {
                int i = candidates.Current;
                float dSq = math.distancesq(p, owned.Data[i].m_Position.xz);
                if (dSq >= bestSq) continue;
                if (math.abs(owned.Data[i].m_Position.y - position.y) > VerticalSnapTol) continue;
                Entity entity = owned.Entities[i];
                NetPrefabInfo info = NetInfoOf(
                    EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab);
                float radius = SnapRadius(placedInfo,
                    NodeHalfWidth(entity, info.HalfWidth), NodeSnapDistance);
                if (dSq >= radius * radius) continue;
                if (!LayersCanConnect(placedInfo, info) ||
                    ((info.ConnectLayers | info.RequiredLayers) &
                     (placedInfo.ConnectLayers | placedInfo.RequiredLayers) & UtilityConnectLayers) == Layer.None)
                    continue;
                if (IsNodeBeingDeleted(entity)) continue;
                bestSq = dSq;
                best = entity;
            }
            return best;
        }

        /// <summary>Cached native connection/width facts for one net prefab.</summary>
        private NetPrefabInfo NetInfoOf(Entity prefab)
        {
            if (_netInfoCache.TryGetValue(prefab, out NetPrefabInfo info)) return info;
            if (EntityManager.HasComponent<NetData>(prefab))
            {
                NetData net = EntityManager.GetComponentData<NetData>(prefab);
                info.RequiredLayers = net.m_RequiredLayers;
                info.ConnectLayers = net.m_ConnectLayers;
            }
            if (EntityManager.HasComponent<PlaceableNetData>(prefab))
            {
                PlaceableNetData placeable = EntityManager.GetComponentData<PlaceableNetData>(prefab);
                info.SnapDistance = math.max(placeable.m_SnapDistance, 1f);
                info.HasElevationRange = true;
                info.ElevationRangeMin = placeable.m_ElevationRange.min;
                info.ElevationRangeMax = placeable.m_ElevationRange.max;
            }
            if (EntityManager.HasComponent<NetGeometryData>(prefab))
            {
                NetGeometryData geometry = EntityManager.GetComponentData<NetGeometryData>(prefab);
                info.HalfWidth = geometry.m_DefaultWidth * 0.5f;
                info.ElevationLimit = geometry.m_ElevationLimit;
                info.MaxSlopeSteepness = geometry.m_MaxSlopeSteepness;
                info.RequireElevated =
                    (geometry.m_Flags & global::Game.Net.GeometryFlags.RequireElevated) != 0;
            }
            _netInfoCache[prefab] = info;
            return info;
        }

        private float EdgeHalfWidth(Entity edge, float fallback)
        {
            if (EntityManager.HasComponent<Composition>(edge))
            {
                Composition composition = EntityManager.GetComponentData<Composition>(edge);
                if (composition.m_Edge != Entity.Null && EntityManager.HasComponent<NetCompositionData>(composition.m_Edge))
                    return EntityManager.GetComponentData<NetCompositionData>(composition.m_Edge).m_Width * 0.5f;
            }
            return fallback;
        }

        private float NodeHalfWidth(Entity node, float fallback)
        {
            float width = fallback;
            if (!EntityManager.HasBuffer<ConnectedEdge>(node)) return width;
            DynamicBuffer<ConnectedEdge> edges = EntityManager.GetBuffer<ConnectedEdge>(node, isReadOnly: true);
            for (int i = 0; i < edges.Length; i++)
            {
                Entity edge = edges[i].m_Edge;
                if (!EntityManager.Exists(edge) || EntityManager.HasComponent<Deleted>(edge)) continue;
                width = math.max(width, EdgeHalfWidth(edge, fallback));
            }
            return width;
        }

        /// <summary>
        /// The height a free-height elevation is measured from, as course splitting does: terrain, or the
        /// water surface plus bridge clearance where deep enough. Tunnel ends ignore water.
        /// </summary>
        private static float SurfaceHeightAt(float3 p, float elevation, float elevationLimit,
            ref TerrainHeightData heightData, ref WaterSurfaceData<SurfaceWater> waterData)
        {
            WaterUtils.SampleHeight(ref waterData, ref heightData, p, out float terrain, out float water,
                out float depth);
            water = depth < 0.2f ? terrain : water + elevationLimit * 2f;
            return elevation < -1f ? terrain : math.max(terrain, water);
        }

        /// <summary>
        /// Projects an endpoint's relative elevation onto the local surface for connection lookup only.
        /// <paramref name="anyLayer"/> extends it past utilities to roads and rails on the last-resort pass,
        /// since surfaces drift beyond <see cref="VerticalSnapTol"/> between machines. Prefab, layers, owner
        /// and direction must still match.
        /// </summary>
        private bool TryProjectEndpointToLocalSurface(Entity prefab, NetPrefabInfo placedInfo,
            float3 sourcePoint, float2 sourceElevation, bool anyLayer,
            ref TerrainHeightData heightData, ref WaterSurfaceData<SurfaceWater> waterData,
            out float3 projected)
        {
            projected = sourcePoint;
            Layer utilityLayers = placedInfo.RequiredLayers | placedInfo.ConnectLayers;
            if (!anyLayer && (utilityLayers & UtilityConnectLayers) == Layer.None) return false;

            float surface = SurfaceHeightAt(sourcePoint, sourceElevation.x,
                NetInfoOf(prefab).ElevationLimit, ref heightData, ref waterData);
            float localY = surface + sourceElevation.x;
            if (!math.isfinite(localY) || math.abs(localY - sourcePoint.y) <= VerticalSnapTol)
                return false;

            projected.y = localY;
            return true;
        }

        /// <summary>
        /// The elevation an endpoint carries. A reused node keeps its own. A fixed-height endpoint keeps
        /// the transmitted value, which also selects the terrain or water profile; only
        /// <see cref="CoursePosFlags.FreeHeight"/> endpoints are recomputed as local surface + elevation.
        /// </summary>
        private float2 EndElevation(Entity prefab, Entity snap, int kind, float3 sourcePoint,
            float2 sourceElevation, uint sourceFlags, ref TerrainHeightData heightData,
            ref WaterSurfaceData<SurfaceWater> waterData, out float correction)
        {
            correction = 0f;
            if ((kind == KindReuseNode || kind == KindReuseConnector) &&
                EntityManager.HasComponent<global::Game.Net.Elevation>(snap))
                return EntityManager.GetComponentData<global::Game.Net.Elevation>(snap).m_Elevation;

            bool freeHeight = (((CoursePosFlags)sourceFlags & CoursePosFlags.FreeHeight) != 0);
            if (!freeHeight)
                return sourceElevation;

            float surface = SurfaceHeightAt(sourcePoint, sourceElevation.x,
                NetInfoOf(prefab).ElevationLimit, ref heightData, ref waterData);
            float projected = sourcePoint.y - surface;
            correction = NetEndpointElevationPolicy.Correction(sourceElevation.x, projected,
                freeHeight, SurfaceAgreementTol);
            return sourceElevation + correction;
        }

        /// <summary>
        /// Had connected edges and none is live: a bulldoze is tearing it down. An empty buffer (an unused
        /// connector) stays reusable.
        /// </summary>
        private bool IsNodeBeingDeleted(Entity node)
        {
            if (!EntityManager.HasBuffer<ConnectedEdge>(node)) return false;
            DynamicBuffer<ConnectedEdge> edges = EntityManager.GetBuffer<ConnectedEdge>(node, isReadOnly: true);
            if (edges.Length == 0) return false;
            for (int i = 0; i < edges.Length; i++)
            {
                Entity e = edges[i].m_Edge;
                if (EntityManager.Exists(e) && !EntityManager.HasComponent<Deleted>(e)) return false;
            }
            return true; // every previously connected edge is gone/Deleted -> being torn down
        }

        /// <summary>
        /// Fallback course definition with each end reusing a node, splitting an edge at
        /// <paramref name="endT"/>, or free; always through Temp + ApplyTool.
        /// </summary>
        private Entity CreateCourse(Entity prefab, Bezier4x3 bez, float length,
            Entity startSnap, float startT, int startKind, Entity endSnap, float endT, int endKind,
            float2 startElevation, float2 endElevation, bool pinProfile,
            CoursePos? startShared, CoursePos? endShared)
        {
            // A stale or Deleted split target crashes ApplyNetSystem natively: fall back to a fresh node.
            if (CourseTargetIsStale(startSnap)) { startSnap = Entity.Null; startT = 0f; }
            if (CourseTargetIsStale(endSnap)) { endSnap = Entity.Null; endT = 0f; }

            // No free-height ends here; the pin only moves the two elevations.
            uint startFlags = 0u, endFlags = 0u;
            if (pinProfile)
                ApplyProfilePin(prefab,
                    ref startFlags, bez.a.y, startSnap, startKind, startT, ref startElevation,
                    ref endFlags, bez.d.y, endSnap, endKind, endT, ref endElevation);

            Entity definition = EntityManager.CreateEntity();
            bool completed = false;
            try
            {
                EntityManager.AddComponentData(definition, new CreationDefinition
                {
                    m_Prefab = prefab,
                    // Seeded from shared geometry so wear and props match on every machine.
                    m_RandomSeed = math.asint(bez.a.x) ^ math.asint(bez.a.z) ^ math.asint(bez.d.x) ^ math.asint(bez.d.z),
                    // The net tool's straight-line recipe (CreateStraightLine).
                    m_Flags = CreationFlags.SubElevation,
                });
                EntityManager.AddComponentData(definition, new NetCourse
                {
                    m_Curve = bez,
                    m_Length = length,
                    m_FixedIndex = -1,
                    m_StartPosition = new CoursePos
                    {
                        m_Entity = startSnap,
                        m_Position = bez.a,
                        // The net tool's GetNodeRotation; identity mis-connects.
                        m_Rotation = NetUtils.GetNodeRotation(MathUtils.Tangent(bez, 0f)),
                        // The only source of the committed node's elevation; without it an elevated net commits as ground.
                        m_Elevation = startElevation,
                        m_CourseDelta = 0f,
                        m_SplitPosition = startT,
                        // A non-parallel course occupies both sides, as CreateStraightLine sets.
                        m_Flags = CoursePosFlags.IsFirst | CoursePosFlags.IsLeft | CoursePosFlags.IsRight,
                        m_ParentMesh = -1, // free-standing road, no owning object (0 is a valid mesh index!)
                    },
                    m_EndPosition = new CoursePos
                    {
                        m_Entity = endSnap,
                        m_Position = bez.d,
                        m_Rotation = NetUtils.GetNodeRotation(MathUtils.Tangent(bez, 1f)),
                        m_Elevation = endElevation,
                        m_CourseDelta = 1f,
                        m_SplitPosition = endT,
                        m_Flags = CoursePosFlags.IsLast | CoursePosFlags.IsLeft | CoursePosFlags.IsRight,
                        m_ParentMesh = -1,
                    },
                });
                MergeBatchNodes(definition, startShared, endShared);
                EntityManager.AddComponent<Updated>(definition);
                EntityManager.AddComponent<Deleted>(definition);
                completed = true;
                return definition;
            }
            finally
            {
                if (!completed && EntityManager.Exists(definition)) EntityManager.DestroyEntity(definition);
            }
        }

        /// <summary>
        /// Temp-routed definition from captured native intent; elevations are passed in because they may
        /// carry a local surface correction.
        /// </summary>
        private Entity CreateNativeCourse(Entity prefab, NetPlacementCommand command, Bezier4x3 bez,
            Entity startSnap, float startT, int startKind, float2 startElevation,
            Entity endSnap, float endT, int endKind, float2 endElevation,
            CoursePos? startShared, CoursePos? endShared)
        {
            if (CourseTargetIsStale(startSnap)) startSnap = Entity.Null;
            if (CourseTargetIsStale(endSnap)) endSnap = Entity.Null;

            NetEndpointIntent start = command.Start;
            NetEndpointIntent end = command.End;
            if (command.PinProfile)
                ApplyProfilePin(prefab,
                    ref start.Flags, start.PosY, startSnap, startKind, startT, ref startElevation,
                    ref end.Flags, end.PosY, endSnap, endKind, endT, ref endElevation);

            CreationFlags flags = (CreationFlags)command.CreationFlags;

            float2 courseElevation =
                new float2(command.CourseElevationLeft, command.CourseElevationRight);

            Entity subPrefab = Entity.Null;
            if (!string.IsNullOrEmpty(command.SubPrefabName) &&
                !_prefabIndex.TryResolve(command.SubPrefabName, out subPrefab))
            {
                throw new System.InvalidOperationException("Unknown net sub-prefab '" +
                                                           command.SubPrefabName + "'.");
            }
            if (subPrefab != Entity.Null && !EntityManager.HasComponent<NetLaneData>(subPrefab))
            {
                throw new System.InvalidOperationException("Net sub-prefab '" +
                    command.SubPrefabName + "' is not a lane prefab.");
            }
            Entity definition = EntityManager.CreateEntity();
            bool completed = false;
            try
            {
                EntityManager.AddComponentData(definition, new CreationDefinition
                {
                    m_Prefab = prefab,
                    m_SubPrefab = subPrefab,
                    m_RandomSeed = command.RandomSeed,
                    m_Flags = flags,
                });
                EntityManager.AddComponentData(definition, new NetCourse
                {
                    m_Curve = bez,
                    m_Elevation = courseElevation,
                    m_Length = command.Length,
                    m_FixedIndex = command.FixedIndex,
                    m_StartPosition = MakeNativeCoursePos(start, startSnap, startT, startKind,
                        startElevation),
                    m_EndPosition = MakeNativeCoursePos(end, endSnap, endT, endKind,
                        endElevation),
                });
                MergeBatchNodes(definition, startShared, endShared);
                EntityManager.AddComponent<Updated>(definition);
                EntityManager.AddComponent<Deleted>(definition);
                completed = true;
                return definition;
            }
            finally
            {
                if (!completed && EntityManager.Exists(definition)) EntityManager.DestroyEntity(definition);
            }
        }

        private void MergeBatchNodes(Entity definition, CoursePos? startShared, CoursePos? endShared)
        {
            if (!startShared.HasValue && !endShared.HasValue) return;
            NetCourse course = EntityManager.GetComponentData<NetCourse>(definition);
            course.m_StartPosition = NetBatchNodes.Merge(course.m_StartPosition, startShared);
            course.m_EndPosition = NetBatchNodes.Merge(course.m_EndPosition, endShared);
            EntityManager.SetComponentData(definition, course);
        }

        private void RegisterBatchNodes(Entity definition, NetPrefabInfo info, int startKind,
            int endKind, NetBatchNodes nodes)
        {
            // Remember the actual generator inputs, not a reconstruction from the wire curve.
            NetCourse course = EntityManager.GetComponentData<NetCourse>(definition);
            if (startKind == KindFree)
                nodes.Add(course.m_StartPosition, (uint)info.RequiredLayers, (uint)info.ConnectLayers);
            if (endKind == KindFree)
                nodes.Add(course.m_EndPosition, (uint)info.RequiredLayers, (uint)info.ConnectLayers);
        }

        private static CoursePos MakeNativeCoursePos(NetEndpointIntent intent,
            Entity target, float resolvedT, int resolvedKind, float2 elevation)
        {
            return new CoursePos
            {
                m_Entity = target,
                m_Position = new float3(intent.PosX, intent.PosY, intent.PosZ),
                // Exact source bits; re-normalizing would change generation.
                m_Rotation = new quaternion(intent.RotX, intent.RotY, intent.RotZ, intent.RotW),
                m_Elevation = elevation,
                m_CourseDelta = intent.CourseDelta,
                m_SplitPosition = resolvedKind == KindSplit ? resolvedT : intent.SplitPosition,
                m_Flags = (CoursePosFlags)intent.Flags,
                m_ParentMesh = intent.ParentMesh,
            };
        }

        private bool CourseTargetIsStale(Entity target)
        {
            return target != Entity.Null &&
                   (!EntityManager.Exists(target) || EntityManager.HasComponent<Deleted>(target) ||
                    EntityManager.HasComponent<Node>(target) && IsNodeBeingDeleted(target));
        }

        /// <summary>
        /// Nearest standalone edge within the snap radius at a matching height. An interior tap splits it;
        /// a tap in an end zone reuses that end's node via <paramref name="endNode"/>, as the native
        /// saturation does.
        /// </summary>
        private void FindEdgeAt(float3 point, NetPrefabInfo placedInfo, ref EdgePool edges,
            out Entity edge, out float t, out Entity endNode)
        {
            float2 p = point.xz;
            float best = MaxEndpointSearch;
            edge = Entity.Null;
            t = 0f;
            endNode = Entity.Null;
            NetCellIndex.Enumerator candidates = edges.Index.Near(p, MaxEndpointSearch);
            while (candidates.MoveNext())
            {
                int i = candidates.Current;
                Bezier4x3 bez = edges.Curves[i].m_Bezier;
                float dist = MathUtils.Distance(bez.xz, p, out float tt);
                if (dist >= best) continue;
                Entity candidate = edges.Entities[i];
                NetPrefabInfo targetInfo = default;
                if (EntityManager.HasComponent<PrefabRef>(candidate))
                    targetInfo = NetInfoOf(EntityManager.GetComponentData<PrefabRef>(candidate).m_Prefab);
                float targetHalf = EdgeHalfWidth(candidate, targetInfo.HalfWidth);
                if (dist >= SnapRadius(placedInfo, targetHalf, EdgeSnapDistance)) continue;
                if (!LayersCanConnect(placedInfo, targetInfo)) continue;
                float3 sp = MathUtils.Position(bez, tt);
                if (math.abs(sp.y - point.y) > VerticalSnapTol) continue; // passes above/below, no tap

                float endZone = math.max(MinSplitOffset, placedInfo.SnapDistance);
                Entity reuse = Entity.Null;
                if (math.distance(sp.xz, bez.a.xz) < endZone)
                    reuse = EntityManager.GetComponentData<Edge>(candidate).m_Start;
                else if (math.distance(sp.xz, bez.d.xz) < endZone)
                    reuse = EntityManager.GetComponentData<Edge>(candidate).m_End;

                if (reuse != Entity.Null)
                {
                    // Same liveness rules as FindNodeAt - a dying node must classify as absent.
                    if (!EntityManager.Exists(reuse) || EntityManager.HasComponent<Deleted>(reuse)
                        || IsNodeBeingDeleted(reuse)) continue;
                    best = dist;
                    edge = Entity.Null;
                    t = 0f;
                    endNode = reuse;
                    continue;
                }

                best = dist;
                edge = candidate;
                t = tt;
                endNode = Entity.Null;
            }
        }
    }
}
