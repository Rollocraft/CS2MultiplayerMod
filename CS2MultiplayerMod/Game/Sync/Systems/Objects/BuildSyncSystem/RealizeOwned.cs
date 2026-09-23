using Game.Common;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Infrastructure;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    public partial class BuildSyncSystem
    {
        /// <summary>
        /// Emits the object, lot and connection-net definitions linked by <see cref="OwnerDefinition"/>, as
        /// Permanent. ToolUpdate only. Permanent skips the apply pass, so <paramref name="attachParent"/>
        /// (a net object's road) is tagged here (see <see cref="NetAttachment"/>).
        /// </summary>
        private void RealizeObject(Entity prefab, float3 position, quaternion rotation, Entity attachParent,
            int randomSeed, float age, CreationFlags extraFlags = default(CreationFlags),
            bool simulationSpawn = false)
        {
            var random = new Unity.Mathematics.Random((uint)math.max(1, randomSeed));

            CreationFlags flags = CreationFlags.Permanent | extraFlags;
            if (attachParent != Entity.Null) flags |= CreationFlags.Attach;

            // 1) The building itself.
            Entity definition = EntityManager.CreateEntity();
            EntityManager.AddComponentData(definition, new CreationDefinition
            {
                m_Prefab = prefab,
                m_RandomSeed = randomSeed,
                m_Attached = attachParent,
                m_Flags = flags,
            });
            EntityManager.AddComponentData(definition, new ObjectDefinition
            {
                // -1: on the ground; anything else is mesh-attached or elevated.
                m_ParentMesh = -1,
                m_Position = position,
                m_Rotation = rotation,
                // No owner, so local space == world space.
                m_LocalPosition = position,
                m_LocalRotation = rotation,
                m_Scale = new float3(1f, 1f, 1f),
                m_Intensity = 1f,
                m_Age = age,
                m_Probability = 100,
                m_PrefabSubIndex = -1,
            });
            EntityManager.AddComponent<Updated>(definition);
            EntityManager.AddComponent<Deleted>(definition); // CleanupSystem frees the definition once consumed.

            // 2) + 3) Sub-elements link back to the building by prefab + transform.
            var owner = new OwnerDefinition
            {
                m_Prefab = prefab,
                m_Position = position,
                m_Rotation = rotation,
            };
            RealizeOwnedSubElements(prefab, owner, ref random, simulationSpawn: simulationSpawn);

            // The parent's composition is re-selected only when Updated, and nothing else tags it here.
            if (attachParent != Entity.Null) NetAttachment.TagParentUpdated(EntityManager, attachParent);
        }

        /// <summary>
        /// Builds a zoning-grown building: the same object definition with the Construction flag, but its
        /// connection nets use the spawner's recipe (<paramref name="simulationSpawn"/>).
        /// <paramref name="randomSeed"/> becomes its PseudoRandomSeed so the variant matches.
        /// </summary>
        internal void RealizeSimulationBuilding(Entity prefab, float3 position, quaternion rotation,
            int randomSeed, bool underConstruction)
        {
            RealizeObject(prefab, position, rotation, Entity.Null, randomSeed, 0f,
                underConstruction ? CreationFlags.Construction : default(CreationFlags),
                simulationSpawn: true);
        }

        /// <summary>
        /// Emits a prefab's lot areas and connection nets. <paramref name="lotOwner"/> is the building
        /// whose lot surface the nets follow (the host, for an upgrade), or null for terrain.
        /// </summary>
        internal void RealizeOwnedSubElements(Entity prefab, OwnerDefinition owner,
            ref Unity.Mathematics.Random random, Entity lotOwner = default(Entity),
            bool simulationSpawn = false)
        {
            RealizeSubAreas(prefab, owner, Entity.Null, ref random);
            RealizeSubNets(prefab, owner, Entity.Null, lotOwner, simulationSpawn, ref random);
        }

        internal void RealizeOwnedSubElements(Entity prefab, Entity ownerEntity,
            global::Game.Objects.Transform ownerTransform, ref Unity.Mathematics.Random random,
            Entity lotOwner = default(Entity))
        {
            PrefabRef ownerPrefab = EntityManager.GetComponentData<PrefabRef>(ownerEntity);
            var owner = new OwnerDefinition
            {
                m_Prefab = ownerPrefab.m_Prefab,
                m_Position = ownerTransform.m_Position,
                m_Rotation = ownerTransform.m_Rotation,
            };
            RealizeSubAreas(prefab, owner, ownerEntity, ref random);
            RealizeSubNets(prefab, owner, ownerEntity, lotOwner, simulationSpawn: false, ref random);
        }

        /// <summary>Lot/area definitions per <see cref="SubArea"/>, with placeholder prefabs resolved safely.</summary>
        private void RealizeSubAreas(Entity prefab, OwnerDefinition owner, Entity ownerEntity,
            ref Unity.Mathematics.Random random)
        {
            if (!EntityManager.HasBuffer<SubArea>(prefab)) return;
            DynamicBuffer<SubArea> subAreas = EntityManager.GetBuffer<SubArea>(prefab, isReadOnly: true);
            if (subAreas.Length == 0) return;
            DynamicBuffer<SubAreaNode> subAreaNodes = EntityManager.GetBuffer<SubAreaNode>(prefab, isReadOnly: true);

            NativeParallelHashMap<Entity, int> selectedSpawnables = default;
            try
            {
                for (int i = 0; i < subAreas.Length; i++)
                {
                    SubArea subArea = subAreas[i];
                    Entity areaPrefab = subArea.m_Prefab;

                    int seed;
                    if (EntityManager.HasBuffer<PlaceholderObjectElement>(areaPrefab))
                    {
                        DynamicBuffer<PlaceholderObjectElement> placeholders =
                            EntityManager.GetBuffer<PlaceholderObjectElement>(areaPrefab, isReadOnly: true);
                        // SelectAreaPrefab reads SpawnableObjectData unchecked: a missing one is a native crash.
                        if (!AllHaveSpawnableData(placeholders))
                        {
                            SyncLog.Warn(LogTopic.Buildings,
                                "BuildSync realize: a placeholder sub-area of '" +
                                _prefabSystem.GetPrefabName(prefab) +
                                "' has a candidate without SpawnableObjectData; skipping that area.");
                            continue;
                        }
                        if (!selectedSpawnables.IsCreated)
                            selectedSpawnables = new NativeParallelHashMap<Entity, int>(10, Allocator.Temp);
                        _spawnableObjectLookup.Update(this);
                        if (!global::Game.Areas.AreaUtils.SelectAreaPrefab(placeholders, _spawnableObjectLookup,
                                selectedSpawnables, ref random, out areaPrefab, out seed))
                            continue;
                    }
                    else
                    {
                        seed = random.NextInt();
                    }

                    // GenerateAreasSystem reads AreaData unchecked: a non-area prefab is a native crash.
                    if (!EntityManager.HasComponent<AreaData>(areaPrefab))
                    {
                        SyncLog.Warn(LogTopic.Buildings, "BuildSync realize: sub-area prefab '" +
                            _prefabSystem.GetPrefabName(areaPrefab) + "' of '" +
                            _prefabSystem.GetPrefabName(prefab) +
                            "' has no AreaData; skipping that area.");
                        continue;
                    }

                    Entity areaDef = EntityManager.CreateEntity();
                    EntityManager.AddComponentData(areaDef, new CreationDefinition
                    {
                        m_Prefab = areaPrefab,
                        m_Owner = ownerEntity,
                        m_RandomSeed = seed,
                        m_Flags = CreationFlags.Permanent,
                    });
                    EntityManager.AddComponent<Updated>(areaDef);
                    EntityManager.AddComponent<Deleted>(areaDef); // consumed this frame, swept at Cleanup
                    if (ownerEntity == Entity.Null) EntityManager.AddComponentData(areaDef, owner);

                    DynamicBuffer<global::Game.Areas.Node> nodes =
                        EntityManager.AddBuffer<global::Game.Areas.Node>(areaDef);
                    nodes.ResizeUninitialized(subArea.m_NodeRange.y - subArea.m_NodeRange.x + 1);
                    int src = ObjectToolBaseSystem.GetFirstNodeIndex(subAreaNodes, subArea.m_NodeRange);
                    int dst = 0;
                    for (int j = subArea.m_NodeRange.x; j <= subArea.m_NodeRange.y; j++)
                    {
                        float3 local = subAreaNodes[src].m_Position;
                        float3 world = global::Game.Objects.ObjectUtils.LocalToWorld(owner.m_Position, owner.m_Rotation, local);
                        int parentMesh = subAreaNodes[src].m_ParentMesh;
                        // float.MinValue = "follow the terrain"; a real height only when mesh-relative.
                        float elevation = math.select(float.MinValue, local.y, parentMesh >= 0);
                        nodes[dst] = new global::Game.Areas.Node(world, elevation);
                        dst++;
                        if (++src == subArea.m_NodeRange.y) src = subArea.m_NodeRange.x;
                    }
                }
            }
            finally
            {
                if (selectedSpawnables.IsCreated) selectedSpawnables.Dispose();
            }
        }

        /// <summary>Every candidate has <see cref="SpawnableObjectData"/>; empty is false.</summary>
        private bool AllHaveSpawnableData(DynamicBuffer<PlaceholderObjectElement> placeholders)
        {
            if (placeholders.Length == 0) return false;
            for (int i = 0; i < placeholders.Length; i++)
                if (!EntityManager.HasComponent<SpawnableObjectData>(placeholders[i].m_Object)) return false;
            return true;
        }
    }
}
