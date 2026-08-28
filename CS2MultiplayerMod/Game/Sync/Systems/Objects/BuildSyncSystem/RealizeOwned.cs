using System.Text;
using Colossal.Mathematics;
using Game.Common;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Sync.Infrastructure;
using CS2MultiplayerMod.Game.Sync.Commands;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    // Creating the object itself and the sub-elements the game generates with it - the lot and its
    // areas - from the same owner definition the sender's tool produced.
    public partial class BuildSyncSystem
    {
        /// <summary>
        /// Emit three definition entities (object + lot per SubArea + net per SubNet) linked by
        /// <see cref="OwnerDefinition"/>, with <see cref="CreationFlags.Permanent"/> for direct build.
        /// Must run in ToolUpdate (see <see cref="SyncRealizeSystem"/>). Fixes prior recipe: m_ParentMesh=-1
        /// ground marker, local transform, sub-definitions.
        ///
        /// <paramref name="attachParent"/> is the road node or edge a net object hangs off (Null
        /// otherwise). Permanent skips the tool's apply pass, so the parent is tagged here instead -
        /// see <see cref="NetAttachment"/>.
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
                // -1 = sits on the ground (gets ElevationFlags.OnGround, no Elevation component);
                // any other value makes the game treat it as mesh-attached / elevated.
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

            // The composition that draws the ring, or applies the sign's restriction, is re-selected
            // only for Updated entities, and nothing else will tag them on this path. GenerateObjects
            // (M1) creates the object, AttachSystem (M3) files it under the parent, and
            // CompositionSelect reads it immediately after - all downstream of this ToolUpdate call.
            if (attachParent != Entity.Null) NetAttachment.TagParentUpdated(EntityManager, attachParent);
        }

        /// <summary>
        /// Builds a building the sending machine's zoning simulation grew. The spawner emits the
        /// same object definition a tool placement does - only the Construction flag differs, which
        /// is what puts it behind scaffolding instead of standing it up finished - but its owned
        /// connection nets follow a different recipe, so this path asks for that one (see
        /// <paramref name="simulationSpawn"/> on <see cref="RealizeSubNetCourse"/>).
        ///
        /// <paramref name="randomSeed"/> is the sender's variant seed and reaches the built entity
        /// as its PseudoRandomSeed, which is what makes the same house look the same on both
        /// machines. Called from ToolUpdate by <see cref="GrowableSyncSystem"/>.
        /// </summary>
        internal void RealizeSimulationBuilding(Entity prefab, float3 position, quaternion rotation,
            int randomSeed, bool underConstruction)
        {
            RealizeObject(prefab, position, rotation, Entity.Null, randomSeed, 0f,
                underConstruction ? CreationFlags.Construction : default(CreationFlags),
                simulationSpawn: true);
        }

        /// <summary>
        /// Emit a prefab's owned lot areas and connection nets.
        ///
        /// <paramref name="lotOwner"/> is the building whose lot surface the connection nets are laid
        /// on, or <see cref="Entity.Null"/> to lay them on the terrain. The tools pass the host
        /// building here for a service upgrade (the extension's paths belong on the host's lot) and
        /// nothing for a plain placement.
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

        /// <summary>
        /// Emit lot/area definitions per <see cref="SubArea"/>, terrain-following polygons from
        /// <see cref="SubAreaNode"/> buffer (local to world). Resolve placeholder prefabs via
        /// SelectAreaPrefab, guarded against missing <see cref="SpawnableObjectData"/>.
        /// </summary>
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
                        // SelectAreaPrefab reads SpawnableObjectData[candidate] with NO existence check —
                        // a candidate missing it is a hard (native) crash, not a catchable exception. Guard.
                        if (!AllHaveSpawnableData(placeholders))
                        {
                            Mod.log.Warn("[MP] BuildSync realize: a placeholder sub-area of '" +
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

                    // GenerateAreasSystem reads AreaData[prefab] with NO existence check → a non-area
                    // prefab here hard-crashes the game. Only emit a definition for a real area prefab.
                    if (!EntityManager.HasComponent<AreaData>(areaPrefab))
                    {
                        Mod.log.Warn("[MP] BuildSync realize: sub-area prefab '" +
                            _prefabSystem.GetPrefabName(areaPrefab) + "' of '" + _prefabSystem.GetPrefabName(prefab) +
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

        /// <summary>
        /// True only when every placeholder candidate carries <see cref="SpawnableObjectData"/>, which
        /// <c>AreaUtils.SelectAreaPrefab</c> dereferences without checking. Empty buffers return false
        /// (nothing to select).
        /// </summary>
        private bool AllHaveSpawnableData(DynamicBuffer<PlaceholderObjectElement> placeholders)
        {
            if (placeholders.Length == 0) return false;
            for (int i = 0; i < placeholders.Length; i++)
                if (!EntityManager.HasComponent<SpawnableObjectData>(placeholders[i].m_Object)) return false;
            return true;
        }
    }
}
