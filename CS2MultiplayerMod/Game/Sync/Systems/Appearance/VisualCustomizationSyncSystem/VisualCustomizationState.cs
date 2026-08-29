using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Colossal.Entities;
using CS2MultiplayerMod.Game.Sync.Commands;
using Game;
using Game.Buildings;
using Game.Common;
using Game.Objects;
using Game.Prefabs;
using Game.Rendering;
using Game.Tools;
using Game.UI.InGame;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    // Reading an entity's current appearance, matching it against what a command describes, and
    // converting between the game's colour set and the one that travels on the wire.
    public partial class VisualCustomizationSyncSystem
    {
        // ---- target resolution ----------------------------------------------

        private Entity ResolveTarget(Entity prefab, in VisualCustomizationTarget target,
            HashSet<Entity> used)
        {
            Entity hinted = new Entity
            {
                Index = target.EntityIndex,
                Version = target.EntityVersion,
            };
            if (!used.Contains(hinted) && IsBaseCandidate(hinted, prefab))
            {
                bool seedMatches = target.RandomSeed < 0 ||
                    (EntityManager.HasComponent<PseudoRandomSeed>(hinted) &&
                     EntityManager.GetComponentData<PseudoRandomSeed>(hinted).m_Seed ==
                     target.RandomSeed);
                float3 hintedPosition =
                    EntityManager.GetComponentData<Transform>(hinted).m_Position;
                float distanceSq = math.distancesq(
                    hintedPosition, new float3(target.X, target.Y, target.Z));
                if (seedMatches || distanceSq <= MatchToleranceSq)
                    return hinted;
            }

            // The bucket already satisfies every IsBaseCandidate condition except an in-frame
            // delete, so only the winner needs re-validating against live state.
            CandidateCache.Bucket bucket = _candidates.For(prefab, _targetQuery, EntityManager);
            int bestSeed = -1;
            int bestNear = -1;
            float bestSeedDistance = float.MaxValue;
            float bestNearDistance = float.MaxValue;
            int seedMatchesCount = 0;
            float3 position = new float3(target.X, target.Y, target.Z);
            bool skipUsed = used.Count != 0;

            for (int i = 0; i < bucket.Count; i++)
            {
                if (skipUsed && used.Contains(bucket.Entities[i])) continue;

                float distanceSq = math.distancesq(bucket.Positions[i], position);
                if (distanceSq < bestNearDistance)
                {
                    bestNearDistance = distanceSq;
                    bestNear = i;
                }

                if (target.RandomSeed >= 0 && bucket.Seeds[i] == target.RandomSeed)
                {
                    seedMatchesCount++;
                    if (distanceSq < bestSeedDistance)
                    {
                        bestSeedDistance = distanceSq;
                        bestSeed = i;
                    }
                }
            }

            if (bestSeed >= 0 &&
                (seedMatchesCount == 1 || bestSeedDistance <= MatchToleranceSq ||
                 EntityManager.HasComponent<Vehicle>(bucket.Entities[bestSeed])) &&
                IsBaseCandidate(bucket.Entities[bestSeed], prefab))
                return bucket.Entities[bestSeed];
            return bestNear >= 0 && bestNearDistance <= MatchToleranceSq &&
                   IsBaseCandidate(bucket.Entities[bestNear], prefab)
                ? bucket.Entities[bestNear]
                : Entity.Null;
        }

        private bool IsBaseCandidate(Entity entity, Entity prefab)
        {
            if (!EntityManager.Exists(entity) ||
                EntityManager.HasComponent<Deleted>(entity) ||
                EntityManager.HasComponent<Temp>(entity) ||
                !EntityManager.HasComponent<PrefabRef>(entity) ||
                !EntityManager.HasComponent<Transform>(entity))
                return false;
            if (EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab != prefab)
                return false;
            return EntityManager.HasBuffer<CustomMeshColor>(entity) ||
                   EntityManager.HasComponent<Building>(entity);
        }

        // ---- state helpers ---------------------------------------------------

        private bool TryReadState(Entity entity, out VisualState state)
        {
            state = default(VisualState);
            Entity prefab;
            if (!TryGetPrefab(entity, out prefab)) return false;

            state.SupportsColor =
                !EntityManager.HasComponent<Plant>(entity) &&
                EntityManager.HasBuffer<MeshColor>(entity) &&
                EntityManager.HasBuffer<CustomMeshColor>(entity);
            if (state.SupportsColor)
            {
                DynamicBuffer<CustomMeshColor> custom =
                    EntityManager.GetBuffer<CustomMeshColor>(entity, true);
                state.HasCustomColor =
                    EntityManager.IsComponentEnabled<CustomMeshColor>(entity) &&
                    custom.Length > 0;
                if (state.HasCustomColor)
                    state.Color = FromGameColor(custom[0].m_ColorSet);
            }

            state.SupportsHistorical = CanBeHistorical(entity, prefab);
            if (state.SupportsHistorical)
            {
                Building building = EntityManager.GetComponentData<Building>(entity);
                state.IsHistorical =
                    (building.m_Flags & global::Game.Buildings.BuildingFlags.Historical) != 0;
            }
            return state.SupportsColor || state.SupportsHistorical;
        }

        private bool CanBeHistorical(Entity entity, Entity prefab) =>
            EntityManager.HasComponent<Building>(entity) &&
            !EntityManager.HasComponent<Abandoned>(entity) &&
            EntityManager.HasComponent<SpawnableBuildingData>(prefab) &&
            !EntityManager.HasComponent<SignatureBuildingData>(prefab);

        private bool TryGetEffectiveColor(Entity entity, out VisualColorSet color)
        {
            color = default(VisualColorSet);
            if (!EntityManager.Exists(entity) ||
                EntityManager.HasComponent<Plant>(entity) ||
                !EntityManager.HasBuffer<MeshColor>(entity) ||
                !EntityManager.HasBuffer<CustomMeshColor>(entity))
                return false;

            DynamicBuffer<CustomMeshColor> custom =
                EntityManager.GetBuffer<CustomMeshColor>(entity, true);
            if (custom.Length > 0)
            {
                color = FromGameColor(custom[0].m_ColorSet);
                return true;
            }

            DynamicBuffer<MeshColor> mesh = EntityManager.GetBuffer<MeshColor>(entity, true);
            if (mesh.Length == 0) return false;
            color = FromGameColor(mesh[0].m_ColorSet);
            return true;
        }

        private bool TryGetPrefab(Entity entity, out Entity prefab)
        {
            prefab = Entity.Null;
            if (entity == Entity.Null || !EntityManager.Exists(entity) ||
                EntityManager.HasComponent<Deleted>(entity) ||
                EntityManager.HasComponent<Temp>(entity) ||
                !EntityManager.HasComponent<PrefabRef>(entity))
                return false;
            prefab = EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab;
            return prefab != Entity.Null && EntityManager.Exists(prefab);
        }

        private static bool SameColorState(in VisualState left, in VisualState right) =>
            left.SupportsColor == right.SupportsColor &&
            left.HasCustomColor == right.HasCustomColor &&
            (!left.HasCustomColor || left.Color.Equals(right.Color));

        private static bool MatchesCommand(in VisualState state,
            VisualCustomizationCommand command)
        {
            if ((command.Fields & VisualCustomizationFields.MeshColor) != 0 &&
                state.SupportsColor &&
                (state.HasCustomColor != command.HasCustomColor ||
                 (command.HasCustomColor && !state.Color.Equals(command.Color))))
                return false;
            if ((command.Fields & VisualCustomizationFields.Historical) != 0 &&
                state.SupportsHistorical &&
                state.IsHistorical != command.IsHistorical)
                return false;
            return true;
        }

        private VisualColorSet[] ReadPalette()
        {
            if (!_paletteSystem.HasPaletteEntity()) return new VisualColorSet[0];
            DynamicBuffer<MeshColorPalette> buffer = _paletteSystem.GetPaletteBuffer();
            int count = Math.Min(buffer.Length, ColorPaletteCommand.MaxColorSets);
            var result = new VisualColorSet[count];
            for (int i = 0; i < count; i++)
                result[i] = FromGameColor(buffer[i].m_ColorSet);
            return result;
        }

        private void WritePalette(VisualColorSet[] colors)
        {
            DynamicBuffer<MeshColorPalette> buffer = _paletteSystem.GetPaletteBuffer();
            buffer.Clear();
            for (int i = 0; i < colors.Length; i++)
            {
                buffer.Add(new MeshColorPalette
                {
                    m_ColorSet = ToGameColor(colors[i]),
                });
            }
        }

        private static bool SamePalette(VisualColorSet[] left, VisualColorSet[] right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left == null || right == null || left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; i++)
                if (!left[i].Equals(right[i])) return false;
            return true;
        }

        private static VisualColorSet[] ClonePalette(VisualColorSet[] source)
        {
            if (source == null) return null;
            var clone = new VisualColorSet[source.Length];
            Array.Copy(source, clone, source.Length);
            return clone;
        }

        private static VisualColorSet FromGameColor(ColorSet color) => new VisualColorSet
        {
            R0 = color.m_Channel0.r, G0 = color.m_Channel0.g,
            B0 = color.m_Channel0.b, A0 = color.m_Channel0.a,
            R1 = color.m_Channel1.r, G1 = color.m_Channel1.g,
            B1 = color.m_Channel1.b, A1 = color.m_Channel1.a,
            R2 = color.m_Channel2.r, G2 = color.m_Channel2.g,
            B2 = color.m_Channel2.b, A2 = color.m_Channel2.a,
        };

        private static ColorSet ToGameColor(VisualColorSet color) => new ColorSet
        {
            m_Channel0 = new UnityEngine.Color(color.R0, color.G0, color.B0, color.A0),
            m_Channel1 = new UnityEngine.Color(color.R1, color.G1, color.B1, color.A1),
            m_Channel2 = new UnityEngine.Color(color.R2, color.G2, color.B2, color.A2),
        };

        private void Prune(long now)
        {
            if (now - _lastPruneMs < PruneIntervalMs) return;
            _lastPruneMs = now;

            List<Entity> dead = null;
            foreach (KeyValuePair<Entity, VisualState> pair in _known)
            {
                if (EntityManager.Exists(pair.Key) &&
                    !EntityManager.HasComponent<Deleted>(pair.Key))
                    continue;
                (dead ?? (dead = new List<Entity>())).Add(pair.Key);
            }
            if (dead != null)
                for (int i = 0; i < dead.Count; i++) _known.Remove(dead[i]);

            dead = null;
            foreach (KeyValuePair<Entity, long> pair in _suppressColorBatch)
            {
                if (pair.Value >= now && EntityManager.Exists(pair.Key)) continue;
                (dead ?? (dead = new List<Entity>())).Add(pair.Key);
            }
            if (dead != null)
                for (int i = 0; i < dead.Count; i++) _suppressColorBatch.Remove(dead[i]);
        }
    }
}
