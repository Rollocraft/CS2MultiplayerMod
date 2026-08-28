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
    // The people and pets in a household: matching the host's roster against who is already
    // there, moving citizens between households, removing duplicates, and applying each
    // citizen's own state.
    public partial class ResidentialOccupancySyncSystem
    {
        private void ApplyCitizens(Entity household, Entity property, OccupancyHousehold wanted)
        {
            if (!EntityManager.HasBuffer<HouseholdCitizen>(household)) return;
            DedupeCitizens(household);
            // Snapshot before touching anything: creating or deleting an entity is a structural
            // change, and every dynamic buffer handle taken before it becomes invalid.
            _memberScratch.Clear();
            DynamicBuffer<HouseholdCitizen> members =
                EntityManager.GetBuffer<HouseholdCitizen>(household, true);
            for (int i = 0; i < members.Length; i++) _memberScratch.Add(members[i].m_Citizen);

            _claimedCitizens.Clear();
            _wantedCitizenIds.Clear();
            bool missingWanted = false;
            bool settling = IsSettling(household);
            if (settling) ScheduleReapply(property);

            for (int i = 0; i < wanted.Citizens.Length; i++)
            {
                OccupancyCitizen desired = wanted.Citizens[i];
                if (!IsCitizenDesiredHere(desired.CitizenId, wanted.HouseholdId)) continue;
                _wantedCitizenIds.Add(desired.CitizenId);

                Entity citizen;
                bool createdNow = false;
                if (!TryResolveCitizen(desired.CitizenId, out citizen))
                {
                    citizen = FindBootstrapCitizen(desired);
                    if (citizen != Entity.Null) BindCitizen(desired.CitizenId, citizen);
                }
                if (citizen == Entity.Null)
                {
                    if (settling || _budget.CitizensCreated >= MaxCitizensCreatedPerUpdate)
                    {
                        missingWanted = true;
                        continue;
                    }
                    citizen = CreateCitizen(household, property, desired);
                    if (citizen == Entity.Null)
                    {
                        missingWanted = true;
                        continue;
                    }
                    _budget.CitizensCreated++;
                    _createdCitizens++;
                    createdNow = true;
                    MarkSettling(household);
                    ScheduleReapply(property);
                }
                else if (!CitizenBelongsToHousehold(citizen, household))
                {
                    if (settling)
                    {
                        missingWanted = true;
                        continue;
                    }
                    MoveCitizenToHousehold(citizen, household);
                    MarkSettling(household);
                    ScheduleReapply(property);
                }

                _claimedCitizens.Add(citizen);
                // CitizenInitializeSystem consumes the small age-class marker on a Created
                // citizen. Replacing it with the host's calendar birthday in this same frame
                // would skip native initialization and leave the person outside population.
                if (!createdNow) ApplyCitizen(citizen, desired);
            }

            // Do not remove unmatched residents until every desired identity is present. This
            // keeps a creation budget boundary from momentarily emptying and retiring the family.
            if (settling || missingWanted) return;
            for (int i = _memberScratch.Count - 1; i >= 0; i--)
            {
                Entity citizen = _memberScratch[i];
                if (_claimedCitizens.Contains(citizen) || citizen == Entity.Null ||
                    !EntityManager.Exists(citizen) || EntityManager.HasComponent<Deleted>(citizen))
                    continue;
                ulong localId, desiredHouseholdId;
                bool bound = TryGetBoundCitizenId(citizen, out localId);
                if (bound && TryGetDesiredHouseholdId(localId, out desiredHouseholdId)) continue;
                if (!bound && DeferUnboundRetirement(citizen, _unboundCitizenSince))
                {
                    ScheduleReapply(property);
                    continue;
                }
                UnbindCitizen(citizen);
                _unboundCitizenSince.Remove(citizen);
                EntityManager.AddComponent<Deleted>(citizen);
                _removedCitizens++;
            }
        }

        private Entity FindBootstrapCitizen(OccupancyCitizen wanted)
        {
            EnsureBootstrapIdentityIndex();
            List<Entity> globalCandidates;
            if (!_bootstrapCitizenIndex.TryGetValue(CitizenBootstrapKey(wanted),
                out globalCandidates)) return Entity.Null;
            Entity match = Entity.Null;
            for (int i = 0; i < globalCandidates.Count; i++)
            {
                Entity candidate = globalCandidates[i];
                if (_claimedCitizens.Contains(candidate) || candidate == Entity.Null ||
                    !EntityManager.Exists(candidate) || EntityManager.HasComponent<Deleted>(candidate))
                    continue;
                ulong alreadyBound;
                if (TryGetBoundCitizenId(candidate, out alreadyBound)) continue;
                if (!CitizenBootstrapMatches(candidate, wanted)) continue;
                if (match != Entity.Null && match != candidate) return Entity.Null;
                match = candidate;
            }
            return match;
        }

        private bool CitizenBelongsToHousehold(Entity citizen, Entity household)
        {
            if (!EntityManager.HasComponent<HouseholdMember>(citizen) ||
                EntityManager.GetComponentData<HouseholdMember>(citizen).m_Household != household ||
                !EntityManager.HasBuffer<HouseholdCitizen>(household)) return false;
            DynamicBuffer<HouseholdCitizen> members =
                EntityManager.GetBuffer<HouseholdCitizen>(household, true);
            for (int i = 0; i < members.Length; i++)
                if (members[i].m_Citizen == citizen) return true;
            return false;
        }

        private bool HasActiveDesiredCitizenStillLinked(Entity household)
        {
            if (household == Entity.Null || !EntityManager.Exists(household) ||
                !EntityManager.HasBuffer<HouseholdCitizen>(household)) return false;
            DynamicBuffer<HouseholdCitizen> members =
                EntityManager.GetBuffer<HouseholdCitizen>(household, true);
            for (int i = 0; i < members.Length; i++)
            {
                ulong citizenId, desiredHouseholdId;
                if (TryGetBoundCitizenId(members[i].m_Citizen, out citizenId) &&
                    TryGetDesiredHouseholdId(citizenId, out desiredHouseholdId)) return true;
            }
            return false;
        }

        private bool DeferUnboundRetirement(Entity entity, Dictionary<Entity, uint> observed)
        {
            uint now = _simulationSystem.frameIndex;
            uint since;
            if (!observed.TryGetValue(entity, out since))
            {
                observed[entity] = now;
                return true;
            }
            if (now - since < BootstrapRetirementGraceFrames) return true;
            return false;
        }

        private void MoveCitizenToHousehold(Entity citizen, Entity household)
        {
            if (EntityManager.HasComponent<HouseholdMember>(citizen))
            {
                Entity previous = EntityManager.GetComponentData<HouseholdMember>(citizen).m_Household;
                if (previous != Entity.Null && previous != household && EntityManager.Exists(previous) &&
                    EntityManager.HasBuffer<HouseholdCitizen>(previous))
                {
                    DynamicBuffer<HouseholdCitizen> oldMembers =
                        EntityManager.GetBuffer<HouseholdCitizen>(previous);
                    for (int i = oldMembers.Length - 1; i >= 0; i--)
                        if (oldMembers[i].m_Citizen == citizen) oldMembers.RemoveAt(i);
                }
            }
            SetOrAdd(citizen, new HouseholdMember { m_Household = household });
            // Household membership and physical location are separate native graphs. An existing
            // citizen may be at work, school, or in transit, so changing CurrentBuilding here would
            // leave its Occupant/path state pointing at a different place. Newly created citizens
            // receive their initial home location in CreateCitizen instead.
            LinkCitizen(household, citizen);
            DedupeCitizens(household);
        }

        /// <summary>
        /// The game's citizen initialization appends every newly created citizen to its household,
        /// including the ones this system already linked. Collapsing repeats here is cheaper and
        /// safer than trying to predict that append, and it also clears out members whose entity
        /// is gone.
        /// </summary>
        private void DedupeCitizens(Entity household)
        {
            DynamicBuffer<HouseholdCitizen> members =
                EntityManager.GetBuffer<HouseholdCitizen>(household);
            for (int i = members.Length - 1; i >= 0; i--)
            {
                Entity citizen = members[i].m_Citizen;
                bool duplicate = false;
                for (int j = 0; j < i; j++)
                {
                    if (members[j].m_Citizen != citizen) continue;
                    duplicate = true;
                    break;
                }
                bool wrongHousehold = citizen != Entity.Null && EntityManager.Exists(citizen) &&
                    EntityManager.HasComponent<HouseholdMember>(citizen) &&
                    EntityManager.GetComponentData<HouseholdMember>(citizen).m_Household != household;
                if (duplicate || citizen == Entity.Null || !EntityManager.Exists(citizen) ||
                    EntityManager.HasComponent<Deleted>(citizen) || wrongHousehold)
                    members.RemoveAt(i);
            }
        }

        private void DedupePets(Entity household)
        {
            if (!EntityManager.HasBuffer<HouseholdAnimal>(household)) return;
            DynamicBuffer<HouseholdAnimal> animals =
                EntityManager.GetBuffer<HouseholdAnimal>(household);
            for (int i = animals.Length - 1; i >= 0; i--)
            {
                Entity pet = animals[i].m_HouseholdPet;
                bool duplicate = false;
                for (int j = 0; j < i; j++)
                {
                    if (animals[j].m_HouseholdPet != pet) continue;
                    duplicate = true;
                    break;
                }
                if (duplicate || pet == Entity.Null || !EntityManager.Exists(pet))
                    animals.RemoveAt(i);
            }
        }

        /// <summary>
        /// Link an existing citizen moved between households immediately. Fresh citizens are left
        /// for the initialization pass to append exactly once; a duplicate here would inflate the
        /// household size used by the first-arrival population event.
        /// </summary>
        private void LinkCitizen(Entity household, Entity citizen)
        {
            if (!EntityManager.HasBuffer<HouseholdCitizen>(household)) return;
            EntityManager.GetBuffer<HouseholdCitizen>(household)
                .Add(new HouseholdCitizen { m_Citizen = citizen });
        }

        private void LinkPet(Entity household, Entity pet)
        {
            if (!EntityManager.HasBuffer<HouseholdAnimal>(household))
                EntityManager.AddBuffer<HouseholdAnimal>(household);
            EntityManager.GetBuffer<HouseholdAnimal>(household)
                .Add(new HouseholdAnimal { m_HouseholdPet = pet });
        }

        private void ApplyCitizen(Entity citizen, OccupancyCitizen wanted)
        {
            if (citizen == Entity.Null || !EntityManager.Exists(citizen) ||
                !EntityManager.HasComponent<Citizen>(citizen)) return;

            Citizen data = EntityManager.GetComponentData<Citizen>(citizen);
            var state = (CitizenFlags)(((short)data.m_State & ~HostOwnedCitizenFlags) |
                                       (wanted.State & HostOwnedCitizenFlags));
            if (data.m_State != state || data.m_PseudoRandom != wanted.PseudoRandom ||
                data.m_BirthDay != wanted.BirthDay || data.m_Health != wanted.Health ||
                data.m_WellBeing != wanted.WellBeing ||
                data.m_UnemploymentCounter != wanted.UnemploymentCounter)
            {
                data.m_State = state;
                data.m_PseudoRandom = wanted.PseudoRandom;
                data.m_BirthDay = wanted.BirthDay;
                data.m_Health = wanted.Health;
                data.m_WellBeing = wanted.WellBeing;
                data.m_UnemploymentCounter = wanted.UnemploymentCounter;
                EntityManager.SetComponentData(citizen, data);
                _rewrittenCitizens++;
            }

            Entity prefab;
            if (ResolveCitizenPrefab(wanted.PrefabName, out prefab) &&
                EntityManager.HasComponent<PrefabRef>(citizen) &&
                EntityManager.GetComponentData<PrefabRef>(citizen).m_Prefab != prefab)
                EntityManager.SetComponentData(citizen, new PrefabRef(prefab));

            ApplyNameIndices(citizen, wanted.NameIndices);
            ApplyWageLevel(citizen, wanted);
        }

        /// <summary>
        /// Keep the wage level coherent when both peers already have this citizen employed. The
        /// employment graph remains local because a valid Worker also requires a matching workplace
        /// Employee entry. Displayed household income is authoritative through SalaryLastDay on the
        /// household snapshot, so no invalid placeholder job is manufactured here.
        /// </summary>
        private void ApplyWageLevel(Entity citizen, OccupancyCitizen wanted)
        {
            if (!wanted.Employed || !EntityManager.HasComponent<Worker>(citizen)) return;
            Worker worker = EntityManager.GetComponentData<Worker>(citizen);
            Entity workplace = worker.m_Workplace;
            if (workplace == Entity.Null || !EntityManager.Exists(workplace) ||
                !EntityManager.HasBuffer<Employee>(workplace)) return;
            DynamicBuffer<Employee> employees = EntityManager.GetBuffer<Employee>(workplace);
            int employeeIndex = -1;
            for (int i = 0; i < employees.Length; i++)
            {
                if (employees[i].m_Worker != citizen) continue;
                employeeIndex = i;
                break;
            }
            // A Worker without its reverse Employee link is already inconsistent. Do not mutate
            // half of that graph; the local job systems own repairing or replacing the job.
            if (employeeIndex < 0) return;

            if (worker.m_Level != wanted.WorkerLevel)
            {
                worker.m_Level = wanted.WorkerLevel;
                EntityManager.SetComponentData(citizen, worker);
            }
            Employee employee = employees[employeeIndex];
            if (employee.m_Level != wanted.WorkerLevel)
            {
                employee.m_Level = wanted.WorkerLevel;
                employees[employeeIndex] = employee;
            }
        }

        private void ApplyPets(Entity household, Entity property, OccupancyHousehold wanted)
        {
            DedupePets(household);
            _memberScratch.Clear();
            if (EntityManager.HasBuffer<HouseholdAnimal>(household))
            {
                DynamicBuffer<HouseholdAnimal> animals =
                    EntityManager.GetBuffer<HouseholdAnimal>(household, true);
                for (int i = 0; i < animals.Length; i++) _memberScratch.Add(animals[i].m_HouseholdPet);
            }

            _claimedPets.Clear();
            _missingPetPrefabs.Clear();
            for (int i = 0; i < wanted.Pets.Length; i++)
            {
                Entity match = Entity.Null;
                for (int j = 0; j < _memberScratch.Count; j++)
                {
                    Entity candidate = _memberScratch[j];
                    if (_claimedPets.Contains(candidate) || candidate == Entity.Null ||
                        !EntityManager.Exists(candidate) ||
                        !EntityManager.HasComponent<PrefabRef>(candidate)) continue;
                    string localName = _prefabIndex.NameOf(
                        EntityManager.GetComponentData<PrefabRef>(candidate).m_Prefab);
                    if (!string.Equals(localName, wanted.Pets[i], StringComparison.Ordinal)) continue;
                    match = candidate;
                    break;
                }
                if (match == Entity.Null) _missingPetPrefabs.Add(wanted.Pets[i]);
                else _claimedPets.Add(match);
            }

            if (_claimedPets.Count == _memberScratch.Count && _missingPetPrefabs.Count == 0) return;
            if (IsSettling(household))
            {
                ScheduleReapply(property);
                return;
            }

            for (int i = _memberScratch.Count - 1; i >= 0; i--)
            {
                Entity pet = _memberScratch[i];
                if (_claimedPets.Contains(pet) || pet == Entity.Null || !EntityManager.Exists(pet) ||
                    EntityManager.HasComponent<Deleted>(pet)) continue;
                EntityManager.AddComponent<Deleted>(pet);
            }

            for (int i = 0; i < _missingPetPrefabs.Count; i++)
            {
                if (CreatePet(household, property, _missingPetPrefabs[i]) == Entity.Null) break;
                _createdPets++;
                MarkSettling(household);
                ScheduleReapply(property);
            }
        }
    }
}
