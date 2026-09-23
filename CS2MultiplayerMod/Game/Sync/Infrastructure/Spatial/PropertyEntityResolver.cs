using System;
using Game.Common;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2MultiplayerMod.Game.Sync.Infrastructure
{
    /// <summary>A read-only value; never carries component or buffer handles across mutations.</summary>
    internal struct PropertyEntitySnapshot
    {
        public Entity Entity;
        public Entity Prefab;
        public global::Game.Objects.Transform Transform;

        public static bool TryRead(EntityManager manager, Entity entity, out PropertyEntitySnapshot value)
        {
            value = default(PropertyEntitySnapshot);
            if (entity == Entity.Null || !manager.Exists(entity) ||
                manager.HasComponent<Deleted>(entity) || manager.HasComponent<Temp>(entity) ||
                !manager.HasComponent<PrefabRef>(entity) ||
                !manager.HasComponent<global::Game.Objects.Transform>(entity)) return false;
            value = new PropertyEntitySnapshot
            {
                Entity = entity,
                Prefab = manager.GetComponentData<PrefabRef>(entity).m_Prefab,
                Transform = manager.GetComponentData<global::Game.Objects.Transform>(entity),
            };
            return true;
        }
    }

    internal struct PropertyResolution
    {
        public PropertyEntitySnapshot Snapshot;
        public bool Ambiguous;
        public bool Found => Snapshot.Entity != Entity.Null && !Ambiguous;
        public Entity Entity => Found ? Snapshot.Entity : Entity.Null;
    }

    internal enum PropertyPrefabPreference { ExactFirst, BreakDistanceTie }

    internal static class PropertyEntityResolver
    {
        public static PropertyResolution Resolve(EntityManager manager, ObjectSearch.Batch search,
            NativeList<Entity> candidates, float3 anchor, float searchRadius, float matchDistance,
            float epsilon, Entity prefab, Func<Entity, bool> eligible,
            PropertyPrefabPreference preference, Func<Entity, bool> canClaim = null)
        {
            search.CollectNear(anchor, searchRadius, candidates);
            PropertyResolution exact = default(PropertyResolution), nearest = default(PropertyResolution);
            float exactDistance = 0, nearestDistance = 0;
            bool nearestExact = false;
            for (int i = 0; i < candidates.Length; i++)
            {
                Entity entity = candidates[i];
                bool live = PropertySpatialPass.Current != null
                    ? PropertySpatialPass.Current.TryRead(manager, entity, out PropertyEntitySnapshot snapshot)
                    : PropertyEntitySnapshot.TryRead(manager, entity, out snapshot);
                if (!live ||
                    !eligible(entity) || (canClaim != null && !canClaim(entity))) continue;
                float distance = math.distancesq(snapshot.Transform.m_Position.xz, anchor.xz);
                if (distance > matchDistance * matchDistance) continue;
                bool isExact = prefab != Entity.Null && snapshot.Prefab == prefab;
                if (isExact) Consider(snapshot, distance, true, epsilon, ref exact, ref exactDistance,
                    ref nearestExact, false);
                Consider(snapshot, distance, isExact, epsilon, ref nearest, ref nearestDistance,
                    ref nearestExact, preference == PropertyPrefabPreference.BreakDistanceTie);
            }
            if (preference == PropertyPrefabPreference.ExactFirst && exact.Found) return exact;
            // If exact matches tie, the unique nearest candidate remains a valid fallback.
            return nearest;
        }

        private static void Consider(PropertyEntitySnapshot candidate, float distance, bool exact,
            float epsilon, ref PropertyResolution best, ref float bestDistance,
            ref bool bestExact, bool breakTie)
        {
            if (best.Snapshot.Entity == Entity.Null || distance < bestDistance - epsilon)
            {
                best.Snapshot = candidate;
                best.Ambiguous = false;
                bestDistance = distance;
                if (breakTie) bestExact = exact;
                return;
            }
            if (math.abs(distance - bestDistance) > epsilon || best.Snapshot.Entity == candidate.Entity) return;
            if (breakTie && exact != bestExact)
            {
                if (!exact) return;
                best.Snapshot = candidate;
                bestDistance = distance;
                bestExact = true;
                best.Ambiguous = false;
                return;
            }
            best.Ambiguous = true;
        }
    }
}
