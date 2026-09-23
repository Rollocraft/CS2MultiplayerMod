using CS2MultiplayerMod.Game.Sync.Infrastructure;
using Game.Buildings;
using Game.Common;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Game.Sync.Commands;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    public partial class GrowableSyncSystem
    {
        /// <summary>Positions derive from the same geometry; the tolerance absorbs float noise and terrain.</summary>
        private Entity FindGrowableAt(float3 position, Entity prefab, long now)
        {
            var candidates = new NativeList<Entity>(16, Allocator.Temp);
            try
            {
                _objectSearch.CollectNear(position, AnchorSearchRadius, candidates);

                Entity best = Entity.Null;
                float bestDistance = AnchorMatchDistance * AnchorMatchDistance;
                bool bestIsExact = false;

                for (int i = 0; i < candidates.Length; i++)
                {
                    Entity candidate = candidates[i];
                    if (!PropertyEntitySnapshot.TryRead(EntityManager, candidate,
                        out PropertyEntitySnapshot snapshot) ||
                        !IsLiveGrowable(candidate, now)) continue;

                    float distance =
                        math.distancesq(snapshot.Transform.m_Position.xz, position.xz);
                    if (distance > AnchorMatchDistance * AnchorMatchDistance) continue;

                    // Prefer the named prefab; a levelled building no longer carries it.
                    bool exact = prefab != Entity.Null && snapshot.Prefab == prefab;
                    if (bestIsExact && !exact) continue;
                    if (exact && !bestIsExact)
                    {
                        best = candidate;
                        bestDistance = distance;
                        bestIsExact = true;
                        continue;
                    }
                    if (best != Entity.Null && distance > bestDistance) continue;
                    best = candidate;
                    bestDistance = distance;
                }
                return best;
            }
            finally
            {
                candidates.Dispose();
            }
        }

        /// <summary>Everything on the lot, by rectangle overlap rather than pivot distance.</summary>
        private void CollectOverlapping(Entity prefab, float3 position, quaternion rotation,
            NativeList<Entity> blockers)
        {
            blockers.Clear();
            if (!EntityManager.Exists(prefab) || !EntityManager.HasComponent<BuildingData>(prefab)) return;
            float2 wantedExtent = LotExtent(EntityManager.GetComponentData<BuildingData>(prefab).m_LotSize);
            float reach = math.length(wantedExtent) + ZoneCellSize;

            var candidates = new NativeList<Entity>(32, Allocator.Temp);
            try
            {
                _objectSearch.CollectNear(position, reach, candidates);
                for (int i = 0; i < candidates.Length; i++)
                {
                    Entity candidate = candidates[i];
                    if (!EntityManager.Exists(candidate) ||
                        !EntityManager.HasComponent<Building>(candidate) ||
                        !EntityManager.HasComponent<global::Game.Objects.Transform>(candidate) ||
                        !EntityManager.HasComponent<PrefabRef>(candidate) ||
                        EntityManager.HasComponent<Temp>(candidate) ||
                        EntityManager.HasComponent<Deleted>(candidate) ||
                        EntityManager.HasComponent<Owner>(candidate)) continue;

                    Entity candidatePrefab = EntityManager.GetComponentData<PrefabRef>(candidate).m_Prefab;
                    if (!EntityManager.Exists(candidatePrefab) ||
                        !EntityManager.HasComponent<BuildingData>(candidatePrefab)) continue;

                    global::Game.Objects.Transform transform =
                        EntityManager.GetComponentData<global::Game.Objects.Transform>(candidate);
                    float2 extent = LotExtent(
                        EntityManager.GetComponentData<BuildingData>(candidatePrefab).m_LotSize);

                    if (RectanglesOverlap(position, rotation, wantedExtent,
                            transform.m_Position, transform.m_Rotation, extent))
                        blockers.Add(candidate);
                }
            }
            finally
            {
                candidates.Dispose();
            }
        }

        /// <summary>Same prefab on the same lot is a redelivery; a different prefab is a level difference.</summary>
        private bool AlreadySatisfied(NativeList<Entity> blockers, Entity prefab, float3 position,
            long now)
        {
            for (int i = 0; i < blockers.Length; i++)
            {
                Entity blocker = blockers[i];
                if (!PropertyEntitySnapshot.TryRead(EntityManager, blocker, out PropertyEntitySnapshot snapshot) ||
                    !IsAutonomousGrowable(blocker, now)) continue;
                if (snapshot.Prefab != prefab) continue;
                float distance = math.distancesq(snapshot.Transform.m_Position.xz, position.xz);
                if (distance <= AnchorMatchDistance * AnchorMatchDistance) return true;
            }
            return false;
        }

        /// <summary>A building a player placed, as opposed to one a simulation grew.</summary>
        private Entity FirstPlayerPlaced(NativeList<Entity> blockers, long now)
        {
            for (int i = 0; i < blockers.Length; i++)
            {
                // The search tree can still name a torn-down entity; that is not a blocker.
                if (!PropertyEntitySnapshot.TryRead(EntityManager, blockers[i], out PropertyEntitySnapshot snapshot))
                    continue;
                if (!IsAutonomousGrowable(blockers[i], now)) return blockers[i];
            }
            return Entity.Null;
        }

        private static float2 LotExtent(int2 lotSize) =>
            new float2(lotSize.x, lotSize.y) * (ZoneCellSize * 0.5f) - OverlapTolerance;

        /// <summary>Separating-axis test; each rectangle's local x and z suffice.</summary>
        private static bool RectanglesOverlap(float3 centreA, quaternion rotationA, float2 extentA,
            float3 centreB, quaternion rotationB, float2 extentB)
        {
            if (extentA.x <= 0f || extentA.y <= 0f || extentB.x <= 0f || extentB.y <= 0f) return false;

            float2 rightA = math.normalizesafe(math.rotate(rotationA, new float3(1f, 0f, 0f)).xz,
                new float2(1f, 0f));
            float2 forwardA = math.normalizesafe(math.rotate(rotationA, new float3(0f, 0f, 1f)).xz,
                new float2(0f, 1f));
            float2 rightB = math.normalizesafe(math.rotate(rotationB, new float3(1f, 0f, 0f)).xz,
                new float2(1f, 0f));
            float2 forwardB = math.normalizesafe(math.rotate(rotationB, new float3(0f, 0f, 1f)).xz,
                new float2(0f, 1f));

            float2 delta = centreB.xz - centreA.xz;
            return !(SeparatedOn(rightA, delta, rightA, forwardA, extentA, rightB, forwardB, extentB) ||
                     SeparatedOn(forwardA, delta, rightA, forwardA, extentA, rightB, forwardB, extentB) ||
                     SeparatedOn(rightB, delta, rightA, forwardA, extentA, rightB, forwardB, extentB) ||
                     SeparatedOn(forwardB, delta, rightA, forwardA, extentA, rightB, forwardB, extentB));
        }

        private static bool SeparatedOn(float2 axis, float2 delta,
            float2 rightA, float2 forwardA, float2 extentA,
            float2 rightB, float2 forwardB, float2 extentB)
        {
            float reachA = math.abs(math.dot(rightA, axis)) * extentA.x +
                           math.abs(math.dot(forwardA, axis)) * extentA.y;
            float reachB = math.abs(math.dot(rightB, axis)) * extentB.x +
                           math.abs(math.dot(forwardB, axis)) * extentB.y;
            return math.abs(math.dot(delta, axis)) > reachA + reachB;
        }

        private bool IsLiveGrowable(Entity entity, long now) =>
            EntityManager.Exists(entity) &&
            EntityManager.HasComponent<Building>(entity) &&
            EntityManager.HasComponent<PrefabRef>(entity) &&
            EntityManager.HasComponent<global::Game.Objects.Transform>(entity) &&
            !EntityManager.HasComponent<Temp>(entity) &&
            !EntityManager.HasComponent<Deleted>(entity) &&
            !EntityManager.HasComponent<Owner>(entity) &&
            IsAutonomousGrowable(entity, now);

        private string DescribeBlocker(Entity blocker, long now)
        {
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(blocker).m_Prefab;
            string name = PrefabIndexSafeName(prefab);
            bool grown = IsAutonomousGrowable(blocker, now);
            return (grown ? "a grown '" : "a placed '") + (name ?? "?") + "'";
        }

        private bool RepairSpawnVariant(Entity building, ushort seed)
        {
            if (EntityManager.HasComponent<PseudoRandomSeed>(building))
            {
                if (EntityManager.GetComponentData<PseudoRandomSeed>(building).m_Seed == seed)
                    return false;
                EntityManager.SetComponentData(building, new PseudoRandomSeed(seed));
            }
            else EntityManager.AddComponentData(building, new PseudoRandomSeed(seed));

            EntityManager.AddComponent<BatchesUpdated>(building);
            return true;
        }

        private static int SeedFor(GrowableLifecycleCommand command) =>
            // All 16 bits are stored, including zero; GetRandom mixes in a nonzero state.
            command.RandomSeed;
    }
}
