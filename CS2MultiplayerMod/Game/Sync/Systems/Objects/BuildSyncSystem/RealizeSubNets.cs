using Colossal.Mathematics;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Game.Diagnostics;
using Game.Common;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    public partial class BuildSyncSystem
    {
        /// <summary>
        /// Connection-net definitions per <see cref="SubNet"/>: shared node positions averaged, mirrored for
        /// left-hand traffic, local to world.
        /// </summary>
        private void RealizeSubNets(Entity prefab, OwnerDefinition owner, Entity ownerEntity,
            Entity lotOwner, bool simulationSpawn, ref Unity.Mathematics.Random random)
        {
            if (!EntityManager.HasBuffer<SubNet>(prefab)) return;
            DynamicBuffer<SubNet> subNets = EntityManager.GetBuffer<SubNet>(prefab, isReadOnly: true);
            if (subNets.Length == 0) return;

            // Settled height fields; the spawner recipe snaps to nothing and skips both reads.
            var heightData = default(TerrainHeightData);
            var waterData = default(WaterSurfaceData<SurfaceWater>);
            var lotInfo = default(global::Game.Buildings.BuildingUtils.LotInfo);
            bool hasLot = false;
            if (!simulationSpawn)
            {
                heightData = _terrainSystem.GetHeightData(waitForPending: true);
                waterData = _waterSystem.GetSurfaceData(out Unity.Jobs.JobHandle waterDeps);
                waterDeps.Complete();
                hasLot = TryGetOwnerLot(lotOwner, out lotInfo);
            }

            // Average endpoints sharing a node index (.w counts contributors).
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
                    // NetData/NetGeometryData are read unchecked: a missing one is a native crash.
                    if (!EntityManager.HasComponent<NetData>(subNet.m_Prefab) ||
                        !EntityManager.HasComponent<NetGeometryData>(subNet.m_Prefab))
                    {
                        SyncLog.Warn(LogTopic.Buildings, "BuildSync realize: sub-net prefab '" +
                            _prefabSystem.GetPrefabName(subNet.m_Prefab) + "' of '" +
                            _prefabSystem.GetPrefabName(prefab) +
                            "' lacks NetData/NetGeometryData; skipping that driveway.");
                        continue;
                    }
                    RealizeSubNetCourse(subNet.m_Prefab, subNet.m_Curve, subNet.m_NodeIndex,
                        subNet.m_ParentMesh, subNet.m_Upgrades, nodePositions, owner, ownerEntity,
                        ref heightData, ref waterData, ref lotInfo, hasLot, simulationSpawn,
                        ref random);
                }
            }
            finally
            {
                nodePositions.Dispose();
            }
        }

        /// <summary>The lot info the game derives; without a Lot the caller snaps to terrain.</summary>
        private bool TryGetOwnerLot(Entity lotOwner,
            out global::Game.Buildings.BuildingUtils.LotInfo lotInfo)
        {
            lotInfo = default(global::Game.Buildings.BuildingUtils.LotInfo);
            if (lotOwner == Entity.Null || !EntityManager.Exists(lotOwner) ||
                !EntityManager.HasComponent<global::Game.Buildings.Lot>(lotOwner) ||
                !EntityManager.HasComponent<global::Game.Objects.Transform>(lotOwner) ||
                !EntityManager.HasComponent<PrefabRef>(lotOwner)) return false;

            Entity ownerPrefab = EntityManager.GetComponentData<PrefabRef>(lotOwner).m_Prefab;
            if (!EntityManager.HasComponent<BuildingData>(ownerPrefab)) return false;

            _transformLookup.Update(this);
            _prefabRefLookup.Update(this);
            _objectGeometryLookup.Update(this);
            _buildingTerraformLookup.Update(this);
            _buildingExtensionLookup.Update(this);

            global::Game.Objects.Elevation elevation = default(global::Game.Objects.Elevation);
            if (EntityManager.HasComponent<global::Game.Objects.Elevation>(lotOwner))
                elevation = EntityManager.GetComponentData<global::Game.Objects.Elevation>(lotOwner);
            DynamicBuffer<global::Game.Buildings.InstalledUpgrade> upgrades =
                EntityManager.HasBuffer<global::Game.Buildings.InstalledUpgrade>(lotOwner)
                    ? EntityManager.GetBuffer<global::Game.Buildings.InstalledUpgrade>(
                        lotOwner, isReadOnly: true)
                    : default(DynamicBuffer<global::Game.Buildings.InstalledUpgrade>);

            lotInfo = global::Game.Buildings.BuildingUtils.CalculateLotInfo(
                new float2(EntityManager.GetComponentData<BuildingData>(ownerPrefab).m_LotSize) * 4f,
                EntityManager.GetComponentData<global::Game.Objects.Transform>(lotOwner),
                elevation,
                EntityManager.GetComponentData<global::Game.Buildings.Lot>(lotOwner),
                EntityManager.GetComponentData<PrefabRef>(lotOwner),
                upgrades, _transformLookup, _prefabRefLookup, _objectGeometryLookup,
                _buildingTerraformLookup, _buildingExtensionLookup, defaultNoSmooth: false,
                out bool hasExtensionLots);
            return true;
        }

        /// <summary>A shared node's world position; water nets use the water surface, except on the spawner recipe.</summary>
        private static float3 SharedSubNetNodePosition(float3 localPosition, OwnerDefinition owner,
            NetGeometryData netGeometry, bool simulationSpawn, ref TerrainHeightData heightData,
            ref WaterSurfaceData<SurfaceWater> waterData)
        {
            float3 world = global::Game.Objects.ObjectUtils.LocalToWorld(
                owner.m_Position, owner.m_Rotation, localPosition);
            if (simulationSpawn ||
                (netGeometry.m_Flags & global::Game.Net.GeometryFlags.OnWater) == 0) return world;
            world.y = global::Game.Simulation.WaterUtils.SampleHeight(ref waterData, ref heightData, world);
            return world;
        }

        /// <summary>
        /// <paramref name="simulationSpawn"/> selects the spawner recipe: prefab-local height, no snapping,
        /// merging disabled. A tool driveway joins its road; a grown building's must not.
        /// </summary>
        private void RealizeSubNetCourse(Entity netPrefab, Bezier4x3 curve, int2 nodeIndex, int2 parentMesh,
            CompositionFlags upgrades, NativeList<float4> nodePositions, OwnerDefinition owner,
            Entity ownerEntity, ref TerrainHeightData heightData,
            ref WaterSurfaceData<SurfaceWater> waterData,
            ref global::Game.Buildings.BuildingUtils.LotInfo lotInfo, bool hasLot,
            bool simulationSpawn, ref Unity.Mathematics.Random random)
        {
            Entity netDef = EntityManager.CreateEntity();
            EntityManager.AddComponentData(netDef, new CreationDefinition
            {
                m_Prefab = netPrefab,
                m_Owner = ownerEntity,
                m_RandomSeed = random.NextInt(),
                m_Flags = CreationFlags.Permanent,
            });
            EntityManager.AddComponent<Updated>(netDef);
            EntityManager.AddComponent<Deleted>(netDef); // consumed this frame, swept at Cleanup
            if (ownerEntity == Entity.Null) EntityManager.AddComponentData(netDef, owner);

            var course = default(NetCourse);
            // Tool recipe: ends not both mesh-relative snap to water, the host lot or terrain, then the
            // prefab-local height is re-applied as an offset.
            _netGeometryLookup.Update(this);
            NetGeometryData netGeometry = _netGeometryLookup.HasComponent(netPrefab)
                ? _netGeometryLookup[netPrefab]
                : default(NetGeometryData);
            bool bothEndsOnMesh = parentMesh.x >= 0 && parentMesh.y >= 0;
            var worldCurve = new global::Game.Net.Curve
            {
                m_Bezier = global::Game.Objects.ObjectUtils.LocalToWorld(
                    owner.m_Position, owner.m_Rotation, curve),
            };
            if (simulationSpawn) course.m_Curve = worldCurve.m_Bezier;
            else if ((netGeometry.m_Flags & global::Game.Net.GeometryFlags.OnWater) != 0)
            {
                curve.y = default(Bezier4x1);
                worldCurve.m_Bezier = global::Game.Objects.ObjectUtils.LocalToWorld(
                    owner.m_Position, owner.m_Rotation, curve);
                course.m_Curve = global::Game.Net.NetUtils.AdjustPosition(worldCurve,
                    fixedStart: false, linearMiddle: false, fixedEnd: false,
                    ref heightData, ref waterData).m_Bezier;
            }
            else if (!bothEndsOnMesh)
            {
                bool fixedStart = parentMesh.x >= 0;
                bool fixedEnd = parentMesh.y >= 0;
                bool linearMiddle = fixedStart || fixedEnd;
                if ((netGeometry.m_Flags & global::Game.Net.GeometryFlags.FlattenTerrain) != 0)
                {
                    if (hasLot)
                    {
                        course.m_Curve = global::Game.Net.NetUtils.AdjustPosition(worldCurve,
                            fixedStart, linearMiddle, fixedEnd, ref lotInfo).m_Bezier;
                        course.m_Curve.a.y += curve.a.y;
                        course.m_Curve.b.y += curve.b.y;
                        course.m_Curve.c.y += curve.c.y;
                        course.m_Curve.d.y += curve.d.y;
                    }
                    else course.m_Curve = worldCurve.m_Bezier;
                }
                else
                {
                    course.m_Curve = global::Game.Net.NetUtils.AdjustPosition(worldCurve,
                        fixedStart, linearMiddle, fixedEnd, ref heightData).m_Bezier;
                    course.m_Curve.a.y += curve.a.y;
                    course.m_Curve.b.y += curve.b.y;
                    course.m_Curve.c.y += curve.c.y;
                    course.m_Curve.d.y += curve.d.y;
                }
            }
            else course.m_Curve = worldCurve.m_Bezier;

            course.m_StartPosition.m_Position = course.m_Curve.a;
            course.m_StartPosition.m_Rotation = global::Game.Net.NetUtils.GetNodeRotation(MathUtils.StartTangent(course.m_Curve), owner.m_Rotation);
            course.m_StartPosition.m_CourseDelta = 0f;
            course.m_StartPosition.m_Elevation = curve.a.y;
            course.m_StartPosition.m_ParentMesh = parentMesh.x;
            if (nodeIndex.x >= 0)
                course.m_StartPosition.m_Position = SharedSubNetNodePosition(
                    nodePositions[nodeIndex.x].xyz, owner, netGeometry, simulationSpawn,
                    ref heightData, ref waterData);

            course.m_EndPosition.m_Position = course.m_Curve.d;
            course.m_EndPosition.m_Rotation = global::Game.Net.NetUtils.GetNodeRotation(MathUtils.EndTangent(course.m_Curve), owner.m_Rotation);
            course.m_EndPosition.m_CourseDelta = 1f;
            course.m_EndPosition.m_Elevation = curve.d.y;
            course.m_EndPosition.m_ParentMesh = parentMesh.y;
            if (nodeIndex.y >= 0)
                course.m_EndPosition.m_Position = SharedSubNetNodePosition(
                    nodePositions[nodeIndex.y].xyz, owner, netGeometry, simulationSpawn,
                    ref heightData, ref waterData);

            course.m_Length = MathUtils.Length(course.m_Curve);
            course.m_FixedIndex = -1;
            course.m_StartPosition.m_Flags |= CoursePosFlags.IsFirst;
            course.m_EndPosition.m_Flags |= CoursePosFlags.IsLast;
            if (simulationSpawn)
            {
                course.m_StartPosition.m_Flags |= CoursePosFlags.DisableMerge;
                course.m_EndPosition.m_Flags |= CoursePosFlags.DisableMerge;
            }
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
