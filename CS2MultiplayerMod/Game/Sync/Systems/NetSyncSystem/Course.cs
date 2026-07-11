using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.Infrastructure;
namespace CS2MultiplayerMod.Game.Sync.Systems.Net
{
    // Course construction for NetSyncSystem: build a Temp-routed NetCourse definition at a resolved
    // location, plus the fallback geometry queries used when native endpoint intent is unavailable.
    public partial class NetSyncSystem
    {
        // Broad safety ceiling for fallback geometric searches. Exact native-intent commands use a
        // target anchor and source curve instead; this ceiling only bounds legacy/system captures.
        private const float MaxEndpointSearch = 64f;

        /// <summary>
        /// Conservative fallback accept radius using both network widths and the placed prefab's
        /// snap distance. Native-intent commands normally bypass this search entirely.
        /// </summary>
        private static float SnapRadius(NetPrefabInfo placed, float targetHalf, float floor) =>
            math.max(floor, placed.HalfWidth + targetHalf + placed.SnapDistance);

        /// <summary>
        /// Native connection-layer rule: either prefab's required layers must be supplied by the
        /// other's connect layers. This is not equivalent to intersecting the two connect masks.
        /// </summary>
        private static bool LayersCanConnect(NetPrefabInfo placed, NetPrefabInfo target) =>
            (placed.RequiredLayers & target.ConnectLayers) == placed.RequiredLayers ||
            (target.RequiredLayers & placed.ConnectLayers) == target.RequiredLayers;

        /// <summary>
        /// Nearest standalone node within the width-scaled snap radius of <paramref name="position"/>
        /// (ranked in XZ, so terrain-height noise never changes which node wins; candidates further
        /// than <see cref="VerticalSnapTol"/> above/below are rejected - a bridge endpoint passing over
        /// a ground junction crosses it, it doesn't connect to it), or <see cref="Entity.Null"/> in
        /// open ground. Reusing that node joins the new segment to the junction instead of stacking a
        /// second, disconnected node on top of it.
        ///
        /// CRASH GUARD: skips a node that is being torn down by a bulldoze this frame (all its connected
        /// edges Deleted). Deleting an edge does NOT immediately tag its end nodes Deleted - they linger,
        /// still query-able, for a frame or two until the net cleanup runs. If a course REUSES such a
        /// dying node and we then commit it, ApplyNetSystem dereferences the stale node/edge and the
        /// process NATIVE-crashes (seen live when the client spammed build->bulldoze->build at one spot:
        /// "DELETED edge ... -> REUSE node #... -> commit ... -> [log ends]"). Treating a dying node as absent
        /// lands the endpoint on fresh ground instead - disconnected at worst, never a crash.
        /// </summary>
        private Entity FindNodeAt(float3 position, NetPrefabInfo placedInfo,
            NativeArray<Entity> nodeEntities, NativeArray<Node> nodeData)
        {
            float2 p = position.xz;
            float bestSq = MaxEndpointSearch * MaxEndpointSearch;
            Entity best = Entity.Null;
            for (int i = 0; i < nodeData.Length; i++)
            {
                float dSq = math.distancesq(p, nodeData[i].m_Position.xz);
                // Only nodes inside the radius cap AND nearer than the current best reach the
                // per-candidate lookups, so those run for a handful of candidates at most.
                if (dSq >= bestSq) continue;
                if (math.abs(nodeData[i].m_Position.y - position.y) > VerticalSnapTol) continue; // other level
                NetPrefabInfo targetInfo = default;
                if (EntityManager.HasComponent<PrefabRef>(nodeEntities[i]))
                    targetInfo = NetInfoOf(EntityManager.GetComponentData<PrefabRef>(nodeEntities[i]).m_Prefab);
                float radius = SnapRadius(placedInfo, NodeHalfWidth(nodeEntities[i], targetInfo.HalfWidth), NodeSnapDistance);
                if (dSq >= radius * radius) continue;
                if (!LayersCanConnect(placedInfo, targetInfo)) continue;
                if (IsNodeBeingDeleted(nodeEntities[i])) continue;
                bestSq = dSq;
                best = nodeEntities[i];
            }
            return best;
        }

