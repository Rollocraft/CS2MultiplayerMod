using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using CS2MultiplayerMod.Game.Sync.Commands;
using Game.Buildings;
using Game.Citizens;
using Game.Common;
using Game.Economy;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    // Reading one property, household, citizen, owned vehicle or name draw out of the local world
    // and into the form that travels on the wire.
    public partial class ResidentialOccupancySyncSystem
    {
        private bool TryCaptureProperty(Entity property, out OccupancyProperty result)
        {
            result = default(OccupancyProperty);
            if (!IsLiveProperty(property)) return false;
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(property).m_Prefab;
            if (prefab == Entity.Null || !EntityManager.Exists(prefab) ||
                !EntityManager.HasComponent<BuildingPropertyData>(prefab)) return false;
            string prefabName = _prefabIndex.NameOf(prefab);
            if (string.IsNullOrEmpty(prefabName)) return false;

            var households = new List<OccupancyHousehold>();
            var householdEntities = new List<Entity>();
            DynamicBuffer<Renter> renters = EntityManager.GetBuffer<Renter>(property, true);
            for (int i = 0; i < renters.Length; i++)
            {
                Entity renter = renters[i].m_Renter;
                // Companies rent the commercial half of a mixed building. They are a different
                // simulation with a different authority story; only households are ours.
                if (renter == Entity.Null || !EntityManager.Exists(renter) ||
                    !EntityManager.HasComponent<Household>(renter) ||
                    EntityManager.HasComponent<Deleted>(renter) ||
                    EntityManager.HasComponent<Temp>(renter) ||
                    EntityManager.HasComponent<TouristHousehold>(renter) ||
                    EntityManager.HasComponent<CommuterHousehold>(renter)) continue;

                // A stale one-way Renter entry is not an occupant. Conversely, a live household
                // whose reverse PropertyRenter still names this property is an occupant even if an
                // initialization/removal pass has temporarily hidden one of the components needed
                // to serialize it. Fail the whole absolute property in that case; omitting the
                // family would turn a transient read into a remote move-out.
                if (!EntityManager.HasComponent<PropertyRenter>(renter) ||
                    EntityManager.GetComponentData<PropertyRenter>(renter).m_Property != property)
                    continue;
                if (!EntityManager.HasComponent<PrefabRef>(renter) ||
                    !EntityManager.HasBuffer<HouseholdCitizen>(renter) ||
                    !EntityManager.HasBuffer<Resources>(renter)) return false;
                if (households.Count >= ResidentialOccupancySnapshot.MaxHouseholdsPerProperty)
                    return false;
                OccupancyHousehold household;
                if (!TryCaptureHousehold(renter, out household)) return false;
                households.Add(household);
                householdEntities.Add(renter);
            }

            global::Game.Objects.Transform transform =
                EntityManager.GetComponentData<global::Game.Objects.Transform>(property);
            byte constructionSpeed = 0;
            if (EntityManager.HasComponent<global::Game.Objects.UnderConstruction>(property))
            {
                // Zero means finished, so a site whose speed has not been drawn yet still reads as
                // "building". One is as slow as the game ever goes.
                byte speed = EntityManager
                    .GetComponentData<global::Game.Objects.UnderConstruction>(property).m_Speed;
                constructionSpeed = speed == 0 ? (byte)1 : speed;
            }
            result = new OccupancyProperty
            {
                PrefabName = prefabName,
                AnchorX = transform.m_Position.x,
                AnchorY = transform.m_Position.y,
                AnchorZ = transform.m_Position.z,
                Revision = NextHostRevision(),
                ConstructionSpeed = constructionSpeed,
                Households = households.ToArray(),
            };
            // City-state capture is shared: never let a broken local asset name or transform reach
            // Write, where a throw would suppress every other channel in the same snapshot. Host
            // identity tracking is committed only after this complete property is valid too.
            if (!ResidentialOccupancySnapshot.IsValidProperty(result)) return false;

            MultiplayerService service = Mod.Service;
            long now = service != null ? service.NowMs : 0;
            for (int h = 0; h < result.Households.Length; h++)
            {
                OccupancyHousehold household = result.Households[h];
                ObserveHostHouseholdEntity(householdEntities[h], household, result.Revision);
                ObserveHostHouseholdCitizenRoster(householdEntities[h], household,
                    result.Revision, now);
                if (household.Departing)
                {
                    RecordHostDeparture(household.HouseholdId, result.Revision, now, false);
                    RecordHostCitizensForDepartingHousehold(householdEntities[h],
                        household.HouseholdId, result.Revision, now, true);
                    _hostHouseholds.Remove(household.HouseholdId);
                }
                else
                    _hostDepartures.Remove(household.HouseholdId);
            }
            return true;
        }

        private bool IsCapturableHousehold(Entity renter, Entity property) =>
            renter != Entity.Null && EntityManager.Exists(renter) &&
            EntityManager.HasComponent<Household>(renter) &&
            EntityManager.HasComponent<PrefabRef>(renter) &&
            EntityManager.HasBuffer<HouseholdCitizen>(renter) &&
            EntityManager.HasBuffer<Resources>(renter) &&
            EntityManager.HasComponent<PropertyRenter>(renter) &&
            !EntityManager.HasComponent<Deleted>(renter) &&
            !EntityManager.HasComponent<Temp>(renter) &&
            !EntityManager.HasComponent<TouristHousehold>(renter) &&
            !EntityManager.HasComponent<CommuterHousehold>(renter) &&
            EntityManager.GetComponentData<PropertyRenter>(renter).m_Property == property;

        private bool TryCaptureHousehold(Entity entity, out OccupancyHousehold result)
        {
            result = default(OccupancyHousehold);
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab;
            string prefabName = _prefabIndex.NameOf(prefab);
            if (string.IsNullOrEmpty(prefabName)) return false;

            Household data = EntityManager.GetComponentData<Household>(entity);
            PropertyRenter rented = EntityManager.GetComponentData<PropertyRenter>(entity);
            DynamicBuffer<Resources> resources = EntityManager.GetBuffer<Resources>(entity, true);

            DynamicBuffer<HouseholdCitizen> members =
                EntityManager.GetBuffer<HouseholdCitizen>(entity, true);
            if (members.Length > ResidentialOccupancySnapshot.MaxCitizensPerHousehold) return false;
            var citizens = new List<OccupancyCitizen>(members.Length);
            for (int i = 0; i < members.Length; i++)
            {
                Entity citizenEntity = members[i].m_Citizen;
                if (citizenEntity == Entity.Null || !EntityManager.Exists(citizenEntity) ||
                    !EntityManager.HasComponent<HouseholdMember>(citizenEntity) ||
                    EntityManager.GetComponentData<HouseholdMember>(citizenEntity).m_Household != entity)
                    return false;
                OccupancyCitizen citizen;
                // An absolute roster must never turn a transient/incomplete read into a remote
                // deletion. Retry the whole property on a later capture instead.
                if (!TryCaptureCitizen(citizenEntity, out citizen)) return false;
                citizens.Add(citizen);
            }

            var pets = new List<string>();
            if (EntityManager.HasBuffer<HouseholdAnimal>(entity))
            {
                DynamicBuffer<HouseholdAnimal> animals =
                    EntityManager.GetBuffer<HouseholdAnimal>(entity, true);
                if (animals.Length > ResidentialOccupancySnapshot.MaxPetsPerHousehold) return false;
                for (int i = 0; i < animals.Length; i++)
                {
                    Entity pet = animals[i].m_HouseholdPet;
                    if (!EntityManager.Exists(pet) || EntityManager.HasComponent<Deleted>(pet) ||
                        !EntityManager.HasComponent<PrefabRef>(pet) ||
                        !EntityManager.HasComponent<HouseholdPet>(pet) ||
                        EntityManager.GetComponentData<HouseholdPet>(pet).m_Household != entity)
                        return false;
                    string petName = _prefabIndex.NameOf(
                        EntityManager.GetComponentData<PrefabRef>(pet).m_Prefab);
                    if (string.IsNullOrEmpty(petName)) return false;
                    pets.Add(petName);
                }
            }

            string[] ownedVehicles;
            if (!TryCaptureOwnedVehicles(entity, out ownedVehicles)) return false;

            result = new OccupancyHousehold
            {
                HouseholdId = PackHostEntityId(entity),
                PrefabName = prefabName,
                Flags = (byte)data.m_Flags,
                Departing = EntityManager.HasComponent<global::Game.Agents.MovingAway>(entity),
                Rent = Clamp(rented.m_Rent, 0, ResidentialOccupancySnapshot.MaxRent),
                Savings = Clamp(data.m_Resources, -ResidentialOccupancySnapshot.MaxMoney,
                    ResidentialOccupancySnapshot.MaxMoney),
                Money = Clamp(EconomyUtils.GetResources(Resource.Money, resources),
                    -ResidentialOccupancySnapshot.MaxMoney,
                    ResidentialOccupancySnapshot.MaxMoney),
                ConsumptionPerDay = data.m_ConsumptionPerDay,
                ShoppedValuePerDay = data.m_ShoppedValuePerDay,
                ShoppedValueLastDay = data.m_ShoppedValueLastDay,
                LastDayFrameIndex = data.m_LastDayFrameIndex,
                SalaryLastDay = Clamp(data.m_SalaryLastDay,
                    -ResidentialOccupancySnapshot.MaxMoney,
                    ResidentialOccupancySnapshot.MaxMoney),
                MoneySpentOnBuildingLevelingLastDay = Clamp(
                    data.m_MoneySpendOnBuildingLevelingLastDay,
                    -ResidentialOccupancySnapshot.MaxMoney,
                    ResidentialOccupancySnapshot.MaxMoney),
                NameIndices = CaptureNameIndices(entity),
                Citizens = citizens.ToArray(),
                Pets = pets.ToArray(),
                OwnedVehicles = ownedVehicles,
            };
            return true;
        }

        private bool TryCaptureOwnedVehicles(Entity household, out string[] result)
        {
            result = EmptyVehiclePrefabs;
            if (!EntityManager.HasBuffer<OwnedVehicle>(household)) return true;

            DynamicBuffer<OwnedVehicle> owned = EntityManager.GetBuffer<OwnedVehicle>(household, true);
            var prefabs = new List<string>(owned.Length);
            for (int i = 0; i < owned.Length; i++)
            {
                Entity vehicle = owned[i].m_Vehicle;
                if (vehicle == Entity.Null || !EntityManager.Exists(vehicle) ||
                    EntityManager.HasComponent<Deleted>(vehicle)) continue;
                if (!EntityManager.HasComponent<global::Game.Vehicles.PersonalCar>(vehicle))
                    continue;
                if (!EntityManager.HasComponent<Owner>(vehicle) ||
                    EntityManager.GetComponentData<Owner>(vehicle).m_Owner != household)
                    continue;
                if (!EntityManager.HasComponent<PrefabRef>(vehicle)) return false;

                string name = _prefabIndex.NameOf(
                    EntityManager.GetComponentData<PrefabRef>(vehicle).m_Prefab);
                if (string.IsNullOrEmpty(name)) return false;
                if (prefabs.Count >= ResidentialOccupancySnapshot.MaxVehiclesPerHousehold)
                    return false;
                prefabs.Add(name);
            }
            if (prefabs.Count == 0) return true;
            prefabs.Sort(System.StringComparer.Ordinal);
            result = prefabs.ToArray();
            return true;
        }

        /// <summary>
        /// The random name slots behind a family surname or a person's first name. Drawn per
        /// machine, so they are the difference between "the same family" and "a family with the
        /// same numbers".
        /// </summary>
        private int[] CaptureNameIndices(Entity entity)
        {
            if (!EntityManager.HasBuffer<RandomLocalizationIndex>(entity)) return EmptyNameIndices;
            DynamicBuffer<RandomLocalizationIndex> indices =
                EntityManager.GetBuffer<RandomLocalizationIndex>(entity, true);
            int count = indices.Length;
            if (count > ResidentialOccupancySnapshot.MaxNameIndices)
                count = ResidentialOccupancySnapshot.MaxNameIndices;
            if (count == 0) return EmptyNameIndices;
            var result = new int[count];
            for (int i = 0; i < count; i++)
                result[i] = indices[i].m_Index < -1 ? -1 : indices[i].m_Index;
            return result;
        }

        private bool TryCaptureCitizen(Entity entity, out OccupancyCitizen result)
        {
            result = default(OccupancyCitizen);
            if (entity == Entity.Null || !EntityManager.Exists(entity) ||
                EntityManager.HasComponent<Deleted>(entity) ||
                !EntityManager.HasComponent<Citizen>(entity) ||
                !EntityManager.HasComponent<PrefabRef>(entity)) return false;
            string name = _prefabIndex.NameOf(
                EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab);
            if (string.IsNullOrEmpty(name)) return false;

            Citizen data = EntityManager.GetComponentData<Citizen>(entity);
            bool employed = false;
            byte level = 0;
            if (EntityManager.HasComponent<Worker>(entity))
            {
                Worker worker = EntityManager.GetComponentData<Worker>(entity);
                employed = true;
                level = worker.m_Level > ResidentialOccupancySnapshot.MaxWorkerLevel
                    ? (byte)ResidentialOccupancySnapshot.MaxWorkerLevel : worker.m_Level;
            }
            result = new OccupancyCitizen
            {
                CitizenId = PackHostEntityId(entity),
                PrefabName = name,
                State = (short)data.m_State,
                PseudoRandom = data.m_PseudoRandom,
                BirthDay = data.m_BirthDay,
                Health = data.m_Health,
                WellBeing = data.m_WellBeing,
                Employment = OccupancyCitizen.PackEmployment(employed, level),
                UnemploymentCounter = Clamp(data.m_UnemploymentCounter, 0,
                    ResidentialOccupancySnapshot.MaxMoney),
                NameIndices = CaptureNameIndices(entity),
            };
            return true;
        }
    }
}
