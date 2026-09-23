using System.Collections.Generic;
using Game.Agents;
using Game.Buildings;
using Game.Citizens;
using Game.Common;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    public partial class ResidentialOccupancySyncSystem
    {
        private bool EnqueueRentAction(Entity property, Entity household)
        {
            if (!CanEnqueueRentAction() || property == Entity.Null || household == Entity.Null ||
                !EntityManager.Exists(property) || !EntityManager.Exists(household)) return false;
            NativeQueue<RentAction> queue = _propertyProcessing.GetRentActionQueue(out JobHandle dependencies);
            dependencies.Complete();
            queue.Enqueue(new RentAction { m_Property = property, m_Renter = household });
            _rentActions++;
            return true;
        }

        /// <summary>Retires through MovingAway, the game's own emigration path.</summary>
        private bool Retire(Entity household)
        {
            if (household == Entity.Null || !EntityManager.Exists(household)) return false;
            if (EntityManager.HasComponent<Deleted>(household)) return false;
            // Whitelisted so the lifecycle guard lets the native executor consume it.
            _authorizedMoveAways.Add(household);
            if (!EntityManager.HasComponent<MovingAway>(household))
                EntityManager.AddComponentData(household,
                    new MovingAway { m_Reason = MoveAwayReason.NoSuitableProperty });
            _settling.Remove(household);
            return true;
        }

        private void UnbindDepartingHousehold(Entity household)
        {
            if (household != Entity.Null && EntityManager.Exists(household) &&
                EntityManager.HasBuffer<HouseholdCitizen>(household))
            {
                DynamicBuffer<HouseholdCitizen> members =
                    EntityManager.GetBuffer<HouseholdCitizen>(household, true);
                for (int i = 0; i < members.Length; i++)
                {
                    Entity citizen = members[i].m_Citizen;
                    if (!TryGetBoundCitizenId(citizen, out ulong citizenId) ||
                        TryGetDesiredHouseholdId(citizenId, out ulong desiredHouseholdId)) continue;
                    UnbindCitizen(citizenId);
                }
            }
            UnbindHousehold(household);
        }

        private void MarkSettling(Entity household) =>
            _settling[household] = _simulationSystem.frameIndex + SettleFrames;

        /// <summary>Recorded apart from the capped list, so the property is never marked settled.</summary>
        private void ScheduleReapply(Entity property)
        {
            _reapplyRequested.Add(property);
            if (_reapply.Count >= MaxDirtyProperties) return;
            _reapply.Add(property);
        }

        private bool IsSettling(Entity household)
        {
            if (!_settling.TryGetValue(household, out uint until)) return false;
            if (_simulationSystem.frameIndex >= until)
            {
                _settling.Remove(household);
                return false;
            }
            return true;
        }

        private void PruneSettling()
        {
            if (_settling.Count == 0) return;
            uint now = _simulationSystem.frameIndex;
            _settlingScratch.Clear();
            foreach (KeyValuePair<Entity, uint> pair in _settling)
                if (now >= pair.Value || !EntityManager.Exists(pair.Key))
                    _settlingScratch.Add(pair.Key);
            for (int i = 0; i < _settlingScratch.Count; i++) _settling.Remove(_settlingScratch[i]);
            _settlingScratch.Clear();
        }

        private bool TryGetCitizenCreationPrefab(out Entity prefab,
            out EntityArchetype archetype)
        {
            prefab = _citizenCreationPrefab;
            if (prefab != Entity.Null && EntityManager.Exists(prefab) &&
                EntityManager.HasComponent<CitizenData>(prefab) &&
                EntityManager.HasComponent<ArchetypeData>(prefab))
            {
                archetype = EntityManager.GetComponentData<ArchetypeData>(prefab).m_Archetype;
                if (archetype.Valid) return true;
            }

            _citizenCreationPrefab = Entity.Null;
            archetype = default(EntityArchetype);
            NativeArray<Entity> prefabs = _citizenCreationPrefabs.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < prefabs.Length; i++)
                {
                    Entity candidate = prefabs[i];
                    if (candidate == Entity.Null || !EntityManager.Exists(candidate) ||
                        !EntityManager.HasComponent<ArchetypeData>(candidate)) continue;
                    EntityArchetype candidateArchetype =
                        EntityManager.GetComponentData<ArchetypeData>(candidate).m_Archetype;
                    if (!candidateArchetype.Valid) continue;
                    _citizenCreationPrefab = candidate;
                    prefab = candidate;
                    archetype = candidateArchetype;
                    return true;
                }
            }
            finally
            {
                prefabs.Dispose();
            }
            prefab = Entity.Null;
            return false;
        }

        private bool ResolveCitizenPrefab(string name, out Entity prefab) =>
            _prefabIndex.TryResolve(name,
                candidate => EntityManager.HasComponent<CitizenData>(candidate), out prefab);

        private Entity SelectArrivalSource(ulong householdId)
        {
            NativeArray<Entity> candidates =
                _arrivalOutsideConnections.ToEntityArray(Allocator.Temp);
            try
            {
                int roadCount = 0;
                for (int i = 0; i < candidates.Length; i++)
                    if (IsRoadArrivalSource(candidates[i])) roadCount++;
                if (roadCount == 0) return Entity.Null;

                ulong mixed = householdId;
                mixed ^= mixed >> 33;
                mixed *= 0xff51afd7ed558ccdUL;
                mixed ^= mixed >> 33;
                int selected = (int)(mixed % (ulong)roadCount);
                for (int i = 0; i < candidates.Length; i++)
                {
                    Entity candidate = candidates[i];
                    if (!IsRoadArrivalSource(candidate)) continue;
                    if (selected-- == 0) return candidate;
                }
                return Entity.Null;
            }
            finally
            {
                candidates.Dispose();
            }
        }

        private bool IsRoadArrivalSource(Entity entity)
        {
            if (entity == Entity.Null || !EntityManager.Exists(entity) ||
                EntityManager.HasComponent<Deleted>(entity) ||
                EntityManager.HasComponent<global::Game.Tools.Temp>(entity) ||
                EntityManager.HasComponent<global::Game.Objects.ElectricityOutsideConnection>(
                    entity) ||
                EntityManager.HasComponent<global::Game.Objects.WaterPipeOutsideConnection>(
                    entity) ||
                !EntityManager.HasComponent<global::Game.Objects.OutsideConnection>(entity) ||
                !EntityManager.HasComponent<global::Game.Objects.Transform>(entity) ||
                !EntityManager.HasComponent<PrefabRef>(entity)) return false;
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab;
            if (prefab == Entity.Null || !EntityManager.Exists(prefab) ||
                !EntityManager.HasComponent<OutsideConnectionData>(prefab)) return false;
            OutsideConnectionData data =
                EntityManager.GetComponentData<OutsideConnectionData>(prefab);
            return (data.m_Type & OutsideConnectionTransferType.Road) !=
                   OutsideConnectionTransferType.None;
        }

        private Entity GetVehicleCreationSource(Entity household, Entity property)
        {
            if (_arrivalSources.TryGetValue(household, out Entity source))
            {
                if (IsRoadArrivalSource(source)) return source;
                _arrivalSources.Remove(household);
            }
            return property;
        }

        private string SafePrefabName(Entity entity)
        {
            if (entity == Entity.Null || !EntityManager.Exists(entity) ||
                !EntityManager.HasComponent<PrefabRef>(entity)) return "<none>";
            return _prefabIndex.NameOf(
                EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab);
        }

        private bool ResolvePrefab<T>(string name, out Entity prefab)
            where T : unmanaged, IComponentData =>
            _prefabIndex.TryResolve(name,
                candidate => EntityManager.HasComponent<T>(candidate) &&
                             EntityManager.HasComponent<ArchetypeData>(candidate), out prefab);

        private bool ResolvePrefab<T>(string name, out Entity prefab, out EntityArchetype archetype)
            where T : unmanaged, IComponentData
        {
            archetype = default(EntityArchetype);
            if (!ResolvePrefab<T>(name, out prefab)) return false;
            archetype = EntityManager.GetComponentData<ArchetypeData>(prefab).m_Archetype;
            return archetype.Valid;
        }

        private void SetOrAdd<T>(Entity entity, T value) where T : unmanaged, IComponentData
        {
            if (EntityManager.HasComponent<T>(entity)) EntityManager.SetComponentData(entity, value);
            else EntityManager.AddComponentData(entity, value);
        }
    }
}
