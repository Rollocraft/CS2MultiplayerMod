using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.Infrastructure;

namespace CS2MultiplayerMod.Game.Sync.Systems.Net
{
    // Portable native-target resolution. Source entity ids cannot cross machines, so an endpoint
    // names the source target by anchor, optional prefab and source curve. The resolver tolerates
    // different local subdivision while strongly preferring the same physical edge and direction.
    public partial class NetSyncSystem
    {
        private const float NativeNodeResolveXZ = 2f;
        private const float NativeTargetResolveY = 3f;
        private const float NativeEdgeResolveXZ = 4f;
        private const float ExistingSplitNodeDistance = 1f;

        private bool TryResolveNativeEndpoint(NetEndpointIntent intent, NetPrefabInfo placedInfo,
            ref NodePool nodes, ref EdgePool edges,
            ref NodePool ownedNodes, ref EdgePool ownedEdges, bool allowMergedNodeSplit,
            out Entity target, out float splitT, out int kind)
        {
            target = Entity.Null;
            splitT = 0f;
            switch (intent.Kind)
            {
                case NetEndpointTargetKind.Free:
                    if ((intent.Flags & (uint)global::Game.Tools.CoursePosFlags.DisableMerge) == 0)
                    {
                        target = FindCoincidentNode(intent, placedInfo, ref nodes);
                        if (target != Entity.Null)
                        {
                            kind = KindReuseNode;
                            return true;
                        }
                    }
                    kind = KindFree;
                    return true;
                case NetEndpointTargetKind.Node:
                    target = FindNativeNode(intent, placedInfo, ref nodes);
                    if (target != Entity.Null)
                    {
                        kind = KindReuseNode;
                        return true;
                    }
                    if (allowMergedNodeSplit &&
                        TryResolveMergedNodeAsEdgeSplit(intent, placedInfo, ref edges,
                            out target, out splitT, out kind))
                        return true;
                    target = Entity.Null;
                    splitT = 0f;
                    kind = KindReuseNode;
                    return false;
                // No merged-node fallback here: a building's sub-net stub is never node-reduced,
                // so a missing one is a real absence, not local subdivision drift.
                case NetEndpointTargetKind.OwnedNode:
                    target = FindNativeNode(intent, placedInfo, ref ownedNodes);
                    kind = KindReuseConnector;
                    return target != Entity.Null;
                case NetEndpointTargetKind.Edge:
                    return TryFindNativeEdge(intent, placedInfo, ref edges,
                        out target, out splitT, out kind);
                case NetEndpointTargetKind.OwnedEdge:
                    return TryFindNativeEdge(intent, placedInfo, ref ownedEdges,
                        out target, out splitT, out kind);
                default:
                    kind = KindFree;
                    return false;
            }
        }

        /// <summary>
        /// Resolve a captured target at its source-world height, then (for owner-less utility
        /// nodes/edges only) at the corresponding local-surface height. Explicit target identity,
        /// prefab/layer contracts and curve direction are still required by the second pass; only
        /// the Y reference changes. This prevents terrain/water drift from turning a valid captured
        /// pipe or cable snap into an unresolved operation.
        /// </summary>
        private bool TryResolveNativeEndpointWithLocalSurface(Entity prefab,
            NetEndpointIntent intent, NetPrefabInfo placedInfo,
            ref NodePool nodes, ref EdgePool edges,
            ref NodePool ownedNodes, ref EdgePool ownedEdges,
            ref TerrainHeightData heightData, ref WaterSurfaceData<SurfaceWater> waterData,
            bool allowMergedNodeSplit,
            out Entity target, out float splitT, out int kind, out bool usedLocalSurface)
        {
            usedLocalSurface = false;
            if (TryResolveNativeEndpoint(intent, placedInfo,
                    ref nodes, ref edges, ref ownedNodes, ref ownedEdges, allowMergedNodeSplit,
                    out target, out splitT, out kind))
                return true;

            if (intent.Kind != NetEndpointTargetKind.Node &&
                intent.Kind != NetEndpointTargetKind.Edge)
                return false;

            float3 sourcePoint = new float3(intent.PosX, intent.PosY, intent.PosZ);
            float2 sourceElevation = new float2(intent.ElevationLeft, intent.ElevationRight);
            float3 projected;
            if (!TryProjectUtilityEndpointToLocalSurface(prefab, placedInfo, sourcePoint,
                    sourceElevation, ref heightData, ref waterData, out projected))
                return false;

            float deltaY = projected.y - intent.PosY;
            intent.PosY = projected.y;
            intent.AnchorY += deltaY;
            if (intent.Kind == NetEndpointTargetKind.Edge)
            {
                intent.TargetAy += deltaY;
                intent.TargetBy += deltaY;
                intent.TargetCy += deltaY;
                intent.TargetDy += deltaY;
            }

            bool resolved = TryResolveNativeEndpoint(intent, placedInfo,
                ref nodes, ref edges, ref ownedNodes, ref ownedEdges, allowMergedNodeSplit,
                out target, out splitT, out kind);
            usedLocalSurface = resolved;
            return resolved;
        }

