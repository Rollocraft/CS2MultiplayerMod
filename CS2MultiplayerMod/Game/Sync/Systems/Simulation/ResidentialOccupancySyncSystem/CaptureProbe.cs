using System.Collections.Generic;
using CS2MultiplayerMod.Game.Sync.Commands;
using Game.Buildings;
using Game.Citizens;
using Game.Common;
using Game.Economy;
using Game.Prefabs;
using Game.Tools;
using Game.Vehicles;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    // Answering "has anything in this building changed?" without first building the answer to
    // "what is in this building?".
    //
    // The rolling sweep used to run the full wire capture on every property it looked at, hash the
    // result and drop the object on the floor. One capture allocates a list and an array for the
    // household roster, another pair per citizen roster, one per pet roster, one per vehicle
    // roster plus an ordinal sort, an int[] per household and per person for the name draws, and
    // two hash sets inside the shared validator - then reads the transform, both utility
    // consumers, the resource buffer and the tax record, none of which the hash looks at. Measured
    // at 0,32 ms per building, which is 38 ms per update once a partition holds a few hundred
    // houses.
    //
    // This folds the same fields the old hash folded, in place, allocating nothing. When it
    // reports a difference the caller still runs the real capture: that is what validates the
    // roster and what registers the households and residents the tombstone scans watch, and a
    // property that just changed is about to be paged anyway. The stored hash is only ever
    // compared against another probe hash, so the value need not agree with the old one - only the
    // notion of "changed" does.
    //
    // Reads stay on EntityManager rather than a ComponentLookup. Every EntityManager read
    // completes the write dependency for the type it touches first; a lookup acquired outside
    // OnCreate does not, and these components are written by native jobs that can still be in
    // flight. The allocations were the cost here, not the accessor.
    public partial class ResidentialOccupancySyncSystem
    {
        private readonly HashSet<ulong> _probeHouseholdIds = new HashSet<ulong>();
        private readonly HashSet<ulong> _probeCitizenIds = new HashSet<ulong>();

        /// <summary>
        /// False means "draw no conclusion from this property on this pass" - a transient or
        /// incomplete read, exactly what a failed capture always meant. The caller skips it and the
        /// baseline sweep still carries it, so a false negative costs latency, never correctness.
        /// </summary>
        private bool TryHashProperty(Entity property, out int hash)
        {
            hash = 0;

            // Liveness is deliberately not re-tested. Every entity reaching this method came out
            // of _properties, whose description already requires Building, ResidentialProperty,
            // Renter, PrefabRef, Transform and UpdateFrame and excludes Temp, Deleted and Owner -
            // the ten component reads IsLiveProperty repeats cannot come out any other way.
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(property).m_Prefab;
            if (prefab == Entity.Null || !EntityManager.Exists(prefab) ||
                !EntityManager.HasComponent<BuildingPropertyData>(prefab)) return false;
            if (string.IsNullOrEmpty(_prefabIndex.NameOf(prefab))) return false;

            byte constructionSpeed = 0;
            if (EntityManager.HasComponent<global::Game.Objects.UnderConstruction>(property))
            {
                byte speed = EntityManager
                    .GetComponentData<global::Game.Objects.UnderConstruction>(property).m_Speed;
                constructionSpeed = speed == 0 ? (byte)1 : speed;
            }

            _probeHouseholdIds.Clear();
            _probeCitizenIds.Clear();

            unchecked
            {
                int folded = (int)2166136261;
                folded = (folded ^ constructionSpeed) * 16777619;

                DynamicBuffer<Renter> renters = EntityManager.GetBuffer<Renter>(property, true);
                int householdCount = 0;
                for (int i = 0; i < renters.Length; i++)
                {
                    Entity renter = renters[i].m_Renter;
                    // Companies rent the commercial half of a mixed building; only households are
                    // ours. A stale one-way Renter entry is not an occupant either.
                    if (renter == Entity.Null || !EntityManager.Exists(renter) ||
                        !EntityManager.HasComponent<Household>(renter) ||
                        EntityManager.HasComponent<Deleted>(renter) ||
                        EntityManager.HasComponent<Temp>(renter) ||
                        EntityManager.HasComponent<TouristHousehold>(renter) ||
                        EntityManager.HasComponent<CommuterHousehold>(renter)) continue;
                    if (!EntityManager.HasComponent<PropertyRenter>(renter)) continue;
                    PropertyRenter rented =
                        EntityManager.GetComponentData<PropertyRenter>(renter);
                    if (rented.m_Property != property) continue;
                    if (!EntityManager.HasComponent<PrefabRef>(renter) ||
                        !EntityManager.HasBuffer<HouseholdCitizen>(renter) ||
                        !EntityManager.HasBuffer<Resources>(renter)) return false;
                    if (householdCount >= ResidentialOccupancySnapshot.MaxHouseholdsPerProperty)
                        return false;
                    if (!TryFoldHousehold(renter, rented, ref folded)) return false;
                    householdCount++;
                }

                // The capture folds the roster length before the households; the probe folds it
                // after, because it only learns the count by walking. Detection is the same.
                folded = (folded ^ householdCount) * 16777619;
                hash = folded;
                return true;
            }
        }

        private bool TryFoldHousehold(Entity household, PropertyRenter rented, ref int folded)
        {
            ulong householdId = PackHostEntityId(household);
            if (householdId == 0 || !_probeHouseholdIds.Add(householdId)) return false;
            string prefabName = _prefabIndex.NameOf(
                EntityManager.GetComponentData<PrefabRef>(household).m_Prefab);
            if (string.IsNullOrEmpty(prefabName)) return false;
            Household data = EntityManager.GetComponentData<Household>(household);
            bool departing =
                EntityManager.HasComponent<global::Game.Agents.MovingAway>(household);

            unchecked
            {
                folded = HashId(folded, householdId);
                folded = (folded ^ prefabName.GetHashCode()) * 16777619;
                // Only the bits a receiver actually installs. HouseholdFlags.MovedIn is owned by
                // arrival on every peer and deliberately never imported, so folding it in reports
                // a change nobody would act on.
                folded = (folded ^ ((byte)data.m_Flags & HouseholdFlagMask)) * 16777619;
                folded = (folded ^ (departing ? 1 : 0)) * 16777619;
                folded = (folded ^ Clamp(rented.m_Rent, 0,
                    ResidentialOccupancySnapshot.MaxRent)) * 16777619;
                folded = (folded ^ Clamp(data.m_SalaryLastDay,
                    -ResidentialOccupancySnapshot.MaxMoney,
                    ResidentialOccupancySnapshot.MaxMoney)) * 16777619;
                folded = FoldNameIndices(folded, household);

                DynamicBuffer<HouseholdCitizen> members =
                    EntityManager.GetBuffer<HouseholdCitizen>(household, true);
                if (members.Length > ResidentialOccupancySnapshot.MaxCitizensPerHousehold)
                    return false;
                for (int i = 0; i < members.Length; i++)
                    if (!TryFoldCitizen(members[i].m_Citizen, household, ref folded)) return false;
                folded = (folded ^ members.Length) * 16777619;

                int petCount = 0;
                if (EntityManager.HasBuffer<HouseholdAnimal>(household))
                {
                    DynamicBuffer<HouseholdAnimal> animals =
                        EntityManager.GetBuffer<HouseholdAnimal>(household, true);
                    if (animals.Length > ResidentialOccupancySnapshot.MaxPetsPerHousehold)
                        return false;
                    for (int i = 0; i < animals.Length; i++)
                    {
                        Entity pet = animals[i].m_HouseholdPet;
                        if (!EntityManager.Exists(pet) ||
                            EntityManager.HasComponent<Deleted>(pet) ||
                            !EntityManager.HasComponent<PrefabRef>(pet) ||
                            !EntityManager.HasComponent<HouseholdPet>(pet) ||
                            EntityManager.GetComponentData<HouseholdPet>(pet).m_Household !=
                            household) return false;
                        string petName = _prefabIndex.NameOf(
                            EntityManager.GetComponentData<PrefabRef>(pet).m_Prefab);
                        if (string.IsNullOrEmpty(petName)) return false;
                        folded = (folded ^ petName.GetHashCode()) * 16777619;
                        petCount++;
                    }
                }
                folded = (folded ^ petCount) * 16777619;

                // Capture sorts the vehicle names ordinally before hashing them, purely so a
                // reordered buffer does not read as a change. Summing a mixed per-name hash gives
                // the same order independence without the list and the sort, and unlike an XOR a
                // pair of identical names does not cancel itself out.
                int vehicleCount = 0;
                int vehicleFold = 0;
                if (EntityManager.HasBuffer<OwnedVehicle>(household))
                {
                    DynamicBuffer<OwnedVehicle> owned =
                        EntityManager.GetBuffer<OwnedVehicle>(household, true);
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
                        string vehicleName = _prefabIndex.NameOf(
                            EntityManager.GetComponentData<PrefabRef>(vehicle).m_Prefab);
                        if (string.IsNullOrEmpty(vehicleName)) return false;
                        if (vehicleCount >= ResidentialOccupancySnapshot.MaxVehiclesPerHousehold)
                            return false;
                        vehicleCount++;
                        vehicleFold += vehicleName.GetHashCode() * -1640531527;
                    }
                }
                folded = (folded ^ vehicleCount) * 16777619;
                folded = (folded ^ vehicleFold) * 16777619;
            }
            return true;
        }

        private bool TryFoldCitizen(Entity citizen, Entity household, ref int folded)
        {
            if (citizen == Entity.Null || !EntityManager.Exists(citizen) ||
                EntityManager.HasComponent<Deleted>(citizen) ||
                !EntityManager.HasComponent<HouseholdMember>(citizen) ||
                EntityManager.GetComponentData<HouseholdMember>(citizen).m_Household != household ||
                !EntityManager.HasComponent<Citizen>(citizen) ||
                !EntityManager.HasComponent<PrefabRef>(citizen)) return false;
            string prefabName = _prefabIndex.NameOf(
                EntityManager.GetComponentData<PrefabRef>(citizen).m_Prefab);
            if (string.IsNullOrEmpty(prefabName)) return false;
            ulong citizenId = PackHostEntityId(citizen);
            if (citizenId == 0 || !_probeCitizenIds.Add(citizenId)) return false;

            Citizen data = EntityManager.GetComponentData<Citizen>(citizen);
            byte employment;
            if (EntityManager.HasComponent<Worker>(citizen))
            {
                byte level = EntityManager.GetComponentData<Worker>(citizen).m_Level;
                if (level > ResidentialOccupancySnapshot.MaxWorkerLevel)
                    level = ResidentialOccupancySnapshot.MaxWorkerLevel;
                employment = OccupancyCitizen.PackEmployment(true, level);
            }
            else employment = OccupancyCitizen.PackEmployment(false, 0);

            bool hasProblem = EntityManager.HasComponent<HealthProblem>(citizen);
            byte problemFlags = hasProblem
                ? (byte)EntityManager.GetComponentData<HealthProblem>(citizen).m_Flags : (byte)0;

            unchecked
            {
                folded = HashId(folded, citizenId);
                folded = (folded ^ prefabName.GetHashCode()) * 16777619;
                // The mask is the whole point. A receiver merges only HostOwnedCitizenFlags and
                // preserves the rest of the word, because the rest is local behaviour:
                // LookingForPartner, BicycleUser, ValidCitizen, Homeless and MovingAwayReachOC all
                // flip while a person goes about their day. Folding the unmasked word made every
                // building with a dozen residents look changed on every single pass, which is what
                // kept the priority queue holding the entire city and made this probe skip nothing.
                folded = (folded ^ ((short)data.m_State & HostOwnedCitizenFlags)) * 16777619;
                folded = (folded ^ data.m_PseudoRandom) * 16777619;
                folded = (folded ^ data.m_BirthDay) * 16777619;
                folded = (folded ^ OccupancyCitizen.PackHealthProblem(hasProblem, problemFlags)) *
                         16777619;
                folded = (folded ^ employment) * 16777619;
                folded = FoldNameIndices(folded, citizen);
            }
            return true;
        }

        private int FoldNameIndices(int hash, Entity entity)
        {
            unchecked
            {
                if (!EntityManager.HasBuffer<RandomLocalizationIndex>(entity))
                    return hash * 16777619;
                DynamicBuffer<RandomLocalizationIndex> indices =
                    EntityManager.GetBuffer<RandomLocalizationIndex>(entity, true);
                int count = indices.Length;
                if (count > ResidentialOccupancySnapshot.MaxNameIndices)
                    count = ResidentialOccupancySnapshot.MaxNameIndices;
                hash = (hash ^ count) * 16777619;
                for (int i = 0; i < count; i++)
                    hash = (hash ^ (indices[i].m_Index < -1 ? -1 : indices[i].m_Index)) * 16777619;
                return hash;
            }
        }
    }
}
