using System;
using System.Collections.Generic;
using System.Diagnostics;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.Infrastructure;
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
    // Creating the entities a host roster names but this peer does not have yet - household,
    // citizen, pet, owned vehicle - each seeded so the two peers agree on what was drawn.
    public partial class ResidentialOccupancySyncSystem
    {
        // ---- Creation and retirement -------------------------------------------

        private Entity CreateHousehold(Entity property, OccupancyHousehold wanted)
        {
            if (!CanEnqueueRentAction()) return Entity.Null;
            Entity prefab;
            EntityArchetype archetype;
            if (!ResolvePrefab<HouseholdData>(wanted.PrefabName, out prefab, out archetype))
                return Entity.Null;

            Entity household = EntityManager.CreateEntity(archetype);
            SetOrAdd(household, new PrefabRef(prefab));
            // No CurrentBuilding: that component is what asks the game to populate a household with
            // a randomly drawn family. The roster already says who lives here.
            Household data = EntityManager.GetComponentData<Household>(household);
            data.m_Flags = (HouseholdFlags)(wanted.Flags & HouseholdFlagMask);
            data.m_Resources = wanted.Savings;
            data.m_ConsumptionPerDay = wanted.ConsumptionPerDay;
            data.m_ShoppedValuePerDay = wanted.ShoppedValuePerDay;
            data.m_ShoppedValueLastDay = wanted.ShoppedValueLastDay;
            data.m_LastDayFrameIndex = wanted.LastDayFrameIndex;
            data.m_SalaryLastDay = wanted.SalaryLastDay;
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
                    Mod.Verbose("[MP] Occupancy: no live road outside connection was " +
                                "available; new families will start at home.");
                }
            }

            // A new host household can be a split containing an already-known citizen. Funnel the
            // roster through the keyed reconciler so existing people move with their job/path state
            // instead of being cloned and rebound to a second local entity.
            CreateInitialOwnedVehicles(household, property, arrivalSource, wanted);
            ApplyCitizens(household, property, wanted);
            ApplyPets(household, property, wanted);
            MarkSettling(household);
            return household;
        }

        private Entity CreateCitizen(Entity household, Entity property, OccupancyCitizen wanted)
        {
            Entity prefab;
            EntityArchetype archetype;
            if (!TryGetCitizenCreationPrefab(out prefab, out archetype))
                return Entity.Null;

            Entity citizen = EntityManager.CreateEntity(archetype);
            SetOrAdd(citizen, new PrefabRef(prefab));
            SetOrAdd(citizen, new HouseholdMember { m_Household = household });
            SetOrAdd(citizen, new CurrentBuilding
            {
                m_CurrentBuilding = GetVehicleCreationSource(household, property),
            });
            // The game's citizen initialization reads 0..4 as an age class rather than a calendar
            // day, and turns it into a plausible birthday, education band and prefab. Seed the
            // class it expects; the next reconcile replaces the result with the host's own values.
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
            Entity prefab;
            EntityArchetype archetype;
            if (!ResolvePrefab<HouseholdPetData>(prefabName, out prefab, out archetype))
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
            Entity prefab;
            if (!_prefabIndex.TryResolve(prefabName,
                    candidate => EntityManager.HasComponent<PersonalCarData>(candidate) &&
                                 EntityManager.HasComponent<global::Game.Prefabs.CarData>(candidate) &&
                                 EntityManager.HasComponent<MovingObjectData>(candidate),
                    out prefab) ||
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