        /// <summary>
        /// Last-resort match for a <see cref="NetEndpointTargetKind.Node"/> target this machine no
        /// longer has as a node. Node reduction merges a node with two compatible edges back into a
        /// single edge as a local side-effect, so the same junction point is legitimately a node on
        /// the source and mid-span here; tapping that edge at the captured anchor rebuilds it.
        /// Identity stays strict — same prefab name, layer contract and owner — so a parallel road
        /// is never split in place of the real target.
        /// </summary>
        private bool TryResolveMergedNodeAsEdgeSplit(NetEndpointIntent intent,
            NetPrefabInfo placedInfo, ref EdgePool edges,
            out Entity target, out float splitT, out int kind)
        {
            target = Entity.Null;
            splitT = 0f;
            kind = KindSplit;
            // Without a source prefab there is no portable identity left to match on.
            if (string.IsNullOrEmpty(intent.TargetPrefabName)) return false;

            float3 anchor = new float3(intent.AnchorX, intent.AnchorY, intent.AnchorZ);
            float best = float.MaxValue;
            Entity bestEdge = Entity.Null;
            float bestT = 0f;
            NetCellIndex.Enumerator candidates = edges.Index.Near(anchor.xz, NativeNodeResolveXZ);
            while (candidates.MoveNext())
            {
                int i = candidates.Current;
                float t;
                float xz = MathUtils.Distance(edges.Curves[i].m_Bezier.xz, anchor.xz, out t);
                if (xz > NativeNodeResolveXZ) continue;
                float3 projected = MathUtils.Position(edges.Curves[i].m_Bezier, t);
                float dy = math.abs(projected.y - anchor.y);
                if (dy > NativeTargetResolveY) continue;

                Entity edge = edges.Entities[i];
                if (!EntityManager.Exists(edge) || EntityManager.HasComponent<Deleted>(edge)) continue;
                if (!EntityManager.HasComponent<PrefabRef>(edge)) continue;
                Entity targetPrefab = EntityManager.GetComponentData<PrefabRef>(edge).m_Prefab;
                if (!LayersCanConnect(placedInfo, NetInfoOf(targetPrefab))) continue;
                if (!string.Equals(PrefabNameOf(targetPrefab), intent.TargetPrefabName,
                        System.StringComparison.Ordinal)) continue;
                if (!TargetContractMatches(edge, intent) || !TargetOwnerMatches(edge, intent)) continue;

                float score = xz * xz + dy * dy * 0.25f;
                if (score >= best) continue;
                best = score;
                bestEdge = edge;
                bestT = t;
            }
            if (bestEdge == Entity.Null) return false;

            // The survivor's own end node carries a different prefab at a mixed junction, so it can
            // be the source's node even when the node search rejected it on prefab identity.
            Bezier4x3 bestCurve = EntityManager.GetComponentData<Curve>(bestEdge).m_Bezier;
            Edge bestEdgeData = EntityManager.GetComponentData<Edge>(bestEdge);
            float3 projectedBest = MathUtils.Position(bestCurve, bestT);
            if (math.distance(projectedBest.xz, bestCurve.a.xz) <= ExistingSplitNodeDistance)
            {
                target = bestEdgeData.m_Start;
                kind = KindReuseNode;
                return IsLiveTargetNode(target);
            }
            if (math.distance(projectedBest.xz, bestCurve.d.xz) <= ExistingSplitNodeDistance)
            {
                target = bestEdgeData.m_End;
                kind = KindReuseNode;
                return IsLiveTargetNode(target);
            }

            target = bestEdge;
            splitT = bestT;
            kind = KindSplit;
            return true;
        }

