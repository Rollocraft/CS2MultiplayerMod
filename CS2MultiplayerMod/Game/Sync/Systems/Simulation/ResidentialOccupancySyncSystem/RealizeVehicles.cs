using System;
using System.Collections.Generic;
using System.Diagnostics;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Commands;
using Game.Agents;
using Game.Buildings;
using Game.Citizens;
using Game.Common;
using Game.Companies;
using Game.Economy;
using Game.Prefabs;
using Game.Simulation;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    // A household's owned vehicles, and the name indices a citizen or household is drawn with.
    // Both are created from the host's roster and traced when a prefab cannot be resolved.
    public partial class ResidentialOccupancySyncSystem
    {
        private void ApplyOwnedVehicles(Entity household, Entity property,
            OccupancyHousehold wanted)
        {
            string[] desired = wanted.OwnedVehicles;
            if (desired == null || desired.Length == 0) return;
            if (IsSettling(household))
            {
                ScheduleReapply(property);
                return;
            }

            _localVehiclePrefabCounts.Clear();
            if (EntityManager.HasBuffer<OwnedVehicle>(household))
            {
                DynamicBuffer<OwnedVehicle> owned =
                    EntityManager.GetBuffer<OwnedVehicle>(household, true);
                for (int i = 0; i < owned.Length; i++)
                {
                    Entity vehicle = owned[i].m_Vehicle;
                    if (vehicle == Entity.Null || !EntityManager.Exists(vehicle) ||
                        EntityManager.HasComponent<Deleted>(vehicle) ||
                        !EntityManager.HasComponent<global::Game.Vehicles.PersonalCar>(vehicle) ||
                        !EntityManager.HasComponent<PrefabRef>(vehicle) ||
                        !EntityManager.HasComponent<Owner>(vehicle) ||
                        EntityManager.GetComponentData<Owner>(vehicle).m_Owner != household)
                        continue;
                    string name = _prefabIndex.NameOf(
                        EntityManager.GetComponentData<PrefabRef>(vehicle).m_Prefab);
                    if (string.IsNullOrEmpty(name)) continue;
                    int count;
                    _localVehiclePrefabCounts.TryGetValue(name, out count);
                    _localVehiclePrefabCounts[name] = count + 1;
                }
            }

            _matchedVehiclePrefabCounts.Clear();
            bool createdAny = false;
            Entity source = GetVehicleCreationSource(household, property);
            for (int i = 0; i < desired.Length; i++)
            {
                string prefabName = desired[i];
                int matched;
                _matchedVehiclePrefabCounts.TryGetValue(prefabName, out matched);
                matched++;
                _matchedVehiclePrefabCounts[prefabName] = matched;
                int local;
                _localVehiclePrefabCounts.TryGetValue(prefabName, out local);
                if (local >= matched) continue;

                if (_budget.VehiclesCreated >= MaxVehiclesCreatedPerUpdate)
                {
                    ScheduleReapply(property);
                    break;
                }
                if (!EntityManager.HasBuffer<OwnedVehicle>(household))
                    EntityManager.AddBuffer<OwnedVehicle>(household);

                Entity vehicle = CreateOwnedVehicle(household, source, wanted.HouseholdId,
                    prefabName, i);
                if (vehicle == Entity.Null)
                {
                    TraceVehicleSpawnFailure(wanted.HouseholdId, prefabName, property, source);
                    ScheduleReapply(property);
                    continue;
                }

                LinkOwnedVehicle(household, vehicle);
                _localVehiclePrefabCounts[prefabName] = local + 1;
                _budget.VehiclesCreated++;
                _createdVehicles++;
                createdAny = true;
                TraceVehicleSpawn(wanted.HouseholdId, prefabName, vehicle, property, source,
                    false);
            }
            if (!createdAny) return;
            MarkSettling(household);
            ScheduleReapply(property);
        }

        /// <summary>
        /// Cars that already belong to a newly arriving family must exist before its citizens run
        /// their first behaviour pass. That gives the native trip planner an owned car to reserve
        /// for the journey from the outside connection to the new home.
        /// </summary>
        private void CreateInitialOwnedVehicles(Entity household, Entity property, Entity source,
            OccupancyHousehold wanted)
        {
            string[] desired = wanted.OwnedVehicles;
            if (desired == null || desired.Length == 0) return;
            if (!EntityManager.HasBuffer<OwnedVehicle>(household))
                EntityManager.AddBuffer<OwnedVehicle>(household);

            for (int i = 0; i < desired.Length; i++)
            {
                string prefabName = desired[i];
                Entity vehicle = CreateOwnedVehicle(household, source, wanted.HouseholdId,
                    prefabName, i);
                if (vehicle == Entity.Null)
                {
                    TraceVehicleSpawnFailure(wanted.HouseholdId, prefabName, property, source);
                    continue;
                }

                LinkOwnedVehicle(household, vehicle);
                _budget.VehiclesCreated++;
                _createdVehicles++;
                TraceVehicleSpawn(wanted.HouseholdId, prefabName, vehicle, property, source, true);
            }
        }

        private void LinkOwnedVehicle(Entity household, Entity vehicle)
        {
            if (!EntityManager.HasBuffer<OwnedVehicle>(household))
                EntityManager.AddBuffer<OwnedVehicle>(household);
            DynamicBuffer<OwnedVehicle> owned = EntityManager.GetBuffer<OwnedVehicle>(household);
            for (int i = 0; i < owned.Length; i++)
                if (owned[i].m_Vehicle == vehicle) return;
            owned.Add(new OwnedVehicle(vehicle));
        }

        [Conditional(DevTrace.Symbol)]
        private void TraceVehicleSpawn(ulong householdId, string prefabName, Entity vehicle,
            Entity property, Entity source, bool initial)
        {
            Mod.log.Info("[MP][OCC-DEV] CAR-SPAWN family=0x" +
                         householdId.ToString("X16") + " vehicle='" + prefabName +
                         "' local=" + vehicle + " house='" + SafePrefabName(property) +
                         "' origin='" + SafePrefabName(source) + "' initial=" + initial + ".");
        }

        private void TraceVehicleSpawnFailure(ulong householdId, string prefabName,
            Entity property, Entity source)
        {
            string warningKey = householdId.ToString("X16") + "|" + prefabName;
            if (!_vehicleSpawnWarnings.Add(warningKey)) return;
            Mod.Verbose("[MP] Occupancy: could not spawn owned vehicle '" + prefabName +
                        "' for family 0x" + householdId.ToString("X16") + " at '" +
                        SafePrefabName(property) + "' (from '" + SafePrefabName(source) + "').");
        }

        /// <summary>
        /// A family's surname and a person's first name are stored as indices into localized name
        /// lists, drawn on each machine from its own clock. Nothing else about the household says
        /// what it is called, so these have to be copied for two players to be talking about the
        /// same family.
        /// </summary>
        private void ApplyNameIndices(Entity entity, int[] wanted)
        {
            if (wanted.Length == 0 ||
                !EntityManager.HasBuffer<RandomLocalizationIndex>(entity)) return;
            DynamicBuffer<RandomLocalizationIndex> indices =
                EntityManager.GetBuffer<RandomLocalizationIndex>(entity);
            // The local buffer is sized from this peer's own prefab, and the host's name lists are
            // the same content. Write the slots both sides have and leave any extra alone.
            int count = math.min(indices.Length, wanted.Length);
            bool changed = false;
            for (int i = 0; i < count; i++)
            {
                if (indices[i].m_Index == wanted[i]) continue;
                indices[i] = new RandomLocalizationIndex(wanted[i]);
                changed = true;
            }
            if (changed) _renamedEntities++;
        }
    }
}
