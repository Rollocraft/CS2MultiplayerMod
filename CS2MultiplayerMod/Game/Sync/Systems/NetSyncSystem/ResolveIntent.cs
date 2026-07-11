using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Game.Sync.Commands;

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
            NativeArray<Entity> nodeEntities, NativeArray<Node> nodeData,
            NativeArray<Entity> edgeEntities, NativeArray<Curve> edgeCurves,
            NativeArray<Entity> ownedNodeEntities, NativeArray<Node> ownedNodeData,
            NativeArray<Entity> ownedEdgeEntities, NativeArray<Curve> ownedEdgeCurves,
            out Entity target, out float splitT, out int kind)
        {
            target = Entity.Null;
            splitT = 0f;
            switch (intent.Kind)
            {
                case NetEndpointTargetKind.Free:
                    if ((intent.Flags & (uint)global::Game.Tools.CoursePosFlags.DisableMerge) == 0)
                    {
                        target = FindCoincidentNode(intent, placedInfo, nodeEntities, nodeData);
                        if (target != Entity.Null)
                        {
                            kind = KindReuseNode;
                            return true;
                        }
                    }
                    kind = KindFree;
                    return true;
                case NetEndpointTargetKind.Node:
                    target = FindNativeNode(intent, placedInfo, nodeEntities, nodeData);
                    kind = KindReuseNode;
                    return target != Entity.Null;
                case NetEndpointTargetKind.OwnedNode:
                    target = FindNativeNode(intent, placedInfo, ownedNodeEntities, ownedNodeData);
                    kind = KindReuseConnector;
                    return target != Entity.Null;
                case NetEndpointTargetKind.Edge:
                    return TryFindNativeEdge(intent, placedInfo, edgeEntities, edgeCurves,
                        out target, out splitT, out kind);
                case NetEndpointTargetKind.OwnedEdge:
                    return TryFindNativeEdge(intent, placedInfo, ownedEdgeEntities, ownedEdgeCurves,
                        out target, out splitT, out kind);
                default:
                    kind = KindFree;
                    return false;
            }
        }

        private Entity FindCoincidentNode(NetEndpointIntent intent, NetPrefabInfo placedInfo,
            NativeArray<Entity> entities, NativeArray<Node> nodes)
        {
            float3 position = new float3(intent.PosX, intent.PosY, intent.PosZ);
            float best = 0.25f * 0.25f;
            Entity result = Entity.Null;
            for (int i = 0; i < nodes.Length; i++)
            {
                Entity entity = entities[i];
                if (!EntityManager.Exists(entity) || EntityManager.HasComponent<Deleted>(entity) ||
                    IsNodeBeingDeleted(entity) || math.abs(nodes[i].m_Position.y - position.y) > 1f) continue;
                float distance = math.distancesq(nodes[i].m_Position.xz, position.xz);
                if (distance >= best) continue;
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
            NativeArray<Entity> entities, NativeArray<Node> nodes)
        {
            float3 anchor = new float3(intent.AnchorX, intent.AnchorY, intent.AnchorZ);
            float best = float.MaxValue;
            Entity result = Entity.Null;
            for (int i = 0; i < nodes.Length; i++)
            {
                Entity entity = entities[i];
                if (!EntityManager.Exists(entity) || EntityManager.HasComponent<Deleted>(entity) ||
                    IsNodeBeingDeleted(entity)) continue;

                float xz = math.distance(nodes[i].m_Position.xz, anchor.xz);
                float dy = math.abs(nodes[i].m_Position.y - anchor.y);
                if (xz > NativeNodeResolveXZ || dy > NativeTargetResolveY) continue;

                bool prefabMatch = TargetPrefabMatches(entity, intent.TargetPrefabName);
                if (EntityManager.HasComponent<PrefabRef>(entity))
                {
                    NetPrefabInfo targetInfo = NetInfoOf(EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab);
                    if (!LayersCanConnect(placedInfo, targetInfo)) continue;
                }

                float score = xz * xz + dy * dy * 0.25f + (prefabMatch ? 0f : 25f);
                if (score >= best) continue;
                best = score;
                result = entity;
            }
            return result;
        }

        private bool TryFindNativeEdge(NetEndpointIntent intent, NetPrefabInfo placedInfo,
            NativeArray<Entity> edgeEntities, NativeArray<Curve> edgeCurves,
            out Entity target, out float splitT, out int kind)
        {
            float3 anchor = new float3(intent.AnchorX, intent.AnchorY, intent.AnchorZ);
            Bezier4x3 source = TargetCurveOf(intent);
            float sourceT = math.clamp(intent.SplitPosition, 0f, 1f);
            float2 sourceTangent = math.normalizesafe(MathUtils.Tangent(source, sourceT).xz);

            float best = float.MaxValue;
            Entity bestEdge = Entity.Null;
            float bestT = 0f;
            for (int i = 0; i < edgeCurves.Length; i++)
            {
                Entity edge = edgeEntities[i];
                if (!EntityManager.Exists(edge) || EntityManager.HasComponent<Deleted>(edge)) continue;

                Entity targetPrefab = Entity.Null;
                NetPrefabInfo targetInfo = default(NetPrefabInfo);
                if (EntityManager.HasComponent<PrefabRef>(edge))
                {
                    targetPrefab = EntityManager.GetComponentData<PrefabRef>(edge).m_Prefab;
                    targetInfo = NetInfoOf(targetPrefab);
                    if (!LayersCanConnect(placedInfo, targetInfo)) continue;
                }

                float t;
                float xz = MathUtils.Distance(edgeCurves[i].m_Bezier.xz, anchor.xz, out t);
                if (xz > NativeEdgeResolveXZ) continue;
                float3 projected = MathUtils.Position(edgeCurves[i].m_Bezier, t);
                float dy = math.abs(projected.y - anchor.y);
                if (dy > NativeTargetResolveY) continue;

                float2 tangent = math.normalizesafe(MathUtils.Tangent(edgeCurves[i].m_Bezier, t).xz);
                float alignment = math.lengthsq(sourceTangent) < 0.001f || math.lengthsq(tangent) < 0.001f
                    ? 1f
                    : math.abs(math.dot(sourceTangent, tangent));
                if (alignment < 0.35f) continue;

                bool prefabMatch = targetPrefab != Entity.Null &&
                    string.Equals(PrefabNameOf(targetPrefab), intent.TargetPrefabName,
                        System.StringComparison.Ordinal);
                float score = xz * xz + dy * dy * 0.25f + (1f - alignment) * 16f +
                              (prefabMatch || string.IsNullOrEmpty(intent.TargetPrefabName) ? 0f : 25f);
                if (score >= best) continue;
                best = score;
                bestEdge = edge;
                bestT = t;
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
            return new Bezier4x3
            {
                a = new float3(intent.TargetAx, intent.TargetAy, intent.TargetAz),
                b = new float3(intent.TargetBx, intent.TargetBy, intent.TargetBz),
                c = new float3(intent.TargetCx, intent.TargetCy, intent.TargetCz),
                d = new float3(intent.TargetDx, intent.TargetDy, intent.TargetDz),
            };
        }
    }
}
