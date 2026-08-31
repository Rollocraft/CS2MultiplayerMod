using System.Collections.Generic;
using Colossal.Collections;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Simulation;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

using CS2MultiplayerMod.Game.Sync.Infrastructure;
using CS2MultiplayerMod.Game.Sync.Commands;
namespace CS2MultiplayerMod.Game.Sync.Systems.Net
{
    // The geometry the realize cycle leans on: whether a span is already built, what an endpoint
    // lands on, and the terrain and water surfaces a course is measured against.
    public partial class NetSyncSystem
    {
        /// <summary>
        /// True when every point of <paramref name="span"/> already lies on live same-prefab geometry
        /// - five samples along the curve, each of which must sit on SOME existing edge of that prefab
        /// (the span may map to several local sub-edges). Uses the tight SplitMatch tolerances so a
        /// parallel road or a span rebuilt at another elevation is never wrongly treated as a
        /// duplicate.
        /// </summary>
        private bool SpanAlreadyBuilt(Entity prefab, Bezier4x3 span, ref EdgePool edges)
        {
            for (int s = 0; s <= 4; s++)
            {
                float3 p = MathUtils.Position(span, s / 4f);
                bool covered = false;
                NetCellIndex.Enumerator candidates = edges.Index.Near(p.xz, SplitMatch.TolXZ);
                while (candidates.MoveNext())
                {
                    int i = candidates.Current;
                    Bezier4x3 bez = edges.Curves[i].m_Bezier;
                    float t;
                    if (MathUtils.Distance(bez.xz, p.xz, out t) > SplitMatch.TolXZ) continue;
                    if (math.abs(MathUtils.Position(bez, t).y - p.y) > SplitMatch.TolY) continue;
                    if (EntityManager.GetComponentData<global::Game.Prefabs.PrefabRef>(edges.Entities[i]).m_Prefab
                        != prefab) continue;
                    covered = true;
                    break;
                }
                if (!covered) return false;
            }
            return true;
        }

        /// <summary>
        /// Native multi-course operations use the game's live network search tree for idempotence.
        /// Only edges near each of the five coverage samples are visited, so a large grid scales with
        /// local network density rather than with every edge in the city.
        /// </summary>
        private static bool SpanAlreadyBuilt(Entity prefab, Bezier4x3 span,
            ref LiveEdgeSearchSnapshot search)
        {
            for (int s = 0; s <= 4; s++)
            {
                float3 point = MathUtils.Position(span, s / 4f);
                var iterator = new SpanCoverageIterator
                {
                    Bounds = new Bounds3(
                        point - new float3(SplitMatch.TolXZ, SplitMatch.TolY, SplitMatch.TolXZ),
                        point + new float3(SplitMatch.TolXZ, SplitMatch.TolY, SplitMatch.TolXZ)),
                    Point = point,
                    Prefab = prefab,
                    Curves = search.Curves,
                    Prefabs = search.Prefabs,
                    Owners = search.Owners,
                    Temps = search.Temps,
                    Deleted = search.Deleted,
                };
                search.Tree.Iterate(ref iterator);
                if (!iterator.Covered) return false;
            }
            return true;
        }