        private const float CoincidentNodeXZ = 0.25f;

        private Entity FindCoincidentNode(NetEndpointIntent intent, NetPrefabInfo placedInfo,
            ref NodePool nodes)
        {
            float3 position = new float3(intent.PosX, intent.PosY, intent.PosZ);
            float best = CoincidentNodeXZ * CoincidentNodeXZ;
            Entity result = Entity.Null;
            NetCellIndex.Enumerator candidates =
                nodes.Index.Near(position.xz, CoincidentNodeXZ);
            while (candidates.MoveNext())
            {
                int i = candidates.Current;
                // Geometry first: the per-candidate liveness and layer lookups are main-thread
                // component reads, and running them ahead of the range test made one endpoint cost
                // the whole city.
                if (math.abs(nodes.Data[i].m_Position.y - position.y) > 1f) continue;
                float distance = math.distancesq(nodes.Data[i].m_Position.xz, position.xz);
                if (distance >= best) continue;
                Entity entity = nodes.Entities[i];
                if (!EntityManager.Exists(entity) || EntityManager.HasComponent<Deleted>(entity) ||
                    IsNodeBeingDeleted(entity)) continue;
                if (EntityManager.HasComponent<PrefabRef>(entity))
                {
                    NetPrefabInfo targetInfo = NetInfoOf(EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab);
                    if (!LayersCanConnect(placedInfo, targetInfo)) continue;
                }
                best = distance;
                result = entity;
            }
            return result;
        }

        private Entity FindNativeNode(NetEndpointIntent intent, NetPrefabInfo placedInfo,
            ref NodePool nodes)
        {
            float3 anchor = new float3(intent.AnchorX, intent.AnchorY, intent.AnchorZ);
            float best = float.MaxValue;
            Entity result = Entity.Null;
            NetCellIndex.Enumerator candidates =
                nodes.Index.Near(anchor.xz, NativeNodeResolveXZ);
            while (candidates.MoveNext())
            {
                int i = candidates.Current;
                float xz = math.distance(nodes.Data[i].m_Position.xz, anchor.xz);
                float dy = math.abs(nodes.Data[i].m_Position.y - anchor.y);
                if (xz > NativeNodeResolveXZ || dy > NativeTargetResolveY) continue;
                float score = xz * xz + dy * dy * 0.25f;
                if (score >= best) continue;

                Entity entity = nodes.Entities[i];
                if (!EntityManager.Exists(entity) || EntityManager.HasComponent<Deleted>(entity) ||
                    IsNodeBeingDeleted(entity)) continue;

                bool prefabMatch = TargetPrefabMatches(entity, intent.TargetPrefabName);
                if (!prefabMatch || !TargetContractMatches(entity, intent) ||
                    !TargetOwnerMatches(entity, intent)) continue;
                if (EntityManager.HasComponent<PrefabRef>(entity))
                {
                    NetPrefabInfo targetInfo = NetInfoOf(EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab);
                    if (!LayersCanConnect(placedInfo, targetInfo)) continue;
                }

                best = score;
                result = entity;
            }
            return result;
        }