        /// <summary>
        /// Like <see cref="FindNodeAt"/>, but over the OWNED node pool, for utility nets only: the
        /// nearest building sub-net node (a power plant's high-voltage stub, a water facility's pipe
        /// stub) whose net layers can connect to <paramref name="placedInfo"/>. The sender's
        /// committed segment ends exactly ON such a node when it was drawn onto a building connector,
        /// so without this the realized line stacks a fresh, disconnected node on top of it and the
        /// building never powers/feeds the line. Roads never come here (their layers aren't in
        /// <see cref="UtilityConnectLayers"/>), so driveways and hidden lane sub-nets stay untouchable.
        /// </summary>
        private Entity FindUtilityNodeAt(float3 position, NativeArray<Entity> ownedEntities,
            NativeArray<Node> ownedData, NetPrefabInfo placedInfo)
        {
            float2 p = position.xz;
            float bestSq = MaxEndpointSearch * MaxEndpointSearch;
            Entity best = Entity.Null;
            for (int i = 0; i < ownedData.Length; i++)
            {
                float dSq = math.distancesq(p, ownedData[i].m_Position.xz);
                if (dSq >= bestSq) continue;
                if (math.abs(ownedData[i].m_Position.y - position.y) > VerticalSnapTol) continue;
                NetPrefabInfo info = NetInfoOf(
                    EntityManager.GetComponentData<PrefabRef>(ownedEntities[i]).m_Prefab);
                float radius = SnapRadius(placedInfo,
                    NodeHalfWidth(ownedEntities[i], info.HalfWidth), NodeSnapDistance);
                if (dSq >= radius * radius) continue;
                if (!LayersCanConnect(placedInfo, info) ||
                    ((info.ConnectLayers | info.RequiredLayers) &
                     (placedInfo.ConnectLayers | placedInfo.RequiredLayers) & UtilityConnectLayers) == Layer.None)
                    continue;
                if (IsNodeBeingDeleted(ownedEntities[i])) continue;
                bestSq = dSq;
                best = ownedEntities[i];
            }
            return best;
        }

