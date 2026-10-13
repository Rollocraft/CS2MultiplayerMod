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

        /// <summary>
        /// An independent top-level create or delete; runtime prefab checks still apply.
        /// <paramref name="allowOptional"/> accepts the brush's Optional mark (the validator may drop a
        /// colliding tree); only a sender that publishes the committed result instead of the graph may pass it,
        /// because a receiver placing these directly would keep the trees the validator dropped.
        /// </summary>
        public static bool IsIndependentObjectDefinition(ObjectToolDefinitionIntent definition,
            bool allowOptional = false)
        {
            if (definition == null || definition.Kind != ObjectToolDefinitionKind.Object ||
                !string.IsNullOrEmpty(definition.SubPrefabName) ||
                definition.Owner.Kind != PortableEntityKind.None ||
                definition.Attached.Kind != PortableEntityKind.None ||
                !string.IsNullOrEmpty(definition.AttachedPrefabName) ||
                definition.HasOwnerDefinition || definition.HasUpgraded) return false;

            uint flags = definition.CreationFlags;
            if (allowOptional) flags &= ~(uint)CreationFlags.Optional;
            bool placement = !definition.PrefabIsNull &&
                             definition.Original.Kind == PortableEntityKind.None &&
                             flags == 0u;
            bool deletion = definition.PrefabIsNull &&
                            definition.Original.Kind == PortableEntityKind.Object &&
                            flags == 1u;
            return placement || deletion;
        }
    }
}