        /// <summary>
        /// True when the course's interior (away from both endpoints, which
        /// <see cref="ClassifyEndpoint"/> already resolved) comes within splitting range of any
        /// existing edge — a transversal crossing or a lengthwise overlap. The game cuts every such
        /// edge during Temp generation, so the course counts against the one-splitting-course-per-batch
        /// rule even though neither endpoint classifies as a split. The fallback probe uses native
        /// connection layers and physical widths; a conservative false positive only serializes work,
        /// while a false negative could place two conflicting split courses in one commit.
        /// </summary>
        private bool BodyTouchesExistingEdge(Bezier4x3 course, NetPrefabInfo placedInfo,
            ref EdgePool edges)
        {
            // The control hull contains the curve, so an expanded-AABB miss is an exact reject.
            float3 lo = math.min(math.min(course.a, course.b), math.min(course.c, course.d))
                - new float3(MaxEndpointSearch, VerticalSnapTol, MaxEndpointSearch);
            float3 hi = math.max(math.max(course.a, course.b), math.max(course.c, course.d))
                + new float3(MaxEndpointSearch, VerticalSnapTol, MaxEndpointSearch);

            // Sample tightly enough (≈ EdgeSnapDistance apart, via the control-polygon length upper
            // bound) that a perpendicular crossing cannot slip between two samples.
            float approxLen = math.distance(course.a, course.b) + math.distance(course.b, course.c)
                + math.distance(course.c, course.d);
            int samples = math.clamp((int)(approxLen / EdgeSnapDistance), 8, 128);

            NetCellIndex.Enumerator candidates = edges.Index.Overlapping(lo.xz, hi.xz);
            while (candidates.MoveNext())
            {
                int i = candidates.Current;
                Bezier4x3 bez = edges.Curves[i].m_Bezier;
                float3 elo = math.min(math.min(bez.a, bez.b), math.min(bez.c, bez.d));
                float3 ehi = math.max(math.max(bez.a, bez.b), math.max(bez.c, bez.d));
                if (math.any(elo > hi) || math.any(ehi < lo)) continue;

                Entity candidate = edges.Entities[i];
                NetPrefabInfo targetInfo = default(NetPrefabInfo);
                if (EntityManager.HasComponent<global::Game.Prefabs.PrefabRef>(candidate))
                    targetInfo = NetInfoOf(EntityManager.GetComponentData<global::Game.Prefabs.PrefabRef>(candidate).m_Prefab);
                if (!LayersCanConnect(placedInfo, targetInfo)) continue;
                float touchDistance = math.max(EdgeSnapDistance,
                    placedInfo.HalfWidth + EdgeHalfWidth(candidate, targetInfo.HalfWidth) +
                    placedInfo.SnapDistance);

                for (int s = 1; s < samples; s++)
                {
                    float3 p = MathUtils.Position(course, s / (float)samples);
                    // Endpoint neighbourhoods belong to endpoint classification (reuse/split/merge).
                    if (math.distance(p.xz, course.a.xz) < NodeSnapDistance) continue;
                    if (math.distance(p.xz, course.d.xz) < NodeSnapDistance) continue;
                    float t;
                    if (MathUtils.Distance(bez.xz, p.xz, out t) >= touchDistance) continue;
                    if (math.abs(MathUtils.Position(bez, t).y - p.y) > VerticalSnapTol) continue; // other level
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Resolve where one course endpoint connects, in priority order: an existing real node (reuse),
        /// a building's utility sub-net node (utility nets only - a power/pipe connector stub), a
        /// pending new node another course in this batch creates (merge), a pending batch edge it taps
        /// mid-span (defer until real), an existing real edge - reusing an end node for taps inside its
        /// end zone, splitting for interior taps - else free ground. Returns the snap entity (node to
        /// reuse, or edge to split, or Entity.Null) and, via out params, the split parameter and the
        /// <c>Kind*</c> classification.
        /// </summary>
        private Entity ClassifyEndpoint(float3 p, NetPrefabInfo placedInfo,
            ref NodePool nodes, ref EdgePool edges, ref NodePool ownedNodes,
            NativeList<float3> batchNewNodes, NativeList<Bezier4x3> batchEdges,
            out float t, out int kind)
        {
            t = 0f;
            Entity node = FindNodeAt(p, placedInfo, ref nodes);
            if (node != Entity.Null) { kind = KindReuseNode; return node; }
            // A power line / pipe endpoint lying on a building's connector stub connects to it —
            // the sender drew it onto that stub, so the committed segment ends exactly there.
            if ((placedInfo.ConnectLayers & UtilityConnectLayers) != Layer.None)
            {
                node = FindUtilityNodeAt(p, ref ownedNodes, placedInfo);
                if (node != Entity.Null) { kind = KindReuseConnector; return node; }
            }
            // Coincides with a new node another course in this batch creates -> leave it as a fresh node
            // (Entity.Null) and let GenerateNodesSystem merge the two by exact position.
            if (NearAny(p, batchNewNodes, NodeSnapDistance)) { kind = KindMergeBatch; return Entity.Null; }
            // Taps the middle of an edge this batch is still building -> can't split a not-yet-real edge;
            // defer the whole course to the next cycle, where that edge is real and this becomes a split.
            if (MidSpanOfAnyBatch(p, batchEdges)) { kind = KindDeferBatchEdge; return Entity.Null; }
            Entity edge, endNode;
            FindEdgeAt(p, placedInfo, ref edges, out edge, out t, out endNode);
            // A tap inside an existing edge's end zone reuses that end's node (see FindEdgeAt).
            if (endNode != Entity.Null) { kind = KindReuseNode; return endNode; }
            if (edge != Entity.Null) { kind = KindSplit; return edge; }
            kind = KindFree;
            return Entity.Null;
        }

        /// <summary>
        /// Classify against the source's absolute height first. If that finds open ground and the
        /// placed network is a utility, retry at the height produced by applying the source endpoint
        /// elevation to this machine's surface. A successful second lookup means the endpoint is the
        /// same visible local pipe/cable connection despite terrain or water drift; retaining the
        /// first result for every other case preserves bridge/tunnel level separation.
        /// </summary>
        private Entity ClassifyEndpointWithLocalSurface(Entity prefab, float3 sourcePoint,
            float2 sourceElevation, NetPrefabInfo placedInfo,
            ref NodePool nodes, ref EdgePool edges, ref NodePool ownedNodes,
            NativeList<float3> batchNewNodes, NativeList<Bezier4x3> batchEdges,
            ref TerrainHeightData heightData, ref WaterSurfaceData<SurfaceWater> waterData,
            out float t, out int kind)
        {
            Entity result = ClassifyEndpoint(sourcePoint, placedInfo, ref nodes, ref edges,
                ref ownedNodes, batchNewNodes, batchEdges, out t, out kind);
            if (kind != KindFree) return result;

            float3 projected;
            // Utility layers only here: this path classifies an endpoint the source left on open
            // ground, where a height reference moved for a road is exactly the bridge/tunnel level
            // separation that must be preserved.
            if (!TryProjectEndpointToLocalSurface(prefab, placedInfo, sourcePoint,
                    sourceElevation, false, ref heightData, ref waterData, out projected))
                return result;

            float projectedT;
            int projectedKind;
            Entity projectedResult = ClassifyEndpoint(projected, placedInfo,
                ref nodes, ref edges, ref ownedNodes, batchNewNodes, batchEdges,
                out projectedT, out projectedKind);
            if (projectedKind == KindFree) return result;

            t = projectedT;
            kind = projectedKind;
            _rzLocalSurfaceMatches++;
            return projectedResult;
        }

        /// <summary>
        /// Mark the echo-suppression guard for a course being realized. The capture side
        /// consumes the key of the committed edge's START (its <c>a</c> endpoint), but the
        /// committed geometry can differ from the command: an endpoint that reuses a node
        /// lands exactly ON that node - up to <see cref="NodeSnapDistance"/> from the
        /// commanded point, past the guard's 0.5 m buckets - a split lands on the split
        /// point, and the game may commit the edge with its endpoints swapped. So mark
        /// every position the committed start can be: both raw endpoints plus each end's
        /// resolved snap target. Stale extras simply age out (15 s TTL).
        /// </summary>
        private void MarkRealizeGuards(string prefabName, float3 a, float3 d,
            Entity startSnap, int startKind, float startT,
            Entity endSnap, int endKind, float endT, long now)
        {
            _guard.Mark(ReplicationGuard.Key(prefabName, a), now);
            _guard.Mark(ReplicationGuard.Key(prefabName, d), now);
            MarkResolvedEndpoint(prefabName, startSnap, startKind, startT, now);
            MarkResolvedEndpoint(prefabName, endSnap, endKind, endT, now);
        }

        private void MarkResolvedEndpoint(string prefabName, Entity snap, int kind, float t, long now)
        {
            if (snap == Entity.Null || !EntityManager.Exists(snap)) return;
            float3 position;
            if ((kind == KindReuseNode || kind == KindReuseConnector) && EntityManager.HasComponent<Node>(snap))
                position = EntityManager.GetComponentData<Node>(snap).m_Position;
            else if (kind == KindSplit && EntityManager.HasComponent<Curve>(snap))
                position = MathUtils.Position(EntityManager.GetComponentData<Curve>(snap).m_Bezier, t);
            else return;
            _guard.Mark(ReplicationGuard.Key(prefabName, position), now);
        }

        // Diagnostic tally by endpoint classification.
        private void TallyEnd(int kind)
        {
            switch (kind)
            {
                case KindReuseNode: _rzSnapEnds++; break;
                case KindReuseConnector: _rzSnapEnds++; break;
                case KindMergeBatch: _rzMergeEnds++; break;
                case KindSplit: _rzMidEnds++; break;
                default: _rzFreeEnds++; break;
            }
        }

        private void TallySurfaceCorrection(float start, float end)
        {
            if (start != 0f) { _rzSurfaceCorrections++; _rzSurfaceCorrectionMax = math.max(_rzSurfaceCorrectionMax, math.abs(start)); }
            if (end != 0f) { _rzSurfaceCorrections++; _rzSurfaceCorrectionMax = math.max(_rzSurfaceCorrectionMax, math.abs(end)); }
        }

        /// <summary>
        /// Read this frame's terrain and water surfaces once per realize cycle. The water dependency
        /// completes here so the data is main-thread readable; between simulation steps the handle is
        /// already complete.
        /// </summary>
        private void TakeSurfaceSnapshot(ref TerrainHeightData heightData,
            ref WaterSurfaceData<SurfaceWater> waterData)
        {
            heightData = _terrainSystem.GetHeightData(waitForPending: true);
            JobHandle waterDeps;
            waterData = _waterSystem.GetSurfaceData(out waterDeps);
            waterDeps.Complete();
        }

        /// <summary>
        /// True when <paramref name="p"/> lies within <paramref name="tol"/> (XZ) of any point at a
        /// matching height. The height gate mirrors the game's node merge, which is by position - a
        /// batch containing both a ground road and a bridge above it must not classify the bridge's
        /// endpoint as merging with the ground node.
        /// </summary>
        private static bool NearAny(float3 p, NativeList<float3> points, float tol)
        {
            float2 xz = p.xz;
            float tolSq = tol * tol;
            for (int i = 0; i < points.Length; i++)
                if (math.distancesq(xz, points[i].xz) < tolSq
                    && math.abs(points[i].y - p.y) <= VerticalSnapTol) return true;
            return false;
        }

        /// <summary>
        /// True when <paramref name="point"/> taps the MIDDLE (away from both ends) of any curve this
        /// batch is creating - the same mid-span test as <see cref="FindEdgeAt"/>, against pending
        /// batch edges rather than real ones, with the same height gate (a crossing on another level
        /// is not a tap).
        /// </summary>
        private static bool MidSpanOfAnyBatch(float3 point, NativeList<Bezier4x3> curves)
        {
            float2 p = point.xz;
            for (int i = 0; i < curves.Length; i++)
            {
                Bezier4x3 bez = curves[i];
                float tt;
                if (MathUtils.Distance(bez.xz, p, out tt) >= EdgeSnapDistance) continue;
                float3 sp = MathUtils.Position(bez, tt);
                if (math.abs(sp.y - point.y) > VerticalSnapTol) continue;
                if (math.distance(sp.xz, bez.a.xz) < MinSplitOffset) continue;
                if (math.distance(sp.xz, bez.d.xz) < MinSplitOffset) continue;
                return true;
            }
            return false;
        }
    }
}
