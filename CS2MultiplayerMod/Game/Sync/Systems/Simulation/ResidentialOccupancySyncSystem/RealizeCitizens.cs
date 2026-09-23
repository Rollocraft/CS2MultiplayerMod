using CS2MultiplayerMod.Game.Sync.Infrastructure;
using System;
using System.Collections.Generic;
using CS2MultiplayerMod.Game.Sync.Commands;
using Game.Buildings;
using Game.Citizens;
using Game.Common;
using Game.Companies;
using Game.Prefabs;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    public partial class ResidentialOccupancySyncSystem
    {
        private void ApplyCitizens(Entity household, Entity property, OccupancyHousehold wanted)
        {
            if (!EntityManager.HasBuffer<HouseholdCitizen>(household)) return;
            DedupeCitizens(household);
            // Snapshot first: a structural change invalidates buffer handles.
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

                bool createdNow = false;
                if (!TryResolveCitizen(desired.CitizenId, out Entity citizen))
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
                // A Created citizen still needs native initialization, which reads the age-class marker.
                if (!createdNow) ApplyCitizen(citizen, desired);
            }

            // Remove nobody until every desired identity is present, or a budget boundary empties the family.
            if (settling || missingWanted) return;
            for (int i = _memberScratch.Count - 1; i >= 0; i--)
            {
                Entity citizen = _memberScratch[i];
                if (_claimedCitizens.Contains(citizen) || citizen == Entity.Null ||
                    !EntityManager.Exists(citizen) || EntityManager.HasComponent<Deleted>(citizen))
                    continue;
                bool bound = TryGetBoundCitizenId(citizen, out ulong localId);
                if (bound && TryGetDesiredHouseholdId(localId, out ulong desiredHouseholdId)) continue;
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
            if (!_bootstrapCitizenIndex.TryGetValue(CitizenBootstrapKey(wanted),
                out List<Entity> globalCandidates)) return Entity.Null;
            Entity match = Entity.Null;
            for (int i = 0; i < globalCandidates.Count; i++)
            {
                Entity candidate = globalCandidates[i];
                if (_claimedCitizens.Contains(candidate) || candidate == Entity.Null ||
                    !EntityManager.Exists(candidate) || EntityManager.HasComponent<Deleted>(candidate))
                    continue;
                if (TryGetBoundCitizenId(candidate, out ulong alreadyBound)) continue;
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
                if (TryGetBoundCitizenId(members[i].m_Citizen, out ulong citizenId) &&
                    TryGetDesiredHouseholdId(citizenId, out ulong desiredHouseholdId)) return true;
            }
            return false;
        }

        private bool DeferUnboundRetirement(Entity entity, Dictionary<Entity, uint> observed)
        {
            uint now = _simulationSystem.frameIndex;
            if (!observed.TryGetValue(entity, out uint since))
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
            // Membership only: an existing citizen may be away, so CurrentBuilding stays untouched.
            LinkCitizen(household, citizen);
            DedupeCitizens(household);
        }

        /// <summary>
        /// Citizen initialization appends every new citizen, including ones already linked; collapse
        /// repeats and dead members here.
        /// </summary>
        private void DedupeCitizens(Entity household)
        {
            var members = new BufferEdit<HouseholdCitizen>(EntityManager, household);
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
            var animals = new BufferEdit<HouseholdAnimal>(EntityManager, household);
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
        /// Links a moved citizen now; fresh citizens are appended once by initialization, and a duplicate
        /// would inflate the first-arrival population event.
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

            if (ResolveCitizenPrefab(wanted.PrefabName, out Entity prefab) &&
                EntityManager.HasComponent<PrefabRef>(citizen) &&
                EntityManager.GetComponentData<PrefabRef>(citizen).m_Prefab != prefab)
                EntityManager.SetComponentData(citizen, new PrefabRef(prefab));

            ApplyNameIndices(citizen, wanted.NameIndices);
            ApplyWageLevel(citizen, wanted);
            ApplyHealthProblem(citizen, wanted);
        }

        /// <summary>
        /// Health problems are lifecycle drawn from RandomSeed.Next, so presence and flags follow the host;
        /// local event and request handles are kept.
        /// </summary>
        private void ApplyHealthProblem(Entity citizen, OccupancyCitizen wanted)
        {
            bool hadProblem = EntityManager.HasComponent<HealthProblem>(citizen);
            HealthProblem current = hadProblem
                ? EntityManager.GetComponentData<HealthProblem>(citizen)
                : default(HealthProblem);
            bool wasDead = hadProblem &&
                           (current.m_Flags & HealthProblemFlags.Dead) != HealthProblemFlags.None;

            if (!wanted.HasHealthProblem)
            {
                if (!hadProblem) return;
                EntityManager.RemoveComponent<HealthProblem>(citizen);
                _healthProblemCorrections++;
                return;
            }

            HealthProblemFlags wantedFlags = (HealthProblemFlags)wanted.HealthProblemFlags;
            if (!hadProblem)
            {
                current = new HealthProblem
                {
                    m_Event = Entity.Null,
                    m_HealthcareRequest = Entity.Null,
                    m_Flags = wantedFlags,
                    m_Timer = 0,
                };
                EntityManager.AddComponentData(citizen, current);
                _healthProblemCorrections++;
            }
            else if (current.m_Flags != wantedFlags)
            {
                current.m_Flags = wantedFlags;
                EntityManager.SetComponentData(citizen, current);
                _healthProblemCorrections++;
            }

            if (!wasDead && (wantedFlags & HealthProblemFlags.Dead) != HealthProblemFlags.None)
                ApplyHostDeathTransition(citizen);
        }

        /// <summary>
        /// Mirrors DeathCheckSystem.Die's structural cleanup; HealthProblemSystem still runs the local
        /// hearse trip.
        /// </summary>
        private void ApplyHostDeathTransition(Entity citizen)
        {
            if (EntityManager.HasComponent<global::Game.Citizens.Student>(citizen))
            {
                Entity school = EntityManager
                    .GetComponentData<global::Game.Citizens.Student>(citizen).m_School;
                if (school != Entity.Null && EntityManager.Exists(school) &&
                    EntityManager.HasBuffer<global::Game.Buildings.Student>(school) &&
                    !EntityManager.HasComponent<StudentsRemoved>(school))
                    EntityManager.AddComponent<StudentsRemoved>(school);
                EntityManager.RemoveComponent<global::Game.Citizens.Student>(citizen);
            }
            if (EntityManager.HasComponent<Worker>(citizen))
                EntityManager.RemoveComponent<Worker>(citizen);
            if (EntityManager.HasComponent<ResourceBuyer>(citizen))
                EntityManager.RemoveComponent<ResourceBuyer>(citizen);
            if (EntityManager.HasComponent<Leisure>(citizen))
                EntityManager.RemoveComponent<Leisure>(citizen);
            _hostDeathTransitions++;
        }

        /// <summary>
        /// Aligns the wage level when both peers employ the citizen. Employment stays local; displayed
        /// income comes from the household snapshot.
        /// </summary>
        private void ApplyWageLevel(Entity citizen, OccupancyCitizen wanted)
        {
            if (!wanted.Employed || !EntityManager.HasComponent<Worker>(citizen)) return;
            Worker worker = EntityManager.GetComponentData<Worker>(citizen);
            Entity workplace = worker.m_Workplace;
            if (workplace == Entity.Null || !EntityManager.Exists(workplace) ||
                !EntityManager.HasBuffer<Employee>(workplace)) return;
            var employees = new BufferEdit<Employee>(EntityManager, workplace);
            int employeeIndex = -1;
            for (int i = 0; i < employees.Length; i++)
            {
                if (employees[i].m_Worker != citizen) continue;
                employeeIndex = i;
                break;
            }
            // Worker without its Employee link: the local job systems own that repair.
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