        private bool TryFindNativeEdge(NetEndpointIntent intent, NetPrefabInfo placedInfo,
            ref EdgePool edges,
            out Entity target, out float splitT, out int kind)
        {
            float3 anchor = new float3(intent.AnchorX, intent.AnchorY, intent.AnchorZ);
            Bezier4x3 source = TargetCurveOf(intent);
            float sourceT = math.clamp(intent.SplitPosition, 0f, 1f);
            float2 sourceTangent = math.normalizesafe(MathUtils.Tangent(source, sourceT).xz);

            float best = float.MaxValue;
            Entity bestEdge = Entity.Null;
            float bestT = 0f;
            NetCellIndex.Enumerator candidates =
                edges.Index.Near(anchor.xz, NativeEdgeResolveXZ);
            while (candidates.MoveNext())
            {
                int i = candidates.Current;
                float t;
                float xz = MathUtils.Distance(edges.Curves[i].m_Bezier.xz, anchor.xz, out t);
                if (xz > NativeEdgeResolveXZ) continue;
                float3 projected = MathUtils.Position(edges.Curves[i].m_Bezier, t);
                float dy = math.abs(projected.y - anchor.y);
                if (dy > NativeTargetResolveY) continue;

                Entity edge = edges.Entities[i];
                if (!EntityManager.Exists(edge) || EntityManager.HasComponent<Deleted>(edge)) continue;

                Entity targetPrefab = Entity.Null;
                NetPrefabInfo targetInfo = default(NetPrefabInfo);
                if (EntityManager.HasComponent<PrefabRef>(edge))
                {
                    targetPrefab = EntityManager.GetComponentData<PrefabRef>(edge).m_Prefab;
                    targetInfo = NetInfoOf(targetPrefab);
                    if (!LayersCanConnect(placedInfo, targetInfo)) continue;
                }

                float2 tangent = math.normalizesafe(MathUtils.Tangent(edges.Curves[i].m_Bezier, t).xz);
                float alignment = math.lengthsq(sourceTangent) < 0.001f || math.lengthsq(tangent) < 0.001f
                    ? 1f
                    : math.abs(math.dot(sourceTangent, tangent));
                if (alignment < 0.35f) continue;

                bool prefabMatch = targetPrefab != Entity.Null &&
                    string.Equals(PrefabNameOf(targetPrefab), intent.TargetPrefabName,
                        System.StringComparison.Ordinal);
                // An explicit source prefab is part of the portable edge identity. A nearby
                // parallel road of another type is not an acceptable fallback: splitting it makes
                // the receivers permanently disagree about which carriageway owns the junction.
                if (!string.IsNullOrEmpty(intent.TargetPrefabName) && !prefabMatch) continue;
                if (!TargetContractMatches(edge, intent) || !TargetOwnerMatches(edge, intent)) continue;
                float score = xz * xz + dy * dy * 0.25f + (1f - alignment) * 16f;
                if (score >= best) continue;
                best = score;
                bestEdge = edge;
                // When the receiver still has the exact source curve, preserve the captured split
                // parameter instead of projecting the anchor and introducing solver-rounding drift.
                Bezier4x3 localCurve = edges.Curves[i].m_Bezier;
                if (SameCurveBits(localCurve, source)) bestT = sourceT;
                else if (SameCurveBitsReversed(localCurve, source)) bestT = 1f - sourceT;
                else bestT = t;
            }

            if (bestEdge == Entity.Null)
            {
                target = Entity.Null;
                splitT = 0f;
                kind = KindSplit;
                return false;
            }

            // The source targeted an edge interior, but this receiver may already have an equivalent
            // split at that anchor. Reuse its existing endpoint node rather than asking the generator
            // to split a local sub-edge at t=0/1.
            Bezier4x3 bestCurve = EntityManager.GetComponentData<Curve>(bestEdge).m_Bezier;
            Edge bestEdgeData = EntityManager.GetComponentData<Edge>(bestEdge);
            float3 projectedBest = MathUtils.Position(bestCurve, bestT);
            if (math.distance(projectedBest.xz, bestCurve.a.xz) <= ExistingSplitNodeDistance)
            {
                target = bestEdgeData.m_Start;
                splitT = 0f;
                kind = KindReuseNode;
                return IsLiveTargetNode(target);
            }
            if (math.distance(projectedBest.xz, bestCurve.d.xz) <= ExistingSplitNodeDistance)
            {
                target = bestEdgeData.m_End;
                splitT = 0f;
                kind = KindReuseNode;
                return IsLiveTargetNode(target);
            }

            target = bestEdge;
            splitT = bestT;
            kind = KindSplit;
            return true;
        }

        private bool IsLiveTargetNode(Entity node)
        {
            return node != Entity.Null && EntityManager.Exists(node) &&
                   !EntityManager.HasComponent<Deleted>(node) && !IsNodeBeingDeleted(node);
        }

