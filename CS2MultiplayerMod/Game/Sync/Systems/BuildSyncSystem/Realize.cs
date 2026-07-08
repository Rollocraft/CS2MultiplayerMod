using Colossal.Mathematics;
using Game.Common;
using Game.Prefabs;
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
    public partial class BuildSyncSystem
    {
        private void RealizeIncoming(MultiplayerSession session, long now)
        {
            SimulationCommandMessage message;
            while (_incoming.TryDequeue(out message))
            {
                // Our own placement coming back to us — already built locally.
                if (message.OriginPlayerId == session.LocalPlayerId) continue;

                ObjectPlacementCommand command;
                try { command = ObjectPlacementCommand.Decode(message.Body); }
                catch (System.Exception ex) { Mod.log.Warn("[MP] BuildSync: dropping malformed command: " + ex.Message); continue; }

                Entity prefab;
                if (!_prefabIndex.TryResolve(command.PrefabName, out prefab))
                {
                    Mod.log.Warn("[MP] BuildSync realize: unknown prefab '" + command.PrefabName +
                                 "' from player " + message.OriginPlayerId + "; skipping.");
                    continue;
                }

                var position = new float3(command.PosX, command.PosY, command.PosZ);
                var rotation = new quaternion(command.RotX, command.RotY, command.RotZ, command.RotW);

                // Remember it so our own detector treats the soon-to-appear object as a replica.
                _guard.Mark(ReplicationGuard.Key(command.PrefabName, position), now);
                try
                {
                    RealizeObject(prefab, position, rotation);
                    ConstructionCharger.ChargeObject(EntityManager, prefab, command.PrefabName);
                    Mod.Verbose("[MP] BuildSync realize: spawned '" + command.PrefabName + "' from player " +
                                message.OriginPlayerId + " at (" + position.x.ToString("F1") + "," +
                                position.z.ToString("F1") + ").");
                }
                catch (System.Exception ex)
                {
                    Mod.log.Error("[MP] BuildSync realize FAILED for '" + command.PrefabName + "': " + ex);
                }
            }
        }

        /// <summary>
        /// Emit three definition entities (object + lot per SubArea + net per SubNet) linked by
        /// <see cref="OwnerDefinition"/>, with <see cref="CreationFlags.Permanent"/> for direct build.
        /// Must run in ToolUpdate (see <see cref="SyncRealizeSystem"/>). Fixes prior recipe: m_ParentMesh=-1
        /// ground marker, local transform, sub-definitions.
        /// </summary>
        private void RealizeObject(Entity prefab, float3 position, quaternion rotation)
        {
            // Seed procedural detail from the world position so every machine realizing this same
            // placement derives identical variation (no shared seed travels over the wire).
            uint hash = math.hash(position);
            var random = new Unity.Mathematics.Random(hash == 0u ? 1u : hash);

            // 1) The building itself.
            Entity definition = EntityManager.CreateEntity();
            EntityManager.AddComponentData(definition, new CreationDefinition
            {
                m_Prefab = prefab,
                m_RandomSeed = random.NextInt(),
                m_Flags = CreationFlags.Permanent,
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
            RealizeSubAreas(prefab, owner, ref random);
            RealizeSubNets(prefab, owner, ref random);
        }

        /// <summary>
        /// Emit lot/area definitions per <see cref="SubArea"/>, terrain-following polygons from
        /// <see cref="SubAreaNode"/> buffer (local to world). Resolve placeholder prefabs via
        /// SelectAreaPrefab, guarded against missing <see cref="SpawnableObjectData"/>.
        /// </summary>
        private void RealizeSubAreas(Entity prefab, OwnerDefinition owner, ref Unity.Mathematics.Random random)
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
                        m_RandomSeed = seed,
                        m_Flags = CreationFlags.Permanent,
                    });
                    EntityManager.AddComponent<Updated>(areaDef);
                    EntityManager.AddComponentData(areaDef, owner);

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

        /// <summary>
        /// Emit connection-net definitions per <see cref="SubNet"/>, curves averaged at shared
        /// node indices, mirrored for left-hand traffic, transformed local to world, marked
        /// <see cref="CoursePosFlags.DisableMerge"/> to prevent node fusion.
        /// </summary>
        private void RealizeSubNets(Entity prefab, OwnerDefinition owner, ref Unity.Mathematics.Random random)
        {
            if (!EntityManager.HasBuffer<SubNet>(prefab)) return;
            DynamicBuffer<SubNet> subNets = EntityManager.GetBuffer<SubNet>(prefab, isReadOnly: true);
            if (subNets.Length == 0) return;

            // Average the curve endpoints that share a node index, so sub-nets meeting at a node agree
            // on one position (.w counts contributors; divide to get the mean).
            var nodePositions = new NativeList<float4>(subNets.Length * 2, Allocator.Temp);
            try
            {
                for (int i = 0; i < subNets.Length; i++)
                {
                    SubNet subNet = subNets[i];
                    if (subNet.m_NodeIndex.x >= 0)
                    {
                        while (nodePositions.Length <= subNet.m_NodeIndex.x) nodePositions.Add(default);
                        nodePositions[subNet.m_NodeIndex.x] += new float4(subNet.m_Curve.a, 1f);
                    }
                    if (subNet.m_NodeIndex.y >= 0)
                    {
                        while (nodePositions.Length <= subNet.m_NodeIndex.y) nodePositions.Add(default);
                        nodePositions[subNet.m_NodeIndex.y] += new float4(subNet.m_Curve.d, 1f);
                    }
                }
                for (int i = 0; i < nodePositions.Length; i++)
                    nodePositions[i] /= math.max(1f, nodePositions[i].w);

                bool lefthand = _cityConfig.leftHandTraffic;
                for (int k = 0; k < subNets.Length; k++)
                {
                    _netGeometryLookup.Update(this);
                    SubNet subNet = global::Game.Net.NetUtils.GetSubNet(subNets, k, lefthand, ref _netGeometryLookup);
                    // GenerateNodes/EdgesSystem read NetData/NetGeometryData[prefab] with NO existence
                    // check → a sub-net prefab missing them hard-crashes the game. Skip rather than risk it.
                    if (!EntityManager.HasComponent<NetData>(subNet.m_Prefab) ||
                        !EntityManager.HasComponent<NetGeometryData>(subNet.m_Prefab))
                    {
                        Mod.log.Warn("[MP] BuildSync realize: sub-net prefab '" +
                            _prefabSystem.GetPrefabName(subNet.m_Prefab) + "' of '" + _prefabSystem.GetPrefabName(prefab) +
                            "' lacks NetData/NetGeometryData; skipping that driveway.");
                        continue;
                    }
                    RealizeSubNetCourse(subNet.m_Prefab, subNet.m_Curve, subNet.m_NodeIndex,
                        subNet.m_ParentMesh, subNet.m_Upgrades, nodePositions, owner, ref random);
                }
            }
            finally
            {
                nodePositions.Dispose();
            }
        }

        private void RealizeSubNetCourse(Entity netPrefab, Bezier4x3 curve, int2 nodeIndex, int2 parentMesh,
            CompositionFlags upgrades, NativeList<float4> nodePositions, OwnerDefinition owner,
            ref Unity.Mathematics.Random random)
        {
            Entity netDef = EntityManager.CreateEntity();
            EntityManager.AddComponentData(netDef, new CreationDefinition
            {
                m_Prefab = netPrefab,
                m_RandomSeed = random.NextInt(),
                m_Flags = CreationFlags.Permanent,
            });
            EntityManager.AddComponent<Updated>(netDef);
            EntityManager.AddComponentData(netDef, owner);

            var course = default(NetCourse);
            course.m_Curve = global::Game.Objects.ObjectUtils.LocalToWorld(owner.m_Position, owner.m_Rotation, curve);

            course.m_StartPosition.m_Position = course.m_Curve.a;
            course.m_StartPosition.m_Rotation = global::Game.Net.NetUtils.GetNodeRotation(MathUtils.StartTangent(course.m_Curve), owner.m_Rotation);
            course.m_StartPosition.m_CourseDelta = 0f;
            course.m_StartPosition.m_Elevation = curve.a.y;
            course.m_StartPosition.m_ParentMesh = parentMesh.x;
            if (nodeIndex.x >= 0)
                course.m_StartPosition.m_Position = global::Game.Objects.ObjectUtils.LocalToWorld(owner.m_Position, owner.m_Rotation, nodePositions[nodeIndex.x].xyz);

            course.m_EndPosition.m_Position = course.m_Curve.d;
            course.m_EndPosition.m_Rotation = global::Game.Net.NetUtils.GetNodeRotation(MathUtils.EndTangent(course.m_Curve), owner.m_Rotation);
            course.m_EndPosition.m_CourseDelta = 1f;
            course.m_EndPosition.m_Elevation = curve.d.y;
            course.m_EndPosition.m_ParentMesh = parentMesh.y;
            if (nodeIndex.y >= 0)
                course.m_EndPosition.m_Position = global::Game.Objects.ObjectUtils.LocalToWorld(owner.m_Position, owner.m_Rotation, nodePositions[nodeIndex.y].xyz);

            course.m_Length = MathUtils.Length(course.m_Curve);
            course.m_FixedIndex = -1;
            course.m_StartPosition.m_Flags |= CoursePosFlags.IsFirst | CoursePosFlags.DisableMerge;
            course.m_EndPosition.m_Flags |= CoursePosFlags.IsLast | CoursePosFlags.DisableMerge;
            if (course.m_StartPosition.m_Position.Equals(course.m_EndPosition.m_Position))
            {
                course.m_StartPosition.m_Flags |= CoursePosFlags.IsLast;
                course.m_EndPosition.m_Flags |= CoursePosFlags.IsFirst;
            }
            EntityManager.AddComponentData(netDef, course);

            if (!upgrades.Equals(default(CompositionFlags)))
                EntityManager.AddComponentData(netDef, new global::Game.Net.Upgraded { m_Flags = upgrades });
        }
    }
}