        /// <summary>Cached native connection/elevation/width facts for one net prefab.</summary>
        private NetPrefabInfo NetInfoOf(Entity prefab)
        {
            NetPrefabInfo info;
            if (_netInfoCache.TryGetValue(prefab, out info)) return info;
            if (EntityManager.HasComponent<NetData>(prefab))
            {
                NetData net = EntityManager.GetComponentData<NetData>(prefab);
                info.RequiredLayers = net.m_RequiredLayers;
                info.ConnectLayers = net.m_ConnectLayers;
            }
            if (EntityManager.HasComponent<PlaceableNetData>(prefab))
            {
                PlaceableNetData placeable = EntityManager.GetComponentData<PlaceableNetData>(prefab);
                Bounds1 range = placeable.m_ElevationRange;
                info.ElevMin = range.min;
                info.ElevMax = range.max;
                info.SnapDistance = math.max(placeable.m_SnapDistance, 1f);
                info.Placeable = true;
            }
            if (EntityManager.HasComponent<NetGeometryData>(prefab))
                info.HalfWidth = EntityManager.GetComponentData<NetGeometryData>(prefab).m_DefaultWidth * 0.5f;
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
        /// The elevation (height above/below the local surface, the game's
        /// <see cref="global::Game.Net.Elevation"/> convention) a course endpoint must carry. A reused
        /// node's committed elevation is exact - a pylon or building connector keeps its height.
        /// Otherwise it is derived from the transmitted Y against the LOCAL terrain (which is
        /// synced, so it equals the sender's): negative = underground (pipes, ground cables);
        /// positive is re-measured against the water surface where there is water, exactly like
        /// the net tool (a bridge over a lake is "+5", not "lakebed + 40"). Road-like nets (their
        /// allowed elevation range spans 0) get a dead zone: a committed ground road's Y deviates
        /// from pre-build terrain by the game's own slope grading, which must stay elevation 0 -
        /// fixed-elevation nets (power lines, pipes) skip it, their offset IS the placement.
        /// </summary>
        private float2 EndElevation(Entity prefab, Entity snap, int kind, float3 p,
            ref TerrainHeightData heightData, ref WaterSurfaceData<SurfaceWater> waterData)
        {
            if ((kind == KindReuseNode || kind == KindReuseConnector) &&
                EntityManager.HasComponent<global::Game.Net.Elevation>(snap))
                return EntityManager.GetComponentData<global::Game.Net.Elevation>(snap).m_Elevation;

            float e = p.y - TerrainUtils.SampleHeight(ref heightData, p);
            if (e > 0f)
                e = math.max(p.y - WaterUtils.SampleHeight(ref waterData, ref heightData, p), 0f);

            NetPrefabInfo info = NetInfoOf(prefab);
            bool fixedBelow = info.Placeable && info.ElevMax <= 0f && info.ElevMin < 0f;
            bool fixedAbove = info.Placeable && info.ElevMin >= 0f && info.ElevMax > 0f;
            if (!fixedBelow && !fixedAbove && math.abs(e) < GroundElevationDeadZone) e = 0f;
            if (info.Placeable) e = math.clamp(e, info.ElevMin, info.ElevMax);
            return new float2(e);
        }

        /// <summary>
        /// True when <paramref name="node"/> has no live (existing, non-<see cref="Deleted"/>) connected
        /// edge - i.e. a bulldoze this frame is tearing it down. See the crash guard on
        /// <see cref="FindNodeAt"/>. A node with no <see cref="ConnectedEdge"/> buffer at all is left
        /// reusable (it isn't attached to a being-deleted edge, so it can't trigger that crash).
        /// </summary>
        private bool IsNodeBeingDeleted(Entity node)
        {
            if (!EntityManager.HasBuffer<ConnectedEdge>(node)) return false;
            DynamicBuffer<ConnectedEdge> edges = EntityManager.GetBuffer<ConnectedEdge>(node, isReadOnly: true);
            for (int i = 0; i < edges.Length; i++)
            {
                Entity e = edges[i].m_Edge;
                if (EntityManager.Exists(e) && !EntityManager.HasComponent<Deleted>(e)) return false;
            }
            return true; // empty buffer or every connected edge gone/Deleted -> being torn down
        }

        /// <summary>
        /// Build a net-course definition with each endpoint resolved to an existing node (reuse) or
        /// an existing edge (split at <paramref name="endT"/>) or Entity.Null (fresh node). This
        /// mirrors the minimum tool definition for fallback/system-captured curves. It always routes
        /// through Temp + ApplyTool so late contacts and splits cannot bypass native commit handling.
        /// </summary>
        private void CreateCourse(Entity prefab, Bezier4x3 bez, float length,
            Entity startSnap, float startT, Entity endSnap, float endT,
            float2 startElevation, float2 endElevation)
        {
            // Never bake a dead entity into the course: a snap/split target resolved this frame could
            // have been torn down (a remote bulldoze, the local sim) before the course is consumed.
            // ApplyNetSystem crashes natively on a stale split reference, so drop to a fresh node. We
            // reject a target that no longer exists OR has been tagged Deleted (destruction in progress
            // but the entity still lingers) — the second half is the spam build↔bulldoze crash guard.
            if (startSnap != Entity.Null && (!EntityManager.Exists(startSnap) || EntityManager.HasComponent<Deleted>(startSnap))) { startSnap = Entity.Null; startT = 0f; }
            if (endSnap != Entity.Null && (!EntityManager.Exists(endSnap) || EntityManager.HasComponent<Deleted>(endSnap))) { endSnap = Entity.Null; endT = 0f; }

            Entity definition = EntityManager.CreateEntity();
            EntityManager.AddComponentData(definition, new CreationDefinition
            {
                m_Prefab = prefab,
                // Seed from the (shared) geometry so procedural detail (wear/props) looks identical on
                // every machine.
                m_RandomSeed = math.asint(bez.a.x) ^ math.asint(bez.a.z) ^ math.asint(bez.d.x) ^ math.asint(bez.d.z),
                // SubElevation matches the net tool's straight-line recipe (CreateStraightLine);
                // without it the generated edge's sub-elevation isn't set up the way the game expects.
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
                    // Real node rotation from the curve tangent — the net tool uses GetNodeRotation, NOT
                    // identity. A wrong node rotation yields an edge that renders but mis-connects.
                    m_Rotation = NetUtils.GetNodeRotation(MathUtils.Tangent(bez, 0f)),
                    // Height above/below the surface (see EndElevation) — the ONLY source of the
                    // committed node's Game.Net.Elevation. Without it an elevated net (power line,
                    // pipe, bridge) commits as a GROUND net at this Y: the ground terraforms up/down
                    // to meet it and the prefab's poles stack on top of the already-raised curve.
                    m_Elevation = startElevation,
                    m_CourseDelta = 0f,
                    m_SplitPosition = startT,
                    // IsLeft|IsRight: a non-parallel single course occupies both sides (CreateStraightLine
                    // sets these whenever m_ParallelCount is 0).
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
            EntityManager.AddComponent<Updated>(definition);
            EntityManager.AddComponent<Deleted>(definition);
            ConstructionCharger.ChargeNet(EntityManager, prefab, length, _prefabSystem.GetPrefabName(prefab));
        }

        /// <summary>Create a Temp-routed definition from captured native course intent.</summary>
        private void CreateNativeCourse(Entity prefab, NetPlacementCommand command, Bezier4x3 bez,
            Entity startSnap, float startT, int startKind,
            Entity endSnap, float endT, int endKind)
        {
            if (startSnap != Entity.Null && (!EntityManager.Exists(startSnap) ||
                EntityManager.HasComponent<Deleted>(startSnap))) startSnap = Entity.Null;
            if (endSnap != Entity.Null && (!EntityManager.Exists(endSnap) ||
                EntityManager.HasComponent<Deleted>(endSnap))) endSnap = Entity.Null;

            CreationFlags flags = (CreationFlags)command.CreationFlags;
            // A placement transaction always creates a fresh course. Destructive/selection modes
            // have dedicated sync systems and must never be smuggled through this command.
            flags &= CreationFlags.Invert | CreationFlags.Align | CreationFlags.Hidden |
                     CreationFlags.Optional | CreationFlags.Lowered | CreationFlags.Native |
                     CreationFlags.Construction | CreationFlags.SubElevation;
            flags |= CreationFlags.SubElevation;

            NetPrefabInfo prefabInfo = NetInfoOf(prefab);
            float2 courseElevation = ClampNativeElevation(prefabInfo,
                new float2(command.CourseElevationLeft, command.CourseElevationRight));

            Entity definition = EntityManager.CreateEntity();
            Entity subPrefab = Entity.Null;
            if (!string.IsNullOrEmpty(command.SubPrefabName) &&
                !_prefabIndex.TryResolve(command.SubPrefabName, out subPrefab))
            {
                EntityManager.DestroyEntity(definition);
                throw new System.InvalidOperationException("Unknown net sub-prefab '" +
                                                           command.SubPrefabName + "'.");
            }
            if (subPrefab != Entity.Null && !EntityManager.HasComponent<NetLaneData>(subPrefab))
            {
                EntityManager.DestroyEntity(definition);
                throw new System.InvalidOperationException("Net sub-prefab '" +
                    command.SubPrefabName + "' is not a lane prefab.");
            }
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
                m_StartPosition = MakeNativeCoursePos(command.Start, prefabInfo, startSnap, startT, startKind),
                m_EndPosition = MakeNativeCoursePos(command.End, prefabInfo, endSnap, endT, endKind),
            });
            EntityManager.AddComponent<Updated>(definition);
            EntityManager.AddComponent<Deleted>(definition);
            if ((((CoursePosFlags)command.Start.Flags | (CoursePosFlags)command.End.Flags) &
                 CoursePosFlags.DontCreate) == 0)
            {
                ConstructionCharger.ChargeNet(EntityManager, prefab, command.Length,
                    _prefabSystem.GetPrefabName(prefab));
            }
        }

        private static CoursePos MakeNativeCoursePos(NetEndpointIntent intent, NetPrefabInfo prefabInfo,
            Entity target, float resolvedT, int resolvedKind)
        {
            float4 rotation = math.normalizesafe(
                new float4(intent.RotX, intent.RotY, intent.RotZ, intent.RotW),
                new float4(0f, 0f, 0f, 1f));
            return new CoursePos
            {
                m_Entity = target,
                m_Position = new float3(intent.PosX, intent.PosY, intent.PosZ),
                m_Rotation = new quaternion(rotation),
                m_Elevation = ClampNativeElevation(prefabInfo,
                    new float2(intent.ElevationLeft, intent.ElevationRight)),
                m_CourseDelta = intent.CourseDelta,
                m_SplitPosition = resolvedKind == KindSplit ? resolvedT : intent.SplitPosition,
                m_Flags = (CoursePosFlags)intent.Flags,
                m_ParentMesh = intent.ParentMesh,
            };
        }

        private static float2 ClampNativeElevation(NetPrefabInfo prefabInfo, float2 elevation)
        {
            return prefabInfo.Placeable
                ? math.clamp(elevation, new float2(prefabInfo.ElevMin), new float2(prefabInfo.ElevMax))
                : elevation;
        }

        /// <summary>
        /// Nearest standalone edge whose centreline passes within the width-scaled snap radius (XZ)
        /// of <paramref name="point"/> at a matching height (within <see cref="VerticalSnapTol"/> - a
        /// bridge endpoint above a ground road crosses it, it does not T-junction into it). A tap in
        /// the edge's interior SPLITS it (returns the edge + split parameter t); a tap inside an end
        /// zone of <c>max(MinSplitOffset, min(halfWidth(target), cap))</c> around either end REUSES
        /// that end's node instead (via <paramref name="endNode"/>) - the native curve-position
        /// saturation - never planting a second junction node inside the existing one's footprint.
        /// </summary>
        private void FindEdgeAt(float3 point, NetPrefabInfo placedInfo,
            NativeArray<Entity> edgeEntities, NativeArray<Curve> edgeCurves,
            out Entity edge, out float t, out Entity endNode)
        {
            float2 p = point.xz;
            float best = MaxEndpointSearch;
            edge = Entity.Null;
            t = 0f;
            endNode = Entity.Null;
            for (int i = 0; i < edgeCurves.Length; i++)
            {
                Bezier4x3 bez = edgeCurves[i].m_Bezier;
                float tt;
                float dist = MathUtils.Distance(bez.xz, p, out tt);
                if (dist >= best) continue;
                NetPrefabInfo targetInfo = default;
                if (EntityManager.HasComponent<PrefabRef>(edgeEntities[i]))
                    targetInfo = NetInfoOf(EntityManager.GetComponentData<PrefabRef>(edgeEntities[i]).m_Prefab);
                float targetHalf = EdgeHalfWidth(edgeEntities[i], targetInfo.HalfWidth);
                if (dist >= SnapRadius(placedInfo, targetHalf, EdgeSnapDistance)) continue;
                if (!LayersCanConnect(placedInfo, targetInfo)) continue;
                float3 sp = MathUtils.Position(bez, tt);
                if (math.abs(sp.y - point.y) > VerticalSnapTol) continue; // passes above/below, no tap

                float endZone = math.max(MinSplitOffset, placedInfo.SnapDistance);
                Entity reuse = Entity.Null;
                if (math.distance(sp.xz, bez.a.xz) < endZone)
                    reuse = EntityManager.GetComponentData<Edge>(edgeEntities[i]).m_Start;
                else if (math.distance(sp.xz, bez.d.xz) < endZone)
                    reuse = EntityManager.GetComponentData<Edge>(edgeEntities[i]).m_End;

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
                edge = edgeEntities[i];
                t = tt;
                endNode = Entity.Null;
            }
        }
    }
}
