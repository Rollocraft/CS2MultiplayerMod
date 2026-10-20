using Game.Net;
using Game.Prefabs;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Infrastructure
{
    /// <summary>
    /// Which network a remote edit may land on. Pipes, subways and tunnels run on the same XZ line as
    /// the road above them, so a position match alone can pick the wrong level.
    /// </summary>
    internal static class NetLayerRules
    {
        /// <summary>The net tool only replaces an edge whose required layers overlap the new prefab's.</summary>
        public static bool CanReplace(EntityManager manager, Entity livePrefab, Entity newPrefab)
        {
            if (livePrefab == newPrefab) return true;
            return TryGetNetData(manager, livePrefab, out NetData live) &&
                   TryGetNetData(manager, newPrefab, out NetData next) &&
                   (live.m_RequiredLayers & next.m_RequiredLayers) != Layer.None;
        }

        /// <summary>Prefabs that can meet at one junction node, which may carry either prefab.</summary>
        public static bool CanShareNode(EntityManager manager, Entity first, Entity second)
        {
            if (first == second) return true;
            return TryGetNetData(manager, first, out NetData a) &&
                   TryGetNetData(manager, second, out NetData b) &&
                   NetUtils.CanConnect(a, b);
        }

        private static bool TryGetNetData(EntityManager manager, Entity prefab, out NetData data)
        {
            data = default(NetData);
            if (prefab == Entity.Null || !manager.Exists(prefab) ||
                !manager.HasComponent<NetData>(prefab)) return false;
            data = manager.GetComponentData<NetData>(prefab);
            return true;
        }
    }
}