        private bool TargetPrefabMatches(Entity entity, string targetPrefabName)
        {
            if (string.IsNullOrEmpty(targetPrefabName)) return true;
            if (!EntityManager.HasComponent<PrefabRef>(entity)) return false;
            string local = PrefabNameOf(EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab);
            return string.Equals(local, targetPrefabName, System.StringComparison.Ordinal);
        }

        private static Bezier4x3 TargetCurveOf(NetEndpointIntent intent)
        {
            // A node target carries no curve, so two DIFFERENT source nodes would otherwise claim
            // one local edge with identical all-zero "curves" and pass the aliasing check that
            // exists to stop two Temps sharing one original.
            if (intent.Kind == NetEndpointTargetKind.Node ||
                intent.Kind == NetEndpointTargetKind.OwnedNode)
            {
                float3 anchor = new float3(intent.AnchorX, intent.AnchorY, intent.AnchorZ);
                return new Bezier4x3 { a = anchor, b = anchor, c = anchor, d = anchor };
            }
            return new Bezier4x3
            {
                a = new float3(intent.TargetAx, intent.TargetAy, intent.TargetAz),
                b = new float3(intent.TargetBx, intent.TargetBy, intent.TargetBz),
                c = new float3(intent.TargetCx, intent.TargetCy, intent.TargetCz),
                d = new float3(intent.TargetDx, intent.TargetDy, intent.TargetDz),
            };
        }

        private bool TargetContractMatches(Entity entity, NetEndpointIntent intent)
        {
            if (!EntityManager.HasComponent<PrefabRef>(entity)) return false;
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab;
            if (!EntityManager.HasComponent<NetData>(prefab)) return false;
            NetData data = EntityManager.GetComponentData<NetData>(prefab);
            return (uint)data.m_RequiredLayers == intent.TargetRequiredLayers &&
                   (uint)data.m_ConnectLayers == intent.TargetConnectLayers;
        }

        private bool TargetOwnerMatches(Entity entity, NetEndpointIntent intent)
        {
            bool wantsOwner = !string.IsNullOrEmpty(intent.OwnerPrefabName);
            Entity cursor = entity;
            Entity top = Entity.Null;
            for (int depth = 0; depth < 64 && EntityManager.HasComponent<Owner>(cursor); depth++)
            {
                Entity next = EntityManager.GetComponentData<Owner>(cursor).m_Owner;
                if (next == Entity.Null || next == cursor || !EntityManager.Exists(next)) return false;
                top = next;
                cursor = next;
            }
            if (!wantsOwner) return top == Entity.Null;
            if (top == Entity.Null || !EntityManager.HasComponent<PrefabRef>(top) ||
                !EntityManager.HasComponent<global::Game.Objects.Transform>(top)) return false;
            string prefabName = PrefabNameOf(EntityManager.GetComponentData<PrefabRef>(top).m_Prefab);
            if (!string.Equals(prefabName, intent.OwnerPrefabName,
                System.StringComparison.Ordinal)) return false;
            global::Game.Objects.Transform transform =
                EntityManager.GetComponentData<global::Game.Objects.Transform>(top);
            float3 wantedPosition = new float3(intent.OwnerX, intent.OwnerY, intent.OwnerZ);
            if (math.distancesq(transform.m_Position, wantedPosition) > 4f) return false;
            quaternion wantedRotation = new quaternion(intent.OwnerRotX, intent.OwnerRotY,
                intent.OwnerRotZ, intent.OwnerRotW);
            return math.abs(math.dot(transform.m_Rotation.value, wantedRotation.value)) >= 0.999f;
        }

        private static bool SameCurveBits(Bezier4x3 left, Bezier4x3 right)
        {
            return math.all(left.a == right.a) && math.all(left.b == right.b) &&
                   math.all(left.c == right.c) && math.all(left.d == right.d);
        }

        private static bool SameCurveBitsReversed(Bezier4x3 left, Bezier4x3 right)
        {
            return math.all(left.a == right.d) && math.all(left.b == right.c) &&
                   math.all(left.c == right.b) && math.all(left.d == right.a);
        }
    }
}
