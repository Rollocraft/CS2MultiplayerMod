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
    // Course construction for NetSyncSystem: build a NON-Permanent NetCourse definition at a resolved
    // location, plus the geometry queries that resolve an endpoint to an existing node/edge.
    public partial class NetSyncSystem
    {
        /// <summary>
        /// Nearest standalone node within <see cref="NodeSnapDistance"/> of <paramref name="position"/>
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
        private Entity FindNodeAt(float3 position, NativeArray<Entity> nodeEntities, NativeArray<Node> nodeData)
        {
            float2 p = position.xz;
            float bestSq = NodeSnapDistance * NodeSnapDistance;
            Entity best = Entity.Null;
            for (int i = 0; i < nodeData.Length; i++)
            {
                float dSq = math.distancesq(p, nodeData[i].m_Position.xz);
                if (dSq >= bestSq) continue;
                if (math.abs(nodeData[i].m_Position.y - position.y) > VerticalSnapTol) continue; // other level
                // Only nodes nearer than the current best (always < NodeSnapDistance) reach the liveness
                // check, so the buffer lookup runs for a handful of candidates at most.
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

        /// <summary>Cached connect layers + allowed elevation range of a net prefab.</summary>
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
        /// Build a NON-Permanent net-course definition (-> a Temp edge the game's ApplyNetSystem will
        /// finalize), with each endpoint resolved to an existing node (reuse) or an existing edge
        /// (split at <paramref name="endT"/>) or Entity.Null (fresh node). This mirrors what the net
        /// tool's CreateDefinitionsJob produces - the difference from the shipped recipe is purely the
        /// missing Permanent flag, which routes the edge through Temp + the ApplyTool split path.
        /// </summary>
        private void CreateTempCourse(Entity prefab, Bezier4x3 bez, float length,
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
                // every machine. NOT Permanent -> GenerateEdgesSystem makes a Temp edge -> ApplyNetSystem.
                m_RandomSeed = math.asint(bez.a.x) ^ math.asint(bez.a.z) ^ math.asint(bez.d.x) ^ math.asint(bez.d.z),
                // Matches the net tool's straight-line recipe (CreateStraightLine); without it the
                // generated edge's sub-elevation isn't set up the way the game expects.
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

        /// <summary>
        /// Nearest standalone edge whose centreline passes within <see cref="EdgeSnapDistance"/> (XZ)
        /// of <paramref name="point"/> at a matching height (within <see cref="VerticalSnapTol"/> - a
        /// bridge endpoint above a ground road crosses it, it does not T-junction into it), away from
        /// its end nodes - i.e. a mid-span tap that should SPLIT that edge. Returns the edge and the
        /// split parameter t, or Entity.Null in open ground.
        /// </summary>
        private void FindEdgeAt(float3 point, NativeArray<Entity> edgeEntities, NativeArray<Curve> edgeCurves,
            out Entity edge, out float t)
        {
            float2 p = point.xz;
            float best = EdgeSnapDistance;
            edge = Entity.Null;
            t = 0f;
            for (int i = 0; i < edgeCurves.Length; i++)
            {
                Bezier4x3 bez = edgeCurves[i].m_Bezier;
                float tt;
                float dist = MathUtils.Distance(bez.xz, p, out tt);
                if (dist >= best) continue;
                float3 sp = MathUtils.Position(bez, tt);
                if (math.abs(sp.y - point.y) > VerticalSnapTol) continue; // passes above/below, no tap
                if (math.distance(sp.xz, bez.a.xz) < MinSplitOffset) continue;
                if (math.distance(sp.xz, bez.d.xz) < MinSplitOffset) continue;
                best = dist;
                edge = edgeEntities[i];
                t = tt;
            }
        }
    }
}
