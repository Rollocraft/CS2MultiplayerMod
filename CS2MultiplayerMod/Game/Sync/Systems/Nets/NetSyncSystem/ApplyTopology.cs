using System.Collections.Generic;
using Game.Common;
using Game.Net;
using Game.Tools;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Systems.Net
{
    // Graph validation: originals, nodes, edges and their connectivity.
    public partial class NetSyncSystem
    {
        private bool ValidateTransactionOriginal(Entity entity, Temp temp, bool isNode, bool isEdge,
            HashSet<Entity> enabledTransactionConnections, out string reason)
        {
            reason = null;
            Entity original = temp.m_Original;
            if (original == Entity.Null) return true;
            if (!EntityManager.Exists(original) || EntityManager.HasComponent<Deleted>(original) ||
                EntityManager.HasComponent<Temp>(original))
            {
                reason = "a referenced original vanished between arm and commit";
                return false;
            }
            // Old edges Deleted inside the transaction are safe only with replacement connectivity.
            if (isNode)
            {
                bool replacesEdge = (temp.m_Flags & TempFlags.Replace) != 0 &&
                                    EntityManager.HasComponent<Edge>(original);
                if (!EntityManager.HasComponent<Node>(original) && !replacesEdge)
                {
                    reason = "a generated node has an invalid original type";
                    return false;
                }
                if (!replacesEdge && (temp.m_Flags & TempFlags.Delete) == 0 &&
                    IsNodeBeingDeleted(original) &&
                    !enabledTransactionConnections.Contains(entity))
                {
                    // Otherwise ApplyNetSystem consumes the node after its last real edge is gone.
                    reason = "a referenced original node is being torn down without replacement connectivity";
                    return false;
                }
                // A split node carries the original Edge with TempFlags.Replace; ApplyNetSystem splits with it.
            }
            if (isEdge && !EntityManager.HasComponent<Edge>(original))
            {
                reason = "a generated edge references a non-edge original";
                return false;
            }

            bool updatesOriginal = (temp.m_Flags & (TempFlags.Delete | TempFlags.Replace |
                                                    TempFlags.Combine)) == 0;
            if (updatesOriginal && (isNode || isEdge) &&
                !ValidateNetPrefabReference(entity, out reason)) return false;

            // The connectivity repair pass reads every referenced edge without checking Edge.
            if (isNode && updatesOriginal && EntityManager.HasBuffer<ConnectedEdge>(original))
            {
                DynamicBuffer<ConnectedEdge> edges =
                    EntityManager.GetBuffer<ConnectedEdge>(original, isReadOnly: true);
                for (int i = 0; i < edges.Length; i++)
                {
                    Entity edge = edges[i].m_Edge;
                    if (!EntityManager.Exists(edge) || !EntityManager.HasComponent<Edge>(edge))
                    {
                        reason = "an original node contains a stale connected-edge reference";
                        return false;
                    }
                }
            }

            if (isEdge && updatesOriginal &&
                !EntityManager.HasBuffer<ConnectedNode>(original))
            {
                reason = "an original edge has no connected-node buffer";
                return false;
            }
            return true;
        }

        private bool ValidateTempNode(Entity entity, Temp temp, out string reason)
        {
            reason = null;
            if ((temp.m_Flags & TempFlags.Delete) == 0 &&
                !ValidateNetPrefabReference(entity, out reason)) return false;
            return true;
        }

        private bool ValidateTempEdge(Entity entity, Temp temp, HashSet<Entity> members,
            HashSet<Entity> enabledTransactionConnections, out string reason)
        {
            reason = null;
            if ((temp.m_Flags & TempFlags.Delete) != 0) return true;
            if (!ValidateNetPrefabReference(entity, out reason)) return false;
            if (!EntityManager.HasBuffer<ConnectedNode>(entity))
            {
                reason = "a generated edge has no connected-node buffer";
                return false;
            }

            Edge edge = EntityManager.GetComponentData<Edge>(entity);
            if (!ValidateTempEndpoint(edge.m_Start, members,
                    enabledTransactionConnections, out reason) ||
                !ValidateTempEndpoint(edge.m_End, members,
                    enabledTransactionConnections, out reason)) return false;

            DynamicBuffer<ConnectedNode> nodes =
                EntityManager.GetBuffer<ConnectedNode>(entity, isReadOnly: true);
            for (int i = 0; i < nodes.Length; i++)
                if (!ValidateConnectedNodeForApply(nodes[i].m_Node,
                        enabledTransactionConnections, out reason)) return false;

            if (temp.m_Original != Entity.Null &&
                (temp.m_Flags & (TempFlags.Replace | TempFlags.Combine)) == 0)
            {
                DynamicBuffer<ConnectedNode> originalNodes =
                    EntityManager.GetBuffer<ConnectedNode>(temp.m_Original, isReadOnly: true);
                for (int i = 0; i < originalNodes.Length; i++)
                {
                    Entity node = originalNodes[i].m_Node;
                    if (!EntityManager.Exists(node) || !EntityManager.HasBuffer<ConnectedEdge>(node))
                    {
                        reason = "an original edge contains a stale connected-node reference";
                        return false;
                    }
                }
            }
            return true;
        }

        private bool ValidateTempEndpoint(Entity node, HashSet<Entity> members,
            HashSet<Entity> enabledTransactionConnections, out string reason)
        {
            if (node == Entity.Null || !members.Contains(node) || !EntityManager.Exists(node) ||
                !EntityManager.HasComponent<Temp>(node) || !EntityManager.HasComponent<Node>(node) ||
                EntityManager.HasComponent<Deleted>(node) || EntityManager.HasComponent<Disabled>(node) ||
                !EntityManager.HasBuffer<ConnectedEdge>(node))
            {
                reason = "a generated edge endpoint is outside the enabled Temp transaction";
                return false;
            }
            return ValidateConnectedNodeForApply(node, enabledTransactionConnections, out reason);
        }

        private bool ValidateConnectedNodeForApply(Entity node,
            HashSet<Entity> enabledTransactionConnections,
            out string reason)
        {
            reason = null;
            if (!EntityManager.Exists(node) || !EntityManager.HasComponent<Node>(node))
            {
                reason = "a generated edge contains a missing connected node";
                return false;
            }

            Entity effective = node;
            if (EntityManager.HasComponent<Temp>(node))
            {
                Temp nodeTemp = EntityManager.GetComponentData<Temp>(node);
                if (nodeTemp.m_Original != Entity.Null &&
                    (nodeTemp.m_Flags & (TempFlags.Delete | TempFlags.Replace)) == 0)
                    effective = nodeTemp.m_Original;
            }
            if (!EntityManager.Exists(effective) || EntityManager.HasComponent<Deleted>(effective) ||
                !EntityManager.HasBuffer<ConnectedEdge>(effective))
            {
                reason = "a generated edge resolves to a node without connectivity data";
                return false;
            }
            if (IsNodeBeingDeleted(effective) &&
                !enabledTransactionConnections.Contains(node) &&
                !enabledTransactionConnections.Contains(effective))
            {
                reason = "a generated edge resolves to a node being torn down";
                return false;
            }
            return true;
        }

        private HashSet<Entity> CollectEnabledTransactionConnections(HashSet<Entity> members)
        {
            var result = new HashSet<Entity>();
            if (members == null) return result;
            foreach (Entity candidate in members)
            {
                if (!EntityManager.Exists(candidate) ||
                    !EntityManager.HasComponent<Temp>(candidate) ||
                    !EntityManager.HasComponent<Edge>(candidate) ||
                    EntityManager.HasComponent<Deleted>(candidate) ||
                    EntityManager.HasComponent<Disabled>(candidate))
                    continue;

                Temp edgeTemp = EntityManager.GetComponentData<Temp>(candidate);
                if ((edgeTemp.m_Flags & TempFlags.Delete) != 0) continue;
                Edge edge = EntityManager.GetComponentData<Edge>(candidate);
                AddEffectiveTransactionConnection(result, edge.m_Start);
                AddEffectiveTransactionConnection(result, edge.m_End);
            }
            return result;
        }

        private void AddEffectiveTransactionConnection(HashSet<Entity> connections, Entity node)
        {
            if (node == Entity.Null) return;
            connections.Add(node);
            if (!EntityManager.Exists(node) || !EntityManager.HasComponent<Temp>(node)) return;

            Temp temp = EntityManager.GetComponentData<Temp>(node);
            if (temp.m_Original != Entity.Null &&
                (temp.m_Flags & (TempFlags.Delete | TempFlags.Replace)) == 0 &&
                EntityManager.Exists(temp.m_Original) &&
                EntityManager.HasComponent<Node>(temp.m_Original))
                connections.Add(temp.m_Original);
        }

        private bool ValidateNetPrefabReference(Entity entity, out string reason)
        {
            reason = null;
            if (!EntityManager.HasComponent<global::Game.Prefabs.PrefabRef>(entity))
            {
                reason = "a generated net entity has no prefab reference";
                return false;
            }
            Entity prefab = EntityManager.GetComponentData<global::Game.Prefabs.PrefabRef>(entity).m_Prefab;
            if (prefab == Entity.Null || !EntityManager.Exists(prefab) ||
                !EntityManager.HasComponent<global::Game.Prefabs.PrefabData>(prefab))
            {
                reason = "a generated net entity references a missing prefab";
                return false;
            }
            return true;
        }
    }
}
