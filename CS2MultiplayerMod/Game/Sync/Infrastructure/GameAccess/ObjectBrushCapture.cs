using Game.Prefabs;
using Game.Tools;
using Unity.Entities;
using CS2MultiplayerMod.Game.Sync.Commands;

namespace CS2MultiplayerMod.Game.Sync.Infrastructure
{
    /// <summary>Separates the object brush's display from the object edits it generates.</summary>
    internal static class ObjectBrushCapture
    {
        public static bool IsVisualDefinition(EntityManager em, Entity entity)
        {
            if (!em.HasComponent<BrushDefinition>(entity) ||
                em.HasComponent<ObjectDefinition>(entity) ||
                em.HasComponent<NetCourse>(entity) ||
                em.HasBuffer<global::Game.Areas.Node>(entity)) return false;

            // The brush display marker; only terraforming brushes edit the world.
            Entity tool = em.GetComponentData<BrushDefinition>(entity).m_Tool;
            return tool != Entity.Null && em.Exists(tool) &&
                   em.HasComponent<ObjectGeometryData>(tool) &&
                   !em.HasComponent<TerraformingData>(tool);
        }

        public static bool SuppressDeletes(bool nativeCaptured, bool lifecycleApplied,
            bool brushApplied) => nativeCaptured || (lifecycleApplied && !brushApplied);

        /// <summary>An independent top-level create or delete; runtime prefab checks still apply.</summary>
        public static bool IsIndependentObjectDefinition(ObjectToolDefinitionIntent definition)
        {
            if (definition == null || definition.Kind != ObjectToolDefinitionKind.Object ||
                !string.IsNullOrEmpty(definition.SubPrefabName) ||
                definition.Owner.Kind != PortableEntityKind.None ||
                definition.Attached.Kind != PortableEntityKind.None ||
                !string.IsNullOrEmpty(definition.AttachedPrefabName) ||
                definition.HasOwnerDefinition || definition.HasUpgraded) return false;

            bool placement = !definition.PrefabIsNull &&
                             definition.Original.Kind == PortableEntityKind.None &&
                             definition.CreationFlags == 0u;
            bool deletion = definition.PrefabIsNull &&
                            definition.Original.Kind == PortableEntityKind.Object &&
                            definition.CreationFlags == 1u;
            return placement || deletion;
        }
    }
}
