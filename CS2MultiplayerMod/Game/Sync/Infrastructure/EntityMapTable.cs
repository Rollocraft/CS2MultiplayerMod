using System.Collections.Concurrent;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Infrastructure
{
    /// <summary>
    /// High-throughput concurrent entity lookup table mapping remote entity handles
    /// to local ECS entities with sub-microsecond lookups.
    /// </summary>
    public static class EntityMapTable
    {
        private static readonly ConcurrentDictionary<long, Entity> RemoteToLocal =
            new ConcurrentDictionary<long, Entity>();

        private static long MakeKey(int remoteIndex, int remoteVersion)
        {
            return ((long)remoteIndex << 32) | (uint)remoteVersion;
        }

        public static void Register(int remoteIndex, int remoteVersion, Entity localEntity)
        {
            RemoteToLocal[MakeKey(remoteIndex, remoteVersion)] = localEntity;
        }

        public static bool TryResolve(int remoteIndex, int remoteVersion, out Entity localEntity)
        {
            return RemoteToLocal.TryGetValue(MakeKey(remoteIndex, remoteVersion), out localEntity);
        }

        public static bool Unregister(int remoteIndex, int remoteVersion)
        {
            return RemoteToLocal.TryRemove(MakeKey(remoteIndex, remoteVersion), out _);
        }

        public static void UnregisterLocal(Entity localEntity)
        {
            foreach (var kvp in RemoteToLocal)
            {
                if (kvp.Value == localEntity)
                {
                    RemoteToLocal.TryRemove(kvp.Key, out _);
                }
            }
        }

        public static void PruneDeadEntities(EntityManager em)
        {
            foreach (var kvp in RemoteToLocal)
            {
                if (!em.Exists(kvp.Value))
                {
                    RemoteToLocal.TryRemove(kvp.Key, out _);
                }
            }
        }

        public static void Clear()
        {
            RemoteToLocal.Clear();
        }
    }
}
