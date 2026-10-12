using Game.Areas;
using Game.Common;
using Game.Tools;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Infrastructure
{
    internal static class AreaPolygon
    {
        // Live polygons omit the closing vertex; creation definitions require it to set Complete.
        internal static void CloseDefinitionRing(DynamicBuffer<Node> nodes)
        {
            if (nodes.Length < 3 ||
                nodes[0].m_Position.Equals(nodes[nodes.Length - 1].m_Position)) return;
            nodes.Add(nodes[0]);
        }

        internal static bool Complete(EntityManager em, Entity entity)
        {
            Area area = em.GetComponentData<Area>(entity);
            if ((area.m_Flags & AreaFlags.Complete) != 0) return false;
            area.m_Flags |= AreaFlags.Complete;
            em.SetComponentData(entity, area);
            return true;
        }

        // Replicas saved without Complete lose their edit actions; only standalone districts qualify.
        internal static bool RepairDistrict(EntityManager em, Entity entity)
        {
            if (!em.Exists(entity) || !em.HasComponent<District>(entity) ||
                !em.HasComponent<Area>(entity) || !em.HasBuffer<Node>(entity) ||
                em.HasComponent<Temp>(entity) || em.HasComponent<Deleted>(entity) ||
                em.HasComponent<Created>(entity) || em.HasComponent<Hidden>(entity) ||
                em.HasComponent<Owner>(entity) || em.HasComponent<MapTile>(entity) ||
                em.GetBuffer<Node>(entity, true).Length < 3) return false;
            return Complete(em, entity);
        }
    }
}
