using Colossal.Mathematics;
using Game.Net;
using Game.Simulation;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

using CS2MultiplayerMod.Game.Sync.Infrastructure;
namespace CS2MultiplayerMod.Game.Sync.Systems.Net
{
    public partial class NetSyncSystem
    {
        /// <summary>
        /// Five samples each lie on some live same-prefab edge (the span may map to several). Tight
        /// tolerances, so parallel or re-elevated spans never count.
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
                    if (MathUtils.Distance(bez.xz, p.xz, out float t) > SplitMatch.TolXZ) continue;
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

        /// <summary>Same test through the live search tree: cost follows local density, not city size.</summary>
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
        /// The course interior comes within splitting range of an existing edge, so it counts against the
        /// one-splitting-course-per-batch rule. False positives only serialize work.
        /// </summary>
        private bool BodyTouchesExistingEdge(Bezier4x3 course, NetPrefabInfo placedInfo,
            ref EdgePool edges)
        {
            // The control hull contains the curve, so an expanded-AABB miss is an exact reject.
            float3 lo = math.min(math.min(course.a, course.b), math.min(course.c, course.d))
                - new float3(MaxEndpointSearch, VerticalSnapTol, MaxEndpointSearch);
            float3 hi = math.max(math.max(course.a, course.b), math.max(course.c, course.d))
                + new float3(MaxEndpointSearch, VerticalSnapTol, MaxEndpointSearch);

            // Samples about EdgeSnapDistance apart, so a perpendicular crossing cannot slip through.
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
                    if (MathUtils.Distance(bez.xz, p.xz, out float t) >= touchDistance) continue;
                    if (math.abs(MathUtils.Position(bez, t).y - p.y) > VerticalSnapTol) continue; // other level
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Where an endpoint connects, in order: existing node, utility connector stub, pending batch node
        /// (merge), pending batch edge (defer), existing edge (end-zone reuse or split), else free.
        /// </summary>
        private Entity ClassifyEndpoint(float3 p, uint flags, NetPrefabInfo placedInfo,
            ref NodePool nodes, ref EdgePool edges, ref NodePool ownedNodes,
            NetBatchNodes batchNewNodes, NativeList<Bezier4x3> batchEdges,
            out float t, out int kind, out CoursePos? sharedNode)
        {
            t = 0f;
            sharedNode = null;
            Entity node = FindNodeAt(p, placedInfo, ref nodes);
            if (node != Entity.Null) { kind = KindReuseNode; return node; }
            if ((placedInfo.ConnectLayers & UtilityConnectLayers) != Layer.None)
            {
                node = FindUtilityNodeAt(p, ref ownedNodes, placedInfo);
                if (node != Entity.Null) { kind = KindReuseConnector; return node; }
            }
            // Carry the shared endpoint; returning null would make two native node keys.
            if (batchNewNodes.TryFind(p, flags, (uint)placedInfo.RequiredLayers,
                    (uint)placedInfo.ConnectLayers, NodeSnapDistance, VerticalSnapTol, out sharedNode))
            { kind = KindMergeBatch; return Entity.Null; }
            // A not-yet-real batch edge cannot be split; defer to the next cycle.
            if (MidSpanOfAnyBatch(p, batchEdges)) { kind = KindDeferBatchEdge; return Entity.Null; }
            FindEdgeAt(p, placedInfo, ref edges, out Entity edge, out t, out Entity endNode);
            // A tap inside an existing edge's end zone reuses that end's node (see FindEdgeAt).
            if (endNode != Entity.Null) { kind = KindReuseNode; return endNode; }
            if (edge != Entity.Null) { kind = KindSplit; return edge; }
            kind = KindFree;
            return Entity.Null;
        }

        /// <summary>
        /// Source height first; for utilities on open ground, retry at the local-surface height. Other
        /// cases keep bridge/tunnel level separation.
        /// </summary>
        private Entity ClassifyEndpointWithLocalSurface(Entity prefab, float3 sourcePoint,
            float2 sourceElevation, uint flags, NetPrefabInfo placedInfo,
            ref NodePool nodes, ref EdgePool edges, ref NodePool ownedNodes,
            NetBatchNodes batchNewNodes, NativeList<Bezier4x3> batchEdges,
            ref TerrainHeightData heightData, ref WaterSurfaceData<SurfaceWater> waterData,
            out float t, out int kind, out CoursePos? sharedNode)
        {
            Entity result = ClassifyEndpoint(sourcePoint, flags, placedInfo, ref nodes, ref edges,
                ref ownedNodes, batchNewNodes, batchEdges, out t, out kind, out sharedNode);
            if (kind != KindFree) return result;

            // Utilities only: for a road this would erase level separation.
            if (!TryProjectEndpointToLocalSurface(prefab, placedInfo, sourcePoint,
                    sourceElevation, false, ref heightData, ref waterData, out float3 projected))
                return result;

            Entity projectedResult = ClassifyEndpoint(projected, flags, placedInfo,
                ref nodes, ref edges, ref ownedNodes, batchNewNodes, batchEdges,
                out float projectedT, out int projectedKind, out CoursePos? projectedNode);
            if (projectedKind == KindFree) return result;

            t = projectedT;
            kind = projectedKind;
            sharedNode = projectedNode;
            _rzLocalSurfaceMatches++;
            return projectedResult;
        }

        /// <summary>
        /// Echo guards for every position the committed start can land on: both raw endpoints and each
        /// resolved snap target (node reuse, split point, swapped ends). Extras age out.
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

        /// <summary>Reads terrain and water once per cycle, completing the water dependency.</summary>
        private void TakeSurfaceSnapshot(ref TerrainHeightData heightData,
            ref WaterSurfaceData<SurfaceWater> waterData)
        {
            heightData = _terrainSystem.GetHeightData(waitForPending: true);
            waterData = _waterSystem.GetSurfaceData(out JobHandle waterDeps);
            waterDeps.Complete();
        }

        /// <summary>Mid-span tap of a pending batch curve, with the same height gate as <see cref="FindEdgeAt"/>.</summary>
        private static bool MidSpanOfAnyBatch(float3 point, NativeList<Bezier4x3> curves)
        {
            float2 p = point.xz;
            for (int i = 0; i < curves.Length; i++)
            {
                Bezier4x3 bez = curves[i];
                if (MathUtils.Distance(bez.xz, p, out float tt) >= EdgeSnapDistance) continue;
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
