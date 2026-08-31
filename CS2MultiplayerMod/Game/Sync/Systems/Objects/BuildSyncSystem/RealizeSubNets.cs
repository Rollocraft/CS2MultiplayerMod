using System.Text;
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
    // The sub-networks a placed object owns: the driveways and paths generated inside its lot,
    // each course rebuilt in the owner's local space so both peers lay down the same geometry.
    public partial class BuildSyncSystem
    {
        /// <summary>
        /// Emit connection-net definitions per <see cref="SubNet"/>, curves averaged at shared
        /// node indices, mirrored for left-hand traffic and transformed local to world.
        /// </summary>
        private void RealizeSubNets(Entity prefab, OwnerDefinition owner, Entity ownerEntity,
            Entity lotOwner, bool simulationSpawn, ref Unity.Mathematics.Random random)
        {
            if (!EntityManager.HasBuffer<SubNet>(prefab)) return;
            DynamicBuffer<SubNet> subNets = EntityManager.GetBuffer<SubNet>(prefab, isReadOnly: true);
            if (subNets.Length == 0) return;

            // Height fields for the per-course snapping below. GetHeightData(waitForPending) is how
            // the terrain path already reads a settled surface; the water dependency is completed
            // before the data is touched. The spawner recipe snaps to no surface at all, so it does
            // not pay for either read.
            var heightData = default(TerrainHeightData);
            var waterData = default(WaterSurfaceData<SurfaceWater>);
            var lotInfo = default(global::Game.Buildings.BuildingUtils.LotInfo);
            bool hasLot = false;
            if (!simulationSpawn)
            {
                heightData = _terrainSystem.GetHeightData(waitForPending: true);
                Unity.Jobs.JobHandle waterDeps;
                waterData = _waterSystem.GetSurfaceData(out waterDeps);
                waterDeps.Complete();
                hasLot = TryGetOwnerLot(lotOwner, out lotInfo);
            }

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

        /// <summary>
        /// Reproduce the lot info the game derives for the building a set of connection nets is laid
        /// on. Requires a <see cref="global::Game.Buildings.Lot"/>; without one the caller falls back
        /// to terrain snapping, exactly as the tools do.
        /// </summary>
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

            bool hasExtensionLots;
            lotInfo = global::Game.Buildings.BuildingUtils.CalculateLotInfo(
                new float2(EntityManager.GetComponentData<BuildingData>(ownerPrefab).m_LotSize) * 4f,
                EntityManager.GetComponentData<global::Game.Objects.Transform>(lotOwner),
                elevation,
                EntityManager.GetComponentData<global::Game.Buildings.Lot>(lotOwner),
                EntityManager.GetComponentData<PrefabRef>(lotOwner),
                upgrades, _transformLookup, _prefabRefLookup, _objectGeometryLookup,
                _buildingTerraformLookup, _buildingExtensionLookup, defaultNoSmooth: false,
                out hasExtensionLots);
            return true;
        }

        /// <summary>
        /// The world position of a node index several sub-nets share. A water net takes its height
        /// from the water surface rather than from the averaged prefab-local position - except on
        /// the spawner recipe, which never samples a surface at all.
        /// </summary>
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
        /// <paramref name="simulationSpawn"/> picks the recipe everything the simulation grows uses
        /// instead of the tool's: prefab-local height, no surface snapping, and merging disabled on
        /// both ends. The two are not interchangeable - a tool placement's driveway is meant to join
        /// the road it snapped to, a grown building's is not allowed to touch it. See
        /// docs/internals/building-placement-and-subnets.md.
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
            // Tool-recipe height handling. A course whose BOTH ends are mesh-relative keeps its
            // prefab-local height; otherwise the free end(s) are snapped - to water, to the host
            // building's lot surface, or to the terrain - and the prefab-local height is then
            // re-applied as an offset. Laying a tool placement's paths at raw LocalToWorld height
            // instead is why they met the street at the wrong height and read as unconnected.
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
