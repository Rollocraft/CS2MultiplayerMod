using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Game.Sync.Commands;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    // What a client is allowed to reproduce for specialized industry: which placements own an
    // area, which spawnables and placeholder attachments are compatible with the prefab, and
    // which references point at something moving and so must not be resolved against.
    public partial class BuildSyncSystem
    {
        /// <summary>
        /// Reject object-tool batches that attempt to create or manipulate entities owned by
        /// simulation spawning. Their prefab names are enough to decide this before resolving
        /// live entity references, so a forged mover target cannot stall the ordered retry queue.
        /// Existing growables may be referenced by a legitimate edit, but they may not be the
        /// newly-created object in a placement batch.
        /// </summary>
        private bool TryFindUnsafeSimulationReference(ObjectToolOperationCommand command,
            out string prefabName)
        {
            bool specializedPlacement = IsSpecializedIndustryPlacement(command);
            for (int i = 0; i < command.Definitions.Length; i++)
            {
                ObjectToolDefinitionIntent definition = command.Definitions[i];
                Entity prefab;

                if (!definition.PrefabIsNull &&
                    _prefabIndex.TryResolve(definition.PrefabName, out prefab))
                {
                    if (EntityManager.HasComponent<MovingObjectData>(prefab) ||
                        (definition.Kind == ObjectToolDefinitionKind.Object &&
                         definition.Original.Kind == PortableEntityKind.None &&
                         EntityManager.HasComponent<SpawnableBuildingData>(prefab) &&
                         !EntityManager.HasComponent<SignatureBuildingData>(prefab) &&
                         !(specializedPlacement &&
                           IsAllowedSpecializedSpawnable(command, i, prefab))))
                    {
                        prefabName = definition.PrefabName;
                        return true;
                    }
                }

                if (IsMovingPrefabName(definition.SubPrefabName, out prefabName) ||
                    IsMovingPrefabName(definition.AttachedPrefabName, out prefabName) ||
                    (definition.HasOwnerDefinition &&
                     IsMovingPrefabName(definition.OwnerDefinitionPrefabName, out prefabName)) ||
                    IsMovingPortableReference(definition.Original, out prefabName) ||
                    IsMovingPortableReference(definition.Owner, out prefabName) ||
                    IsMovingPortableReference(definition.Attached, out prefabName))
                    return true;

                if (definition.Kind == ObjectToolDefinitionKind.NetCourse &&
                    (IsMovingPortableReference(definition.NetCourse.Start.Entity, out prefabName) ||
                     IsMovingPortableReference(definition.NetCourse.End.Entity, out prefabName)))
                    return true;
            }

            prefabName = null;
            return false;
        }

        /// <summary>
        /// A specialized-industry placement is distinguishable from arbitrary growable creation:
        /// its new root owns an extractor/storage area declared by that root prefab. Some
        /// facilities use a placeholder root plus one level-one spawnable building attached to the
        /// placeholder prefab; older/direct variants use a spawnable root. Require the complete
        /// graph before exempting either exact form from the generic growable rejection.
        /// </summary>
        private bool IsSpecializedIndustryPlacement(ObjectToolOperationCommand command)
        {
            if (command == null || command.Definitions == null || command.RootIndex < 0 ||
                command.RootIndex >= command.Definitions.Length) return false;
            ObjectToolDefinitionIntent root = command.Definitions[command.RootIndex];
            if (root == null || root.Kind != ObjectToolDefinitionKind.Object || root.PrefabIsNull ||
                root.Original.Kind != PortableEntityKind.None ||
                root.Owner.Kind != PortableEntityKind.None ||
                root.Attached.Kind != PortableEntityKind.None ||
                !string.IsNullOrEmpty(root.AttachedPrefabName)) return false;

            CreationFlags rootFlags = (CreationFlags)root.CreationFlags;
            if ((rootFlags & (CreationFlags.Delete | CreationFlags.Relocate |
                              CreationFlags.Recreate | CreationFlags.Upgrade |
                              CreationFlags.Permanent)) != 0) return false;

            Entity rootPrefab;
            if (!_prefabIndex.TryResolve(root.PrefabName, out rootPrefab)) return false;
            bool directSpawnable =
                EntityManager.HasComponent<SpawnableBuildingData>(rootPrefab) &&
                !EntityManager.HasComponent<SignatureBuildingData>(rootPrefab);
            bool placeholder =
                EntityManager.HasComponent<PlaceholderBuildingData>(rootPrefab) &&
                EntityManager.HasComponent<BuildingData>(rootPrefab);
            if (!directSpawnable && !placeholder) return false;

            bool hasPlaceholderAttachment = false;
            if (placeholder)
            {
                for (int i = 0; i < command.Definitions.Length; i++)
                {
                    Entity candidatePrefab;
                    if (i != command.RootIndex &&
                        TryGetSpecializedPlaceholderAttachment(command, i, root,
                            rootPrefab, out candidatePrefab))
                    {
                        hasPlaceholderAttachment = true;
                        break;
                    }
                }
                if (!hasPlaceholderAttachment) return false;
            }

            for (int i = 0; i < command.Definitions.Length; i++)
                if (IsOwnedSpecializedAreaDefinition(command.Definitions[i], root, rootPrefab))
                    return true;
            return false;
        }

        /// <summary>
        /// One extractor/storage lot belonging to this placement's root. The polygon is not
        /// required to be a drawn ring: the game lets a player leave the area tool without
        /// drawing one, and the building then commits with the lot its prefab declares.
        /// </summary>
        private bool IsOwnedSpecializedAreaDefinition(ObjectToolDefinitionIntent area,
            ObjectToolDefinitionIntent root, Entity rootPrefab)
        {
            if (area == null || area.Kind != ObjectToolDefinitionKind.Area ||
                area.PrefabIsNull || !area.HasOwnerDefinition ||
                area.OwnerDefinitionPrefabName != root.PrefabName ||
                area.Original.Kind != PortableEntityKind.None ||
                area.Owner.Kind != PortableEntityKind.None ||
                area.Attached.Kind != PortableEntityKind.None ||
                !string.IsNullOrEmpty(area.AttachedPrefabName) ||
                area.CreationFlags != 0 || area.AreaNodes == null ||
                area.AreaNodes.Length == 0 ||
                area.AreaNodes.Length > ObjectToolOperationCommand.MaxAreaNodesPerDefinition)
                return false;

            float3 rootPosition = new float3(root.Object.PosX, root.Object.PosY,
                root.Object.PosZ);
            float3 ownerPosition = new float3(area.OwnerDefinitionX,
                area.OwnerDefinitionY, area.OwnerDefinitionZ);
            if (math.distancesq(rootPosition, ownerPosition) > 0.01f) return false;
            float4 rootRotation = new float4(root.Object.RotX, root.Object.RotY,
                root.Object.RotZ, root.Object.RotW);
            float4 ownerRotation = new float4(area.OwnerDefinitionRotX,
                area.OwnerDefinitionRotY, area.OwnerDefinitionRotZ,
                area.OwnerDefinitionRotW);
            if (math.abs(math.dot(rootRotation, ownerRotation)) < 0.999f) return false;

            Entity areaPrefab;
            return _prefabIndex.TryResolve(area.PrefabName, out areaPrefab) &&
                   IsSpecializedAreaPrefab(areaPrefab) &&
                   PrefabDeclaresOwnedArea(rootPrefab, areaPrefab);
        }

        private bool IsAllowedSpecializedSpawnable(ObjectToolOperationCommand command,
            int definitionIndex, Entity definitionPrefab)
        {
            ObjectToolDefinitionIntent root = command.Definitions[command.RootIndex];
            Entity rootPrefab;
            if (!_prefabIndex.TryResolve(root.PrefabName, out rootPrefab)) return false;
            if (definitionIndex == command.RootIndex)
                return definitionPrefab == rootPrefab &&
                       EntityManager.HasComponent<SpawnableBuildingData>(rootPrefab);

            Entity attachmentPrefab;
            return TryGetSpecializedPlaceholderAttachment(command, definitionIndex,
                       root, rootPrefab, out attachmentPrefab) &&
                   attachmentPrefab == definitionPrefab;
        }

        private bool TryGetSpecializedPlaceholderAttachment(
            ObjectToolOperationCommand command, int definitionIndex,
            ObjectToolDefinitionIntent root, Entity rootPrefab, out Entity attachmentPrefab)
        {
            attachmentPrefab = Entity.Null;
            if (definitionIndex < 0 || definitionIndex >= command.Definitions.Length ||
                rootPrefab == Entity.Null ||
                !EntityManager.HasComponent<PlaceholderBuildingData>(rootPrefab))
                return false;

            ObjectToolDefinitionIntent definition =
                command.Definitions[definitionIndex];
            if (definition == null ||
                definition.Kind != ObjectToolDefinitionKind.Object ||
                definition.PrefabIsNull ||
                definition.Original.Kind != PortableEntityKind.None ||
                definition.Owner.Kind != PortableEntityKind.None ||
                definition.Attached.Kind != PortableEntityKind.None ||
                definition.HasOwnerDefinition ||
                definition.AttachedPrefabName != root.PrefabName ||
                definition.CreationFlags != (uint)CreationFlags.Attach ||
                !_prefabIndex.TryResolve(definition.PrefabName,
                    out attachmentPrefab))
                return false;

            return IsCompatiblePlaceholderAttachment(definition,
                attachmentPrefab, rootPrefab);
        }

        private bool IsCompatiblePlaceholderAttachment(
            ObjectToolDefinitionIntent definition, Entity attachmentPrefab,
            Entity placeholderPrefab)
        {
            if (definition == null ||
                definition.Kind != ObjectToolDefinitionKind.Object ||
                ((CreationFlags)definition.CreationFlags &
                 CreationFlags.Attach) == 0)
                return false;

            return IsCompatiblePlaceholderAttachmentPrefab(attachmentPrefab,
                placeholderPrefab);
        }

        /// <summary>
        /// The prefab relationship used by a specialized-industry placeholder and its visible
        /// level-one building. Kept separate from the transient definition checks above so the
        /// same relationship can identify the committed live graph at ModificationEnd.
        /// </summary>
        private bool IsCompatiblePlaceholderAttachmentPrefab(Entity attachmentPrefab,
            Entity placeholderPrefab)
        {
            if (attachmentPrefab == Entity.Null ||
                placeholderPrefab == Entity.Null ||
                !EntityManager.HasComponent<PrefabData>(attachmentPrefab) ||
                !EntityManager.HasComponent<ObjectData>(attachmentPrefab) ||
                !EntityManager.HasComponent<SpawnableBuildingData>(attachmentPrefab) ||
                !EntityManager.HasComponent<BuildingData>(attachmentPrefab) ||
                !EntityManager.HasComponent<PrefabData>(placeholderPrefab) ||
                !EntityManager.HasComponent<ObjectData>(placeholderPrefab) ||
                !EntityManager.HasComponent<PlaceholderBuildingData>(placeholderPrefab) ||
                !EntityManager.HasComponent<BuildingData>(placeholderPrefab))
                return false;

            SpawnableBuildingData attachment =
                EntityManager.GetComponentData<SpawnableBuildingData>(
                    attachmentPrefab);
            PlaceholderBuildingData placeholder =
                EntityManager.GetComponentData<PlaceholderBuildingData>(
                    placeholderPrefab);
            if (attachment.m_Level != 1 ||
                attachment.m_ZonePrefab == Entity.Null ||
                placeholder.m_ZonePrefab == Entity.Null ||
                !EntityManager.HasComponent<ZoneData>(attachment.m_ZonePrefab) ||
                !EntityManager.HasComponent<ZoneData>(placeholder.m_ZonePrefab))
                return false;

            ZoneData attachmentZone =
                EntityManager.GetComponentData<ZoneData>(attachment.m_ZonePrefab);
            ZoneData placeholderZone =
                EntityManager.GetComponentData<ZoneData>(placeholder.m_ZonePrefab);
            if (!attachmentZone.m_ZoneType.Equals(
                    placeholderZone.m_ZoneType))
                return false;

            BuildingData attachmentBuilding =
                EntityManager.GetComponentData<BuildingData>(attachmentPrefab);
            BuildingData placeholderBuilding =
                EntityManager.GetComponentData<BuildingData>(placeholderPrefab);
            return math.all(attachmentBuilding.m_LotSize <=
                            placeholderBuilding.m_LotSize);
        }

        /// <summary>
        /// True for a committed spawnable building that belongs to a player-placed specialized
        /// industry graph. Placeholder variants attach their visible level-one building to the
        /// placeholder; direct variants declare the extractor/storage area on the spawnable root.
        /// </summary>
        private bool IsLiveSpecializedIndustrySpawnable(Entity entity, Entity prefab)
        {
            if (entity == Entity.Null || prefab == Entity.Null ||
                !EntityManager.Exists(entity) || !EntityManager.Exists(prefab) ||
                !EntityManager.HasComponent<SpawnableBuildingData>(prefab)) return false;

            // The prefab declaration alone is not origin evidence: a future simulation spawner
            // could legitimately choose a spawnable which also declares an area. Direct variants
            // count as placed only when this live instance actually owns the specialized area
            // graph produced by the object tool.
            if (PrefabDeclaresSpecializedArea(prefab) &&
                HasLiveOwnedSpecializedArea(entity)) return true;
            if (!EntityManager.HasComponent<global::Game.Objects.Attached>(entity)) return false;

            Entity parent = EntityManager
                .GetComponentData<global::Game.Objects.Attached>(entity).m_Parent;
            if (parent == Entity.Null || parent == entity || !EntityManager.Exists(parent))
                return false;

            // Prefab-local attachment definitions initially name the placeholder prefab itself;
            // after owner resolution the same relationship may name its live instance.
            Entity parentPrefab = Entity.Null;
            if (EntityManager.HasComponent<PrefabData>(parent))
                parentPrefab = parent;
            else if (EntityManager.HasComponent<PrefabRef>(parent))
                parentPrefab = EntityManager.GetComponentData<PrefabRef>(parent).m_Prefab;

            return parentPrefab != Entity.Null &&
                   IsCompatiblePlaceholderAttachmentPrefab(prefab, parentPrefab) &&
                   PrefabDeclaresSpecializedArea(parentPrefab);
        }

        private bool HasLiveOwnedSpecializedArea(Entity owner)
        {
            if (owner == Entity.Null || !EntityManager.Exists(owner) ||
                !EntityManager.HasBuffer<global::Game.Areas.SubArea>(owner)) return false;

            DynamicBuffer<global::Game.Areas.SubArea> areas =
                EntityManager.GetBuffer<global::Game.Areas.SubArea>(owner, isReadOnly: true);
            for (int i = 0; i < areas.Length; i++)
            {
                Entity area = areas[i].m_Area;
                if (area == Entity.Null || !EntityManager.Exists(area) ||
                    !EntityManager.HasComponent<PrefabRef>(area)) continue;
                Entity areaPrefab = EntityManager.GetComponentData<PrefabRef>(area).m_Prefab;
                if (!IsSpecializedAreaPrefab(areaPrefab)) continue;

                Entity topOwner;
                if (TryFindTopOwner(area, out topOwner) && topOwner == owner) return true;
            }
            return false;
        }

        private static bool IsClosedAreaNodeRing(ObjectAreaNodeIntent[] nodes)
        {
            if (nodes == null || nodes.Length < 4 ||
                nodes.Length > ObjectToolOperationCommand.MaxAreaNodesPerDefinition) return false;
            ObjectAreaNodeIntent first = nodes[0];
            ObjectAreaNodeIntent last = nodes[nodes.Length - 1];
            return first.X == last.X && first.Y == last.Y && first.Z == last.Z;
        }

        private bool PrefabDeclaresOwnedArea(Entity objectPrefab, Entity areaPrefab)
        {
            if (!EntityManager.HasBuffer<SubArea>(objectPrefab)) return false;
            DynamicBuffer<SubArea> subAreas =
                EntityManager.GetBuffer<SubArea>(objectPrefab, isReadOnly: true);
            for (int i = 0; i < subAreas.Length; i++)
            {
                Entity declared = subAreas[i].m_Prefab;
                if (declared == areaPrefab) return true;
                if (declared == Entity.Null || !EntityManager.Exists(declared)) continue;
                if (!EntityManager.HasBuffer<PlaceholderObjectElement>(declared)) continue;
                DynamicBuffer<PlaceholderObjectElement> candidates =
                    EntityManager.GetBuffer<PlaceholderObjectElement>(declared, isReadOnly: true);
                for (int j = 0; j < candidates.Length; j++)
                    if (candidates[j].m_Object == areaPrefab) return true;
            }
            return false;
        }

        private bool PrefabDeclaresSpecializedArea(Entity objectPrefab)
        {
            if (objectPrefab == Entity.Null || !EntityManager.Exists(objectPrefab) ||
                !EntityManager.HasBuffer<SubArea>(objectPrefab)) return false;

            DynamicBuffer<SubArea> subAreas =
                EntityManager.GetBuffer<SubArea>(objectPrefab, isReadOnly: true);
            for (int i = 0; i < subAreas.Length; i++)
            {
                Entity declared = subAreas[i].m_Prefab;
                if (IsSpecializedAreaPrefab(declared)) return true;
                if (declared == Entity.Null || !EntityManager.Exists(declared) ||
                    !EntityManager.HasBuffer<PlaceholderObjectElement>(declared)) continue;

                DynamicBuffer<PlaceholderObjectElement> candidates =
                    EntityManager.GetBuffer<PlaceholderObjectElement>(declared, isReadOnly: true);
                for (int j = 0; j < candidates.Length; j++)
                    if (IsSpecializedAreaPrefab(candidates[j].m_Object)) return true;
            }
            return false;
        }

        private bool IsMovingPrefabName(string name, out string unsafeName)
        {
            unsafeName = null;
            if (string.IsNullOrEmpty(name)) return false;
            Entity prefab;
            if (!_prefabIndex.TryResolve(name, out prefab)) return false;
            if (!EntityManager.HasComponent<MovingObjectData>(prefab)) return false;
            unsafeName = name;
            return true;
        }

        private bool IsMovingPortableReference(PortableEntityRef reference, out string unsafeName)
        {
            unsafeName = null;
            if (reference.Kind == PortableEntityKind.None ||
                string.IsNullOrEmpty(reference.PrefabName)) return false;
            Entity prefab;
            if (!_prefabIndex.TryResolve(reference.PrefabName, out prefab) ||
                !EntityManager.HasComponent<MovingObjectData>(prefab)) return false;
            unsafeName = reference.PrefabName;
            return true;
        }
    }
}
