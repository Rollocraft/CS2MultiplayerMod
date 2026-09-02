using Game.Buildings;
using Game.Common;
using Game.Objects;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Game.Sync.Commands;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    // Finding the growable a command refers to, and deciding whether something already standing on
    // the lot is that building, a player's own placement, or a blocker worth reporting. Matching
    // is by lot footprint, because the two peers share no entity ids.
    public partial class GrowableSyncSystem
    {
        /// <summary>
        /// The building standing at an anchor. Positions are computed from the same road and block
        /// geometry on both machines, so the tolerance only absorbs float noise and a terrain
        /// height that was sampled independently.
        /// </summary>
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
                    if (!IsLiveGrowable(candidate, now)) continue;

                    float distance = math.distancesq(
                        EntityManager.GetComponentData<global::Game.Objects.Transform>(candidate)
                            .m_Position.xz, position.xz);
                    if (distance > AnchorMatchDistance * AnchorMatchDistance) continue;

                    // Prefer the named prefab, but stay tolerant of a different one: a building
                    // that levelled up no longer carries the prefab a removal names, and that
                    // removal still has to reach it.
                    bool exact = prefab != Entity.Null &&
                                 EntityManager.GetComponentData<PrefabRef>(candidate).m_Prefab == prefab;
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

        /// <summary>
        /// Everything already standing on the lot this spawn wants. Compares the two lot rectangles
        /// rather than the two pivots: buildings of different sizes conflict long before their
        /// centres coincide, and two neighbours on one street share a centre-to-centre distance
        /// that says nothing about whether they fit.
        /// </summary>
        private void CollectOverlapping(Entity prefab, float3 position, quaternion rotation,
            NativeList<Entity> blockers)
        {
            blockers.Clear();
            if (!EntityManager.HasComponent<BuildingData>(prefab)) return;
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
                    if (!EntityManager.HasComponent<BuildingData>(candidatePrefab)) continue;

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

        /// <summary>
        /// True when the host's building is the one already standing here. Same prefab on the same
        /// lot is the redelivery case; a different prefab at the same anchor is a real level
        /// difference and has to be resolved, not ignored.
        /// </summary>
        private bool AlreadySatisfied(NativeList<Entity> blockers, Entity prefab, float3 position,
            long now)
        {
            for (int i = 0; i < blockers.Length; i++)
            {
                Entity blocker = blockers[i];
                if (!IsAutonomousGrowable(blocker, now)) continue;
                if (EntityManager.GetComponentData<PrefabRef>(blocker).m_Prefab != prefab) continue;
                float distance = math.distancesq(
                    EntityManager.GetComponentData<global::Game.Objects.Transform>(blocker)
                        .m_Position.xz, position.xz);
                if (distance <= AnchorMatchDistance * AnchorMatchDistance) return true;
            }
            return false;
        }

        /// <summary>A building a player placed, as opposed to one a simulation grew.</summary>
        private Entity FirstPlayerPlaced(NativeList<Entity> blockers, long now)
        {
            for (int i = 0; i < blockers.Length; i++)
            {
                if (!IsAutonomousGrowable(blockers[i], now)) return blockers[i];
            }
            return Entity.Null;
        }

        private static float2 LotExtent(int2 lotSize) =>
            new float2(lotSize.x, lotSize.y) * (ZoneCellSize * 0.5f) - OverlapTolerance;

        /// <summary>
        /// Separating-axis test between two rotated lot rectangles. Four axes suffice: the two
        /// rectangles' own edge normals, which for rectangles are their local x and z.
        /// </summary>
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

        private int SeedFor(GrowableLifecycleCommand command)
        {
            // The built entity keeps the low 16 bits as its PseudoRandomSeed, which is the variant.
            // Zero is the one value the game's own random rejects, so it is nudged rather than
            // passed through - a building with no seed at all would fail to pick a mesh.
            int seed = command.RandomSeed;
            return seed == 0 ? 1 : seed;
        }
    }
}
