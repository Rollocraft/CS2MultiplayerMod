using Game.Tools;
using Unity.Collections;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Classifies native definition batches that create a new top-level object. Some network
    /// prefabs create an object as the owner of their complete course/sub-net graph; that batch is
    /// one object transaction even though the network tool produced it.
    /// </summary>
    internal static class NativeObjectGraph
    {
        internal static bool HasNewTopLevelObjectRoot(EntityManager entityManager,
            NativeArray<Entity> definitions)
        {
            for (int i = 0; i < definitions.Length; i++)
            {
                Entity entity = definitions[i];
                if (!entityManager.Exists(entity) ||
                    !entityManager.HasComponent<CreationDefinition>(entity) ||
                    !entityManager.HasComponent<ObjectDefinition>(entity) ||
                    entityManager.HasComponent<OwnerDefinition>(entity)) continue;

                CreationDefinition definition =
                    entityManager.GetComponentData<CreationDefinition>(entity);
                if (definition.m_Prefab == Entity.Null ||
                    definition.m_Original != Entity.Null ||
                    definition.m_Owner != Entity.Null) continue;

                CreationFlags flags = definition.m_Flags;
                if ((flags & (CreationFlags.Delete | CreationFlags.Relocate |
                              CreationFlags.Recreate | CreationFlags.Permanent)) == 0)
                    return true;
            }
            return false;
        }
    }
}
