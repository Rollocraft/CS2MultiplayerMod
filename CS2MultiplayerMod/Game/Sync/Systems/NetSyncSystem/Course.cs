using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

using CS2MultiplayerMod.Game.Sync.Infrastructure;
namespace CS2MultiplayerMod.Game.Sync.Systems.Net
{
    // Course construction for NetSyncSystem: build a NetCourse definition (Temp-routed for splits,
    // Permanent for the fast path) at a resolved location, plus the geometry queries that resolve
    // an endpoint to an existing node/edge with width-scaled snap radii.
    public partial class NetSyncSystem
    {
        // Cap on the per-prefab contribution to a width-scaled snap radius (metres). Twin highway
        // carriageways sit further apart than this, so one can never grab the other.
        private const float MaxSnapHalfWidth = 8f;

        /// <summary>
        /// The accept radius against one candidate: half the placed net's width plus half the
        /// target's, floored at the legacy fixed radius, capped by <see cref="MaxSnapHalfWidth"/>.
        /// Mirrors the net tool's own snap: a merge endpoint metres from a wide junction node's
        /// centre is physically INSIDE that junction and must connect, not land on free ground.
        /// </summary>
        private static float SnapRadius(float placedHalf, float targetHalf, float floor) =>
            math.max(floor, math.min(placedHalf + targetHalf, MaxSnapHalfWidth));

        /// <summary>
        /// Connect-layer gate for the width-scaled radii: with both layer sets known they must
        /// intersect (the wide radius must not join a power line to a road junction); missing data
        /// on either side allows, exactly like the fixed-radius behaviour before scaling.
        /// </summary>
        private static bool LayersCanConnect(Layer placed, Layer target) =>
            placed == Layer.None || target == Layer.None || (placed & target) != Layer.None;

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
            float bestSq = MaxSnapHalfWidth * MaxSnapHalfWidth;
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
                float radius = SnapRadius(placedInfo.HalfWidth, targetInfo.HalfWidth, NodeSnapDistance);
                if (dSq >= radius * radius) continue;
                if (!LayersCanConnect(placedInfo.ConnectLayers, targetInfo.ConnectLayers)) continue;
                if (IsNodeBeingDeleted(nodeEntities[i])) continue;
                bestSq = dSq;
                best = nodeEntities[i];
            }
            return best;
        }

        /// <summary>
        /// Like <see cref="FindNodeAt"/>, but over the OWNED node pool, for utility nets only: the
        /// nearest building sub-net node (a power plant's high-voltage stub, a water facility's pipe
        /// stub) whose net layers can connect to <paramref name="placedConnect"/>. The sender's
        /// committed segment ends exactly ON such a node when it was drawn onto a building connector,
        /// so without this the realized line stacks a fresh, disconnected node on top of it and the
        /// building never powers/feeds the line. Roads never come here (their layers aren't in
        /// <see cref="UtilityConnectLayers"/>), so driveways and hidden lane sub-nets stay untouchable.
        /// </summary>
        private Entity FindUtilityNodeAt(float3 position, NativeArray<Entity> ownedEntities,
            NativeArray<Node> ownedData, Layer placedConnect)
        {
            float2 p = position.xz;
            float bestSq = NodeSnapDistance * NodeSnapDistance;
            Entity best = Entity.Null;
            for (int i = 0; i < ownedData.Length; i++)
            {
                float dSq = math.distancesq(p, ownedData[i].m_Position.xz);
                if (dSq >= bestSq) continue;
                if (math.abs(ownedData[i].m_Position.y - position.y) > VerticalSnapTol) continue;
                NetPrefabInfo info = NetInfoOf(
                    EntityManager.GetComponentData<PrefabRef>(ownedEntities[i]).m_Prefab);
                if ((info.ConnectLayers & placedConnect & UtilityConnectLayers) == Layer.None) continue;
                if (IsNodeBeingDeleted(ownedEntities[i])) continue;
                bestSq = dSq;
                best = ownedEntities[i];
            }
            return best;
        }

        /// <summary>Cached connect layers + allowed elevation range + half-width of a net prefab.</summary>
        private NetPrefabInfo NetInfoOf(Entity prefab)
        {
            NetPrefabInfo info;
            if (_netInfoCache.TryGetValue(prefab, out info)) return info;
            if (EntityManager.HasComponent<NetData>(prefab))
                info.ConnectLayers = EntityManager.GetComponentData<NetData>(prefab).m_ConnectLayers;
            if (EntityManager.HasComponent<PlaceableNetData>(prefab))
            {
                Bounds1 range = EntityManager.GetComponentData<PlaceableNetData>(prefab).m_ElevationRange;
                info.ElevMin = range.min;
                info.ElevMax = range.max;
                info.Placeable = true;
            }
            if (EntityManager.HasComponent<NetGeometryData>(prefab))
                info.HalfWidth = EntityManager.GetComponentData<NetGeometryData>(prefab).m_DefaultWidth * 0.5f;
            _netInfoCache[prefab] = info;
            return info;
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
        /// mirrors what the net tool's CreateDefinitionsJob produces. Non-<paramref name="permanent"/>
        /// routes the edge through Temp + the ApplyTool pipeline - required whenever an existing
        /// edge must SPLIT (ApplyNetSystem is the only splitter). <paramref name="permanent"/> makes
        /// the generate systems build the finished real edge directly in the same frame's
        /// Modification - the fast path for courses that touch nothing pre-existing: no arm, no
        /// preview wipe, no gate, no drain, no interaction with the local player's tool.
        /// </summary>
        private void CreateCourse(Entity prefab, Bezier4x3 bez, float length,
            Entity startSnap, float startT, Entity endSnap, float endT,
            float2 startElevation, float2 endElevation, bool permanent)
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
                m_Flags = permanent ? CreationFlags.SubElevation | CreationFlags.Permanent
                                    : CreationFlags.SubElevation,
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
            float best = MaxSnapHalfWidth;
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
                if (dist >= SnapRadius(placedInfo.HalfWidth, targetInfo.HalfWidth, EdgeSnapDistance)) continue;
                if (!LayersCanConnect(placedInfo.ConnectLayers, targetInfo.ConnectLayers)) continue;
                float3 sp = MathUtils.Position(bez, tt);
                if (math.abs(sp.y - point.y) > VerticalSnapTol) continue; // passes above/below, no tap

                float endZone = math.max(MinSplitOffset, math.min(targetInfo.HalfWidth, MaxSnapHalfWidth));
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
