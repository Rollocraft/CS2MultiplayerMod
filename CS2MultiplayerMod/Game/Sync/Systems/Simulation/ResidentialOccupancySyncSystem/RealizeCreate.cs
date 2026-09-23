using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Commands;
using Game.Agents;
using Game.Citizens;
using Game.Common;
using Game.Economy;
using Game.Prefabs;
using Game.Vehicles;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    public partial class ResidentialOccupancySyncSystem
    {
        private Entity CreateHousehold(Entity property, OccupancyHousehold wanted)
        {
            if (!CanEnqueueRentAction()) return Entity.Null;
            if (!ResolvePrefab<HouseholdData>(wanted.PrefabName, out Entity prefab, out EntityArchetype archetype))
                return Entity.Null;

            Entity household = EntityManager.CreateEntity(archetype);
            SetOrAdd(household, new PrefabRef(prefab));
            // No CurrentBuilding: that asks the game to populate a random family.
            Household data = EntityManager.GetComponentData<Household>(household);
            data.m_Flags = (HouseholdFlags)(wanted.Flags & HouseholdFlagMask);
            data.m_Resources = wanted.Savings;
            data.m_ConsumptionPerDay = wanted.ConsumptionPerDay;
            data.m_ShoppedValuePerDay = wanted.ShoppedValuePerDay;
            data.m_ShoppedValueLastDay = wanted.ShoppedValueLastDay;
            data.m_LastDayFrameIndex = wanted.LastDayFrameIndex;
            data.m_Income = wanted.Income;
            data.m_MoneySpendOnBuildingLevelingLastDay =
                wanted.MoneySpentOnBuildingLevelingLastDay;
            EntityManager.SetComponentData(household, data);
            if (!BindHousehold(wanted.HouseholdId, household))
            {
                EntityManager.AddComponent<Deleted>(household);
                return Entity.Null;
            }
            if (!EntityManager.HasBuffer<Resources>(household))
                EntityManager.AddBuffer<Resources>(household);
            EconomyUtils.SetResources(Resource.Money,
                EntityManager.GetBuffer<Resources>(household), wanted.Money);

            if (EntityManager.HasComponent<PropertySeeker>(household))
                EntityManager.SetComponentEnabled<PropertySeeker>(household, false);
            ApplyNameIndices(household, wanted.NameIndices);
            if (!EnqueueRentAction(property, household))
            {
                UnbindHousehold(household);
                if (!EntityManager.HasComponent<Deleted>(household))
                    EntityManager.AddComponent<Deleted>(household);
                return Entity.Null;
            }

            Entity arrivalSource = SelectArrivalSource(wanted.HouseholdId);
            if (arrivalSource != Entity.Null)
                _arrivalSources[household] = arrivalSource;
            else
            {
                arrivalSource = property;
                if (!_arrivalSourceWarned)
                {
                    _arrivalSourceWarned = true;
                    SyncLog.Detail(LogTopic.Residential,
                        "Occupancy: no live road outside connection was " +
                        "available; new families will start at home.");
                }
            }

            // A new host household can be a split: known citizens move rather than being cloned.
            CreateInitialOwnedVehicles(household, property, arrivalSource, wanted);
            ApplyCitizens(household, property, wanted);
            ApplyPets(household, property, wanted);
            MarkSettling(household);
            return household;
        }

        private Entity CreateCitizen(Entity household, Entity property, OccupancyCitizen wanted)
        {
            if (!TryGetCitizenCreationPrefab(out Entity prefab, out EntityArchetype archetype))
                return Entity.Null;

            Entity citizen = EntityManager.CreateEntity(archetype);
            SetOrAdd(citizen, new PrefabRef(prefab));
            SetOrAdd(citizen, new HouseholdMember { m_Household = household });
            SetOrAdd(citizen, new CurrentBuilding
            {
                m_CurrentBuilding = GetVehicleCreationSource(household, property),
            });
            // Initialization reads 0..4 as an age class; the next reconcile applies the host's values.
            SetOrAdd(citizen, new Citizen
            {
                m_BirthDay = SeedAgeClass(wanted),
                m_State = (CitizenFlags)(wanted.State &
                    (short)(CitizenFlags.Tourist | CitizenFlags.Commuter)),
            });
            if (!BindCitizen(wanted.CitizenId, citizen))
            {
                EntityManager.AddComponent<Deleted>(citizen);
                return Entity.Null;
            }
            return citizen;
        }

        private static short SeedAgeClass(OccupancyCitizen wanted)
        {
            var state = (CitizenFlags)wanted.State;
            var probe = new Citizen { m_State = state };
            switch (probe.GetAge())
            {
                case CitizenAge.Adult: return 1;
                case CitizenAge.Elderly: return 3;
                default: return 2;
            }
        }

        private Entity CreatePet(Entity household, Entity property, string prefabName)
        {
            if (!ResolvePrefab<HouseholdPetData>(prefabName, out Entity prefab, out EntityArchetype archetype))
                return Entity.Null;

            Entity pet = EntityManager.CreateEntity(archetype);
            SetOrAdd(pet, new PrefabRef(prefab));
            SetOrAdd(pet, new HouseholdPet { m_Household = household });
            SetOrAdd(pet, new CurrentBuilding
            {
                m_CurrentBuilding = GetVehicleCreationSource(household, property),
            });
            LinkPet(household, pet);
            return pet;
        }

        private Entity CreateOwnedVehicle(Entity household, Entity source, ulong householdId,
            string prefabName, int ordinal)
        {
            if (!_prefabIndex.TryResolve(prefabName,
                    candidate => EntityManager.HasComponent<PersonalCarData>(candidate) &&
                                 EntityManager.HasComponent<global::Game.Prefabs.CarData>(candidate) &&
                                 EntityManager.HasComponent<MovingObjectData>(candidate),
                    out Entity prefab) ||
                !EntityManager.HasComponent<global::Game.Objects.Transform>(source))
                return Entity.Null;
            EntityArchetype archetype =
                EntityManager.GetComponentData<MovingObjectData>(prefab).m_StoppedArchetype;
            if (!archetype.Valid) return Entity.Null;

            Entity vehicle = EntityManager.CreateEntity(archetype);
            SetOrAdd(vehicle,
                EntityManager.GetComponentData<global::Game.Objects.Transform>(source));
            SetOrAdd(vehicle, new global::Game.Vehicles.PersonalCar(
                Entity.Null, default(PersonalCarFlags)));
            SetOrAdd(vehicle, new PrefabRef(prefab));
            ushort seed = unchecked((ushort)(householdId ^ (ulong)(ordinal + 1) * 40503UL));
            SetOrAdd(vehicle, new PseudoRandomSeed(seed == 0 ? (ushort)1 : seed));
            SetOrAdd(vehicle, new global::Game.Objects.TripSource(source));
            SetOrAdd(vehicle, default(global::Game.Objects.Unspawned));
            SetOrAdd(vehicle, new Owner(household));
            return vehicle;
        }
    }
}
