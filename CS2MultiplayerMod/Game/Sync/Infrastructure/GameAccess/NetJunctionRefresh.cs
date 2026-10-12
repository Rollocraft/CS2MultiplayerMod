using System.Collections.Generic;
using Game.Common;
using Game.Net;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Infrastructure
{
    internal static class NetJunctionRefresh
    {
        /// <summary>
        /// Lane regeneration resets endpoint PathNodes, which LaneReferencesSystem restores only for
        /// Updated nodes: an edge refresh must include both end nodes.
        /// </summary>
        public static int Refresh(EntityManager em, NativeArray<Edge> edges, HashSet<Entity> nodes)
        {
            nodes.Clear();
            for (int i = 0; i < edges.Length; i++)
            {
                Collect(em, edges[i].m_Start, nodes);
                Collect(em, edges[i].m_End, nodes);
            }

            foreach (Entity node in nodes) em.AddComponent<Updated>(node);
            return nodes.Count;
        }

        private static void Collect(EntityManager em, Entity node, HashSet<Entity> nodes)
        {
            if (node == Entity.Null || !em.Exists(node) || !em.HasComponent<Node>(node) ||
                em.HasComponent<Temp>(node) || em.HasComponent<Deleted>(node) ||
                em.HasComponent<Disabled>(node) || em.HasComponent<Updated>(node)) return;
            nodes.Add(node);
        }
    }
}
