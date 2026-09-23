using CS2MultiplayerMod.Game.Sync.Infrastructure;
using System.Diagnostics;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Commands;
using Game.Common;
using Game.Prefabs;
using Game.Vehicles;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
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
                    _localVehiclePrefabCounts.TryGetValue(name, out int count);
                    _localVehiclePrefabCounts[name] = count + 1;
                }
            }

            _matchedVehiclePrefabCounts.Clear();
            bool createdAny = false;
            Entity source = GetVehicleCreationSource(household, property);
            for (int i = 0; i < desired.Length; i++)
            {
                string prefabName = desired[i];
                _matchedVehiclePrefabCounts.TryGetValue(prefabName, out int matched);
                matched++;
                _matchedVehiclePrefabCounts[prefabName] = matched;
                _localVehiclePrefabCounts.TryGetValue(prefabName, out int local);
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

        /// <summary>Owned cars exist before the first behaviour pass, so the arrival trip can use one.</summary>
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
            SyncLog.Detail(LogTopic.Residential, "CAR-SPAWN family=0x" + householdId.ToString("X16") +
                " vehicle='" + prefabName + "' local=" + vehicle + " house='" +
                SafePrefabName(property) + "' origin='" + SafePrefabName(source) + "' initial=" +
                initial + ".");
        }

        private void TraceVehicleSpawnFailure(ulong householdId, string prefabName,
            Entity property, Entity source)
        {
            string warningKey = householdId.ToString("X16") + "|" + prefabName;
            if (!_vehicleSpawnWarnings.Add(warningKey)) return;
            SyncLog.Warn(LogTopic.Residential, "Occupancy: could not spawn owned vehicle '" +
                prefabName + "' for family 0x" + householdId.ToString("X16") + " at '" +
                SafePrefabName(property) + "' (from '" + SafePrefabName(source) + "').");
        }

        /// <summary>Surname and first-name indices, drawn per machine from its own clock.</summary>
        private void ApplyNameIndices(Entity entity, int[] wanted)
        {
            if (wanted.Length == 0 ||
                !EntityManager.HasBuffer<RandomLocalizationIndex>(entity)) return;
            var indices = new BufferEdit<RandomLocalizationIndex>(EntityManager, entity);
            // Write the slots both sides have.
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
