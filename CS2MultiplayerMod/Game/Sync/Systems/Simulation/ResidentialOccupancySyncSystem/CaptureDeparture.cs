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
    // Noticing that a household or citizen the host was tracking has gone, and recording it so the
    // client retires its own copy. A departure has to be stated explicitly: an absolute roster
    // only says who is there, never who left.
    public partial class ResidentialOccupancySyncSystem
    {
        private void AddDepartureRecords(ResidentialOccupancySnapshot snapshot, PageBudget budget,
            long now)
        {
            int examined = _hostDepartureOrder.Count;
            while (examined-- > 0 &&
                   snapshot.Departures.Count < HostDeparturesPerPage)
            {
                ulong householdId;
                if (!_hostDepartureOrder.TryDequeue(out householdId)) break;
                HostDeparture departure;
                if (!_hostDepartures.TryGetValue(householdId, out departure))
                {
                    _hostDepartureOrderMembers.Remove(householdId);
                    continue;
                }
                if (departure.ExpiresMs <= now)
                {
                    _hostDepartures.Remove(householdId);
                    _hostDepartureOrderMembers.Remove(householdId);
                    continue;
                }
                _hostDepartureOrder.Enqueue(householdId);
                if (!budget.DepartureIds.Add(householdId)) continue;
                if (budget.Bytes + 17 > ResidentialOccupancySnapshot.MaxEncodedBytes)
                {
                    budget.DepartureIds.Remove(householdId);
                    break;
                }
                snapshot.Departures.Add(new OccupancyDeparture
                {
                    HouseholdId = householdId,
                    Revision = departure.Revision,
                    Unhoused = departure.Unhoused,
                });
                budget.Bytes += 17;
            }

            examined = _hostCitizenDepartureOrder.Count;
            while (examined-- > 0 &&
                   snapshot.CitizenDepartures.Count < HostCitizenDeparturesPerPage)
            {
                ulong citizenId;
                if (!_hostCitizenDepartureOrder.TryDequeue(out citizenId)) break;
                HostDeparture departure;
                if (!_hostCitizenDepartures.TryGetValue(citizenId, out departure))
                {
                    _hostCitizenDepartureOrderMembers.Remove(citizenId);
                    continue;
                }
                if (departure.ExpiresMs <= now)
                {
                    _hostCitizenDepartures.Remove(citizenId);
                    _hostCitizenDepartureOrderMembers.Remove(citizenId);
                    continue;
                }
                _hostCitizenDepartureOrder.Enqueue(citizenId);
                if (!budget.CitizenDepartureIds.Add(citizenId)) continue;
                if (budget.Bytes + 16 > ResidentialOccupancySnapshot.MaxEncodedBytes)
                {
                    budget.CitizenDepartureIds.Remove(citizenId);
                    break;
                }
                snapshot.CitizenDepartures.Add(new OccupancyCitizenDeparture
                {
                    CitizenId = citizenId,
                    Revision = departure.Revision,
                });
                budget.Bytes += 16;
            }
        }

        private void RecordHostDeparture(ulong householdId, ulong revision, long now,
            bool unhoused)
        {
            HostDeparture existing;
            if (_hostDepartures.TryGetValue(householdId, out existing))
            {
                if (revision > existing.Revision)
                {
                    existing.Revision = revision;
                    existing.Unhoused = unhoused;
                }
                else if (!unhoused)
                {
                    // An explicit move-away/destroyed observation outranks an earlier release.
                    existing.Unhoused = false;
                }
                existing.ExpiresMs = now + DepartureRetentionMs;
                return;
            }
            while (_hostDepartures.Count >= MaxTrackedDepartures &&
                   _hostDepartureOrder.TryDequeue(out ulong oldest))
            {
                _hostDepartureOrderMembers.Remove(oldest);
                _hostDepartures.Remove(oldest);
            }
            if (_hostDepartures.Count >= MaxTrackedDepartures) return;
            _hostDepartures[householdId] = new HostDeparture
            {
                Revision = revision,
                ExpiresMs = now + DepartureRetentionMs,
                Unhoused = unhoused,
            };
            if (_hostDepartureOrderMembers.Add(householdId))
                _hostDepartureOrder.Enqueue(householdId);
        }

        private void RecordHostCitizenDeparture(ulong citizenId, ulong revision, long now)
        {
            if (citizenId == 0 || revision == 0) return;
            HostDeparture existing;
            if (_hostCitizenDepartures.TryGetValue(citizenId, out existing))
            {
                if (revision > existing.Revision) existing.Revision = revision;
                existing.ExpiresMs = now + DepartureRetentionMs;
                return;
            }
            while (_hostCitizenDepartures.Count >= MaxTrackedDepartures &&
                   _hostCitizenDepartureOrder.TryDequeue(out ulong oldest))
            {
                _hostCitizenDepartureOrderMembers.Remove(oldest);
                _hostCitizenDepartures.Remove(oldest);
            }
            if (_hostCitizenDepartures.Count >= MaxTrackedDepartures) return;
            _hostCitizenDepartures[citizenId] = new HostDeparture
            {
                Revision = revision,
                ExpiresMs = now + DepartureRetentionMs,
            };
            if (_hostCitizenDepartureOrderMembers.Add(citizenId))
                _hostCitizenDepartureOrder.Enqueue(citizenId);
        }

        /// <summary>
        /// Moving-away households are few and may lose their renter link before their property's
        /// rotating change bucket is sampled. Scan that explicit lifecycle component directly and
        /// retain its tombstone across many outbound pages.
        /// </summary>
        private void ScanHostDepartures(long now)
        {
            if (_departingHouseholds.IsEmptyIgnoreFilter) return;
            NativeArray<Entity> households = default(NativeArray<Entity>);
            try
            {
                households = _departingHouseholds.ToEntityArray(Allocator.Temp);
                for (int i = 0; i < households.Length; i++)
                {
                    Entity household = households[i];
                    ulong householdId = PackHostEntityId(household);
                    if (householdId == 0) continue;
                    HostDeparture known;
                    bool tracked = _hostDepartures.TryGetValue(householdId, out known) &&
                                   !known.Unhoused;

                    // The native move-away executor runs on a wider interval than this
                    // every-frame boundary, so the same family sits in this query for a stretch of
                    // frames. Once its members are tombstoned there is nothing left to harvest and
                    // only the retention window still needs pushing forward; a member the family
                    // gains afterwards is caught by the tracked-citizen scan.
                    if (tracked && !_hostHouseholds.ContainsKey(householdId) &&
                        !_hostHouseholdCitizens.ContainsKey(householdId))
                    {
                        RecordHostDeparture(householdId, known.Revision, now, false);
                        continue;
                    }

                    ulong revision = tracked ? known.Revision : NextHostRevision();
                    RecordHostDeparture(householdId, revision, now, false);
                    RecordHostCitizensForDepartingHousehold(household, householdId,
                        revision, now, true);
                    _hostHouseholds.Remove(householdId);
                }
            }
            finally
            {
                if (households.IsCreated) households.Dispose();
            }
        }

        private void ScanTrackedHostHouseholds(long now)
        {
            int examined = System.Math.Min(MaxTrackedHouseholdChecksPerUpdate,
                _hostHouseholdOrder.Count);
            while (examined-- > 0 && _hostHouseholdOrder.TryDequeue(out ulong householdId))
            {
                Entity household;
                if (!_hostHouseholds.TryGetValue(householdId, out household))
                {
                    _hostHouseholdOrderMembers.Remove(householdId);
                    continue;
                }
                if (household != Entity.Null && EntityManager.Exists(household) &&
                    EntityManager.HasComponent<Household>(household) &&
                    !EntityManager.HasComponent<Deleted>(household))
                {
                    bool housed = HasCompleteHostRenterLink(household);
                    if (!housed)
                    {
                        HostDeparture known;
                        ulong releaseRevision =
                            _hostDepartures.TryGetValue(householdId, out known)
                                ? known.Revision : NextHostRevision();
                        RecordHostDeparture(householdId, releaseRevision, now, true);
                    }
                    _hostHouseholdOrder.Enqueue(householdId);
                    continue;
                }

                ulong revision = NextHostRevision();
                RecordHostDeparture(householdId, revision, now, false);
                RecordHostCitizensForDepartingHousehold(Entity.Null, householdId, revision, now,
                    false);
                _hostHouseholds.Remove(householdId);
                _hostHouseholdOrderMembers.Remove(householdId);
            }
        }

        private bool HasCompleteHostRenterLink(Entity household)
        {
            if (!EntityManager.HasComponent<PropertyRenter>(household)) return false;
            Entity property = EntityManager.GetComponentData<PropertyRenter>(household).m_Property;
            if (!IsLiveProperty(property)) return false;
            DynamicBuffer<Renter> renters = EntityManager.GetBuffer<Renter>(property, true);
            for (int i = 0; i < renters.Length; i++)
                if (renters[i].m_Renter == household) return true;
            return false;
        }

        private void ObserveHostHouseholdEntity(Entity household, OccupancyHousehold captured,
            ulong revision)
        {
            ulong householdId = captured.HouseholdId;
            if (captured.Departing)
            {
                _hostHouseholds.Remove(householdId);
                return;
            }
            if (_hostHouseholdOrderMembers.Add(householdId))
                _hostHouseholdOrder.Enqueue(householdId);
            _hostHouseholds[householdId] = household;

            HostDeparture departure;
            if (!captured.Departing &&
                _hostDepartures.TryGetValue(householdId, out departure) &&
                revision > departure.Revision)
                _hostDepartures.Remove(householdId);
        }

        /// <summary>
        /// A person can disappear from a surviving household without the household itself moving
        /// away. Retain the last successfully captured local entity for each host id and inspect a
        /// bounded slice every update, so even a short-lived Deleted tag becomes an eventual exact
        /// tombstone after the entity handle ceases to exist.
        /// </summary>
        private void ScanTrackedHostCitizens(long now)
        {
            int examined = System.Math.Min(MaxTrackedCitizenChecksPerUpdate,
                _hostCitizenOrder.Count);
            while (examined-- > 0 && _hostCitizenOrder.TryDequeue(out ulong citizenId))
            {
                HostCitizenObservation observed;
                if (!_hostCitizens.TryGetValue(citizenId, out observed))
                {
                    _hostCitizenOrderMembers.Remove(citizenId);
                    continue;
                }
                Entity citizen = observed.Entity;
                if (citizen != Entity.Null && EntityManager.Exists(citizen) &&
                    EntityManager.HasComponent<Citizen>(citizen) &&
                    !EntityManager.HasComponent<Deleted>(citizen))
                {
                    _hostCitizenOrder.Enqueue(citizenId);
                    continue;
                }

                RecordHostCitizenDeparture(citizenId, NextHostRevision(), now);
                _hostCitizens.Remove(citizenId);
                _hostCitizenOrderMembers.Remove(citizenId);
            }
        }

        private void ObserveHostHouseholdCitizenRoster(Entity household,
            OccupancyHousehold captured, ulong revision, long now)
        {
            ulong householdId = captured.HouseholdId;
            ulong[] previous;
            if (_hostHouseholdCitizens.TryGetValue(householdId, out previous))
            {
                for (int i = 0; i < previous.Length; i++)
                {
                    ulong previousId = previous[i];
                    bool stillHere = false;
                    for (int j = 0; j < captured.Citizens.Length; j++)
                    {
                        if (captured.Citizens[j].CitizenId != previousId) continue;
                        stillHere = true;
                        break;
                    }
                    if (stillHere) continue;

                    HostCitizenObservation observed;
                    if (!_hostCitizens.TryGetValue(previousId, out observed)) continue;
                    Entity citizen = observed.Entity;
                    if (citizen != Entity.Null && EntityManager.Exists(citizen) &&
                        EntityManager.HasComponent<Citizen>(citizen) &&
                        !EntityManager.HasComponent<Deleted>(citizen))
                    {
                        // A live person absent here may be in a household split whose destination
                        // page has not been captured yet. Do not infer a departure from absence.
                        continue;
                    }
                    RecordHostCitizenDeparture(previousId, revision, now);
                    _hostCitizens.Remove(previousId);
                }
            }

            DynamicBuffer<HouseholdCitizen> members =
                EntityManager.GetBuffer<HouseholdCitizen>(household, true);
            // A family's roster is re-observed on every pass and almost never differs. Writing back
            // into the array already stored for this household keeps the common case free of an
            // allocation; a household is only re-keyed when its member count actually changed.
            int count = captured.Citizens.Length;
            bool reuse = previous != null && previous.Length == count;
            ulong[] current = reuse ? previous : new ulong[count];
            for (int i = 0; i < count; i++)
            {
                ulong citizenId = captured.Citizens[i].CitizenId;
                current[i] = citizenId;
                HostCitizenObservation observed;
                if (!_hostCitizens.TryGetValue(citizenId, out observed) &&
                    _hostCitizenOrderMembers.Add(citizenId))
                    _hostCitizenOrder.Enqueue(citizenId);
                observed.Entity = members[i].m_Citizen;
                observed.HouseholdId = householdId;
                _hostCitizens[citizenId] = observed;

                HostDeparture departure;
                if (_hostCitizenDepartures.TryGetValue(citizenId, out departure) &&
                    revision > departure.Revision)
                    _hostCitizenDepartures.Remove(citizenId);
            }
            if (!reuse) _hostHouseholdCitizens[householdId] = current;
        }

        private void RecordHostCitizensForDepartingHousehold(Entity household, ulong householdId,
            ulong revision, long now, bool explicitHouseholdDeparture)
        {
            if (household != Entity.Null && EntityManager.Exists(household) &&
                EntityManager.HasBuffer<HouseholdCitizen>(household))
            {
                DynamicBuffer<HouseholdCitizen> members =
                    EntityManager.GetBuffer<HouseholdCitizen>(household, true);
                for (int i = 0; i < members.Length; i++)
                {
                    Entity citizen = members[i].m_Citizen;
                    if (citizen == Entity.Null || !EntityManager.Exists(citizen) ||
                        !EntityManager.HasComponent<HouseholdMember>(citizen) ||
                        EntityManager.GetComponentData<HouseholdMember>(citizen).m_Household !=
                        household) continue;
                    ulong citizenId = PackHostEntityId(citizen);
                    if (citizenId == 0) continue;
                    RecordHostCitizenDeparture(citizenId, revision, now);
                    _hostCitizens.Remove(citizenId);
                }
            }

            ulong[] previous;
            if (!_hostHouseholdCitizens.TryGetValue(householdId, out previous)) return;
            for (int i = 0; i < previous.Length; i++)
            {
                HostCitizenObservation observed;
                if (_hostCitizens.TryGetValue(previous[i], out observed))
                {
                    bool stillLive = observed.Entity != Entity.Null &&
                                     EntityManager.Exists(observed.Entity) &&
                                     EntityManager.HasComponent<Citizen>(observed.Entity) &&
                                     !EntityManager.HasComponent<Deleted>(observed.Entity);
                    // A vanished shell is not proof that its live residents left the city: a
                    // household split may have moved them before the destination was captured.
                    // Consult the live reverse link, not the last captured household id: that
                    // observation is intentionally stale until the destination property appears.
                    if (stillLive)
                    {
                        bool stillBelongsToDepartingHousehold = household != Entity.Null &&
                            EntityManager.Exists(household) &&
                            EntityManager.HasComponent<HouseholdMember>(observed.Entity) &&
                            EntityManager.GetComponentData<HouseholdMember>(observed.Entity)
                                .m_Household == household;
                        if (!explicitHouseholdDeparture || !stillBelongsToDepartingHousehold)
                            continue;
                    }
                }
                RecordHostCitizenDeparture(previous[i], revision, now);
                _hostCitizens.Remove(previous[i]);
            }
            _hostHouseholdCitizens.Remove(householdId);
        }
    }
}
