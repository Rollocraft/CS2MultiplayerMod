using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.Infrastructure;
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
    public partial class ResidentialOccupancySyncSystem
    {
        private static readonly int[] EmptyNameIndices = new int[0];
        private static readonly string[] EmptyVehiclePrefabs = new string[0];
        private readonly HashSet<string> _pageEntryNames = new HashSet<string>();

        private enum PageAddResult
        {
            Added,
            Duplicate,
            Full,
            Invalid,
        }

        private sealed class PageBudget
        {
            public readonly HashSet<PropertyRentIdentity> Identities =
                new HashSet<PropertyRentIdentity>();
            public readonly HashSet<ulong> HouseholdIds = new HashSet<ulong>();
            public readonly HashSet<ulong> CitizenIds = new HashSet<ulong>();
            public readonly HashSet<ulong> DepartureIds = new HashSet<ulong>();
            public readonly HashSet<ulong> CitizenDepartureIds = new HashSet<ulong>();
            public readonly HashSet<string> Names = new HashSet<string>();
            public int Bytes = 24;
            public int Households;
            public int Citizens;
            public int Pets;
            public int Vehicles;
        }

        /// <summary>Called once per city-state snapshot on the host.</summary>
        internal bool Capture(NetworkWriter writer)
        {
            if (writer == null) return false;
            MultiplayerService service = Mod.Service;
            if (service == null || !service.GameplaySyncReady ||
                service.Session.Role != Core.Session.SessionRole.Host) return false;

            if (_hostSweepEntities == null && !BeginHostSweep()) return WriteEmptySweep(writer);
            if (_captureCursor < 0 || _captureCursor >= _hostSweepEntities.Length)
            {
                _hostSweepEntities = null;
                _captureCursor = 0;
                AdvanceHostSweep();
                if (!BeginHostSweep()) return WriteEmptySweep(writer);
            }

            var snapshot = new ResidentialOccupancySnapshot
            {
                SweepId = _captureSweepId,
                PageIndex = _capturePageIndex,
            };
            var budget = new PageBudget();
            bool baselineNeedsEmptyPage = _captureBaselineNeedsEmptyPage;
            _captureBaselineNeedsEmptyPage = false;
            if (!baselineNeedsEmptyPage)
            {
                // Lifecycle proof comes first. A very dense property is allowed to exceed the soft
                // page target, so appending tombstones afterward could otherwise starve the only
                // authoritative evidence that an entity left.
                AddDepartureRecords(snapshot, budget, service.NowMs);
                AddPriorityProperties(snapshot, budget);
            }

            int index = _captureCursor;
            bool baselineAdvanced = false;
            while (index < _hostSweepEntities.Length &&
                   snapshot.Properties.Count < ResidentialOccupancySnapshot.MaxProperties &&
                   (budget.Bytes < PageByteBudget || !baselineAdvanced))
            {
                OccupancyProperty property;
                if (TryCaptureProperty(_hostSweepEntities[index], out property))
                {
                    PageAddResult result = TryAddPageEntry(snapshot, budget, property);
                    if (result == PageAddResult.Added)
                        TraceSentRoster(_hostSweepEntities[index], property);
                    if (result == PageAddResult.Full)
                    {
                        // Priority entries may already have used most of the hard page cap. Close
                        // that page without consuming this baseline entity; the following page
                        // starts empty and can always carry a valid single property.
                        if (snapshot.Properties.Count > 0 || snapshot.Departures.Count > 0 ||
                            snapshot.CitizenDepartures.Count > 0)
                        {
                            // The following capture intentionally omits priority/lifecycle extras
                            // once, guaranteeing that any individually valid baseline property can
                            // make progress even when it nearly fills the hard transport cap.
                            _captureBaselineNeedsEmptyPage = true;
                            break;
                        }
                        _captureSkips++;
                        _captureSweepHadSkips = true;
                    }
                    else if (result == PageAddResult.Invalid)
                    {
                        _captureSkips++;
                        _captureSweepHadSkips = true;
                    }
                }
                else
                {
                    _captureSkips++;
                    _captureSweepHadSkips = true;
                }
                index++;
                baselineAdvanced = true;
            }

            bool cappedSweep = _capturePageIndex + 1 >= ResidentialOccupancySnapshot.MaxPagesPerSweep;
            snapshot.EndOfSweep = index >= _hostSweepEntities.Length || cappedSweep;
            snapshot.SweepComplete = snapshot.EndOfSweep && index >= _hostSweepEntities.Length &&
                                     !_captureSweepHadSkips;
            snapshot.RevisionWatermark = LastHostRevision();
            if (snapshot.Properties.Count == 0 && snapshot.Departures.Count == 0 &&
                snapshot.CitizenDepartures.Count == 0 && !snapshot.EndOfSweep) return false;

            // Encode before committing traversal state. Future schema changes can then fail this
            // one channel safely without consuming a baseline suffix that was never sent.
            byte[] encoded = snapshot.Encode();
            if (snapshot.EndOfSweep)
            {
                _hostSweepEntities = null;
                _captureCursor = 0;
                AdvanceHostSweep();
            }
            else
            {
                _captureCursor = index;
                _capturePageIndex++;
            }

            int before = writer.Length;
            writer.WriteBytes(encoded, 0, encoded.Length);
            _sentBytes += writer.Length - before;
            _sentPages++;
            _sentProperties += snapshot.Properties.Count;
            return true;
        }

        private bool BeginHostSweep()
        {
            NativeArray<Entity> properties = _properties.ToEntityArray(Allocator.Temp);
            try
            {
                if (properties.Length == 0) return false;
                _hostSweepEntities = new Entity[properties.Length];
                for (int i = 0; i < properties.Length; i++) _hostSweepEntities[i] = properties[i];
                return true;
            }
            finally { properties.Dispose(); }
        }

        /// <summary>
        /// A city with no residential property still has to close its sweep, otherwise a client
        /// that bulldozed its last house would keep the previous roster cached forever.
        /// </summary>
        private bool WriteEmptySweep(NetworkWriter writer)
        {
            var empty = new ResidentialOccupancySnapshot
            {
                SweepId = _captureSweepId,
                PageIndex = 0,
                EndOfSweep = true,
                SweepComplete = true,
            };
            var budget = new PageBudget();
            MultiplayerService service = Mod.Service;
            AddDepartureRecords(empty, budget, service != null ? service.NowMs : 0);
            empty.RevisionWatermark = LastHostRevision();
            int before = writer.Length;
            empty.Write(writer);
            _sentBytes += writer.Length - before;
            _sentPages++;
            AdvanceHostSweep();
            return true;
        }

        private void AddPriorityProperties(ResidentialOccupancySnapshot snapshot, PageBudget budget)
        {
            int added = 0;
            while (added < PriorityPropertiesPerPage && _priorityOrder.Count > 0 &&
                   snapshot.Properties.Count < ResidentialOccupancySnapshot.MaxProperties &&
                   budget.Bytes < PriorityByteBudget)
            {
                PropertyRentIdentity identity;
                if (!_priorityOrder.TryDequeue(out identity)) break;
                Entity entity;
                if (!_priority.TryGetValue(identity, out entity)) continue;
                OccupancyProperty property;
                // Recapture at send time. The queued signal says only "this property changed";
                // retaining the old payload could let a later baseline lose to a stale priority
                // copy of the same identity in this page.
                if (!TryCaptureProperty(entity, out property))
                {
                    _priority.Remove(identity);
                    continue;
                }
                PageAddResult result = TryAddPageEntry(snapshot, budget, property);
                if (result == PageAddResult.Full)
                {
                    // Keep the signal for the next empty page instead of silently consuming it.
                    _priorityOrder.Enqueue(identity);
                    break;
                }
                _priority.Remove(identity);
                if (result == PageAddResult.Added)
                {
                    TraceSentRoster(entity, property);
                    added++;
                }
            }
        }

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
                    if (householdId != 0)
                    {
                        HostDeparture known;
                        ulong revision = _hostDepartures.TryGetValue(householdId, out known) &&
                                         !known.Unhoused
                            ? known.Revision : NextHostRevision();
                        RecordHostDeparture(householdId, revision, now, false);
                        RecordHostCitizensForDepartingHousehold(household, householdId,
                            revision, now, true);
                        _hostHouseholds.Remove(householdId);
                    }
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

            var current = new ulong[captured.Citizens.Length];
            DynamicBuffer<HouseholdCitizen> members =
                EntityManager.GetBuffer<HouseholdCitizen>(household, true);
            for (int i = 0; i < captured.Citizens.Length; i++)
            {
                ulong citizenId = captured.Citizens[i].CitizenId;
                current[i] = citizenId;
                HostCitizenObservation observed;
                if (!_hostCitizens.TryGetValue(citizenId, out observed))
                {
                    observed = new HostCitizenObservation();
                    _hostCitizens[citizenId] = observed;
                    if (_hostCitizenOrderMembers.Add(citizenId))
                        _hostCitizenOrder.Enqueue(citizenId);
                }
                observed.Entity = members[i].m_Citizen;
                observed.HouseholdId = householdId;

                HostDeparture departure;
                if (_hostCitizenDepartures.TryGetValue(citizenId, out departure) &&
                    revision > departure.Revision)
                    _hostCitizenDepartures.Remove(citizenId);
            }
            _hostHouseholdCitizens[householdId] = current;
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

        private PageAddResult TryAddPageEntry(ResidentialOccupancySnapshot snapshot,
            PageBudget budget, OccupancyProperty property)
        {
            if (budget.Identities.Contains(property.Identity)) return PageAddResult.Duplicate;
            if (!ResidentialOccupancySnapshot.IsValidProperty(property))
                return PageAddResult.Invalid;

            int households = property.Households.Length;
            int citizens = 0;
            int pets = 0;
            int vehicles = 0;
            int bytes = 24;
            _pageEntryNames.Clear();
            _pageEntryNames.Add(property.PrefabName);
            for (int h = 0; h < property.Households.Length; h++)
            {
                OccupancyHousehold household = property.Households[h];
                if (budget.HouseholdIds.Contains(household.HouseholdId))
                    return PageAddResult.Invalid;
                citizens += household.Citizens.Length;
                pets += household.Pets.Length;
                vehicles += household.OwnedVehicles.Length;
                bytes += 50 + household.NameIndices.Length * 4 +
                         (household.Pets.Length + household.OwnedVehicles.Length) * 2;
                _pageEntryNames.Add(household.PrefabName);
                for (int c = 0; c < household.Citizens.Length; c++)
                {
                    OccupancyCitizen citizen = household.Citizens[c];
                    if (budget.CitizenIds.Contains(citizen.CitizenId))
                        return PageAddResult.Invalid;
                    bytes += 24 + citizen.NameIndices.Length * 4;
                    _pageEntryNames.Add(citizen.PrefabName);
                }
                for (int p = 0; p < household.Pets.Length; p++)
                    _pageEntryNames.Add(household.Pets[p]);
                for (int v = 0; v < household.OwnedVehicles.Length; v++)
                    _pageEntryNames.Add(household.OwnedVehicles[v]);
            }

            if (budget.Households + households > ResidentialOccupancySnapshot.MaxHouseholdsPerPage ||
                budget.Citizens + citizens > ResidentialOccupancySnapshot.MaxCitizensPerPage ||
                budget.Pets + pets > ResidentialOccupancySnapshot.MaxPetsPerPage ||
                budget.Vehicles + vehicles > ResidentialOccupancySnapshot.MaxVehiclesPerPage)
                return PageAddResult.Full;

            int newNameCount = 0;
            foreach (string name in _pageEntryNames)
            {
                if (budget.Names.Contains(name)) continue;
                newNameCount++;
                bytes += 4 + Encoding.UTF8.GetByteCount(name);
            }
            if (budget.Names.Count + newNameCount > ResidentialOccupancySnapshot.MaxNames ||
                budget.Bytes + bytes > ResidentialOccupancySnapshot.MaxEncodedBytes)
                return PageAddResult.Full;

            budget.Identities.Add(property.Identity);
            budget.Households += households;
            budget.Citizens += citizens;
            budget.Pets += pets;
            budget.Vehicles += vehicles;
            budget.Bytes += bytes;
            foreach (string name in _pageEntryNames) budget.Names.Add(name);
            for (int h = 0; h < property.Households.Length; h++)
            {
                OccupancyHousehold household = property.Households[h];
                budget.HouseholdIds.Add(household.HouseholdId);
                for (int c = 0; c < household.Citizens.Length; c++)
                    budget.CitizenIds.Add(household.Citizens[c].CitizenId);
            }
            snapshot.Properties.Add(property);
            return PageAddResult.Added;
        }

        /// <summary>
        /// One residential partition per update. This is the change detector that gets a newly
        /// occupied building onto the wire in seconds rather than waiting for the rolling baseline
        /// sweep to come round to it.
        /// </summary>
        private void ScanHostChanges(int bucket)
        {
            _properties.SetSharedComponentFilter(new UpdateFrame((uint)bucket));
            NativeArray<Entity> properties = default(NativeArray<Entity>);
            try
            {
                properties = _properties.ToEntityArray(Allocator.Temp);
                bool initialized = _hostBucketInitialized[bucket];
                for (int i = 0; i < properties.Length; i++)
                {
                    Entity property = properties[i];
                    OccupancyProperty captured;
                    if (!TryCaptureProperty(property, out captured)) continue;
                    int hash = Hash(captured);
                    HostObserved observed;
                    if (!_hostObserved.TryGetValue(property, out observed))
                    {
                        _hostObserved[property] = new HostObserved { Hash = hash, Bucket = bucket };
                        _hostObservedBuckets[bucket].Add(property);
                        if (initialized) Prioritize(property, captured);
                    }
                    else if (observed.Hash != hash)
                    {
                        observed.Hash = hash;
                        if (initialized) Prioritize(property, captured);
                    }
                }
                _hostBucketInitialized[bucket] = true;
            }
            finally
            {
                if (properties.IsCreated) properties.Dispose();
                _properties.ResetFilter();
            }
            PruneHostObservedBucket(bucket);
        }

        /// <summary>
        /// Fast path for native renter transactions. This is called every simulation frame after
        /// PropertyProcessingSystem, while its RentersUpdated event still names the exact property
        /// whose absolute roster changed.
        /// </summary>
        internal void CaptureRenterChanges()
        {
            MultiplayerService service = Mod.Service;
            if (service == null || !service.GameplaySyncReady ||
                service.Session.Role != Core.Session.SessionRole.Host ||
                _renterUpdates.IsEmptyIgnoreFilter) return;

            NativeArray<RentersUpdated> updates = default(NativeArray<RentersUpdated>);
            try
            {
                updates = _renterUpdates.ToComponentDataArray<RentersUpdated>(Allocator.Temp);
                for (int i = 0; i < updates.Length; i++)
                {
                    Entity property = updates[i].m_Property;
                    OccupancyProperty captured;
                    if (!TryCaptureProperty(property, out captured)) continue;

                    int bucket = (int)(EntityManager.GetSharedComponent<UpdateFrame>(property)
                        .m_Index % UpdatePartitions);
                    int hash = Hash(captured);
                    HostObserved observed;
                    if (!_hostObserved.TryGetValue(property, out observed))
                    {
                        _hostObserved[property] = new HostObserved { Hash = hash, Bucket = bucket };
                        _hostObservedBuckets[bucket].Add(property);
                    }
                    else
                    {
                        observed.Hash = hash;
                    }

                    // Always queue the event, even if the rolling observer happened to sample the
                    // same hash. The event is authoritative proof of a completed renter mutation.
                    Prioritize(property, captured);
                }
            }
            finally
            {
                if (updates.IsCreated) updates.Dispose();
            }
        }

        private void Prioritize(Entity entity, OccupancyProperty property)
        {
            PropertyRentIdentity identity = property.Identity;
            if (_priority.ContainsKey(identity))
            {
                _priority[identity] = entity;
                return;
            }
            while (_priority.Count >= MaxPriorityProperties && _priorityOrder.Count > 0)
            {
                PropertyRentIdentity oldest;
                if (!_priorityOrder.TryDequeue(out oldest)) break;
                if (_priority.Remove(oldest)) _priorityDrops++;
            }
            if (_priority.Count >= MaxPriorityProperties)
            {
                _priorityDrops++;
                return;
            }
            _priority[identity] = entity;
            _priorityOrder.Enqueue(identity);
            _priorityChanges++;
        }

        private void PruneHostObservedBucket(int bucket)
        {
            List<Entity> entities = _hostObservedBuckets[bucket];
            int write = 0;
            for (int i = 0; i < entities.Count; i++)
            {
                Entity entity = entities[i];
                HostObserved observed;
                if (!_hostObserved.TryGetValue(entity, out observed)) continue;
                if (!IsLiveProperty(entity) || observed.Bucket != bucket ||
                    EntityManager.GetSharedComponent<UpdateFrame>(entity).m_Index != (uint)bucket)
                {
                    _hostObserved.Remove(entity);
                    continue;
                }
                entities[write++] = entity;
            }
            if (write < entities.Count) entities.RemoveRange(write, entities.Count - write);
        }

        private bool TryCaptureProperty(Entity property, out OccupancyProperty result)
        {
            result = default(OccupancyProperty);
            if (!IsLiveProperty(property)) return false;
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(property).m_Prefab;
            if (prefab == Entity.Null || !EntityManager.Exists(prefab) ||
                !EntityManager.HasComponent<BuildingPropertyData>(prefab)) return false;
            string prefabName = PrefabIndex.SafeName(_prefabSystem, prefab);
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
            string prefabName = PrefabIndex.SafeName(_prefabSystem, prefab);
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
                    string petName = PrefabIndex.SafeName(_prefabSystem,
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

                string name = PrefabIndex.SafeName(_prefabSystem,
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
            string name = PrefabIndex.SafeName(_prefabSystem,
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

        /// <summary>
        /// Content hash of structural identity plus prompt UI changes. Money, savings, health and
        /// wellbeing drift continuously and belong to the rolling baseline; putting them here can
        /// fill the priority queue with every household and starve actual move-ins and move-outs.
        /// </summary>
        private static int Hash(OccupancyProperty property)
        {
            unchecked
            {
                int hash = (int)2166136261;
                // Construction is in the hash because its end is the change a client most needs to
                // hear about promptly; the rate itself only ever changes once, at creation.
                hash = (hash ^ property.ConstructionSpeed) * 16777619;
                hash = (hash ^ property.Households.Length) * 16777619;
                for (int h = 0; h < property.Households.Length; h++)
                {
                    OccupancyHousehold household = property.Households[h];
                    hash = HashId(hash, household.HouseholdId);
                    hash = (hash ^ household.PrefabName.GetHashCode()) * 16777619;
                    hash = (hash ^ household.Flags) * 16777619;
                    hash = (hash ^ (household.Departing ? 1 : 0)) * 16777619;
                    hash = (hash ^ household.Rent) * 16777619;
                    hash = (hash ^ household.SalaryLastDay) * 16777619;
                    hash = HashIndices(hash, household.NameIndices);
                    hash = (hash ^ household.Citizens.Length) * 16777619;
                    for (int c = 0; c < household.Citizens.Length; c++)
                    {
                        OccupancyCitizen citizen = household.Citizens[c];
                        hash = HashId(hash, citizen.CitizenId);
                        hash = (hash ^ citizen.PrefabName.GetHashCode()) * 16777619;
                        hash = (hash ^ citizen.State) * 16777619;
                        hash = (hash ^ citizen.PseudoRandom) * 16777619;
                        hash = (hash ^ citizen.BirthDay) * 16777619;
                        hash = (hash ^ citizen.Employment) * 16777619;
                        hash = HashIndices(hash, citizen.NameIndices);
                    }
                    hash = (hash ^ household.Pets.Length) * 16777619;
                    for (int p = 0; p < household.Pets.Length; p++)
                        hash = (hash ^ household.Pets[p].GetHashCode()) * 16777619;
                    hash = (hash ^ household.OwnedVehicles.Length) * 16777619;
                    for (int v = 0; v < household.OwnedVehicles.Length; v++)
                        hash = (hash ^ household.OwnedVehicles[v].GetHashCode()) * 16777619;
                }
                return hash;
            }
        }

        private static int HashId(int hash, ulong id)
        {
            unchecked
            {
                hash = (hash ^ (int)id) * 16777619;
                return (hash ^ (int)(id >> 32)) * 16777619;
            }
        }

        private static int HashIndices(int hash, int[] indices)
        {
            unchecked
            {
                hash = (hash ^ indices.Length) * 16777619;
                for (int i = 0; i < indices.Length; i++) hash = (hash ^ indices[i]) * 16777619;
                return hash;
            }
        }

        [Conditional(DevTrace.Symbol)]
        private void TraceSentRoster(Entity propertyEntity, OccupancyProperty property)
        {
            int hash = TraceRosterHash(property);
            int previous;
            if (_traceSentRosterHashes.TryGetValue(propertyEntity, out previous) &&
                previous == hash) return;
            bool first = !_traceSentRosterHashes.ContainsKey(propertyEntity);
            _traceSentRosterHashes[propertyEntity] = hash;
            if (first && property.Households.Length == 0) return;
            LogRosterTrace("SENT", property);
        }

        [Conditional(DevTrace.Symbol)]
        private void TraceReceivedRoster(OccupancyProperty property)
        {
            int hash = TraceRosterHash(property);
            int previous;
            if (_traceReceivedRosterHashes.TryGetValue(property.Identity, out previous) &&
                previous == hash) return;
            bool first = !_traceReceivedRosterHashes.ContainsKey(property.Identity);
            _traceReceivedRosterHashes[property.Identity] = hash;
            if (first && property.Households.Length == 0) return;
            LogRosterTrace("RECEIVED", property);
        }

        private static int TraceRosterHash(OccupancyProperty property)
        {
            unchecked
            {
                int hash = property.Households != null ? property.Households.Length : 0;
                if (property.Households == null) return hash;
                for (int h = 0; h < property.Households.Length; h++)
                {
                    OccupancyHousehold household = property.Households[h];
                    hash = HashId(hash, household.HouseholdId);
                    hash = hash * 397 ^ (household.Departing ? 1 : 0);
                    hash = hash * 397 ^ household.Rent;
                    hash = hash * 397 ^ household.SalaryLastDay;
                    hash = hash * 397 ^ (int)household.ShoppedValuePerDay;
                    hash = hash * 397 ^ household.MoneySpentOnBuildingLevelingLastDay;
                    hash = hash * 397 + (household.Citizens != null
                        ? household.Citizens.Length : 0);
                    if (household.Citizens != null)
                        for (int c = 0; c < household.Citizens.Length; c++)
                            hash = HashId(hash, household.Citizens[c].CitizenId);
                    hash = hash * 397 + (household.OwnedVehicles != null
                        ? household.OwnedVehicles.Length : 0);
                    if (household.OwnedVehicles != null)
                        for (int v = 0; v < household.OwnedVehicles.Length; v++)
                            hash = hash * 397 ^ household.OwnedVehicles[v].GetHashCode();
                }
                return hash;
            }
        }

        private static void LogRosterTrace(string stage, OccupancyProperty property)
        {
            var roster = new StringBuilder();
            for (int i = 0; i < property.Households.Length; i++)
            {
                if (i != 0) roster.Append(", ");
                OccupancyHousehold household = property.Households[i];
                roster.Append("0x").Append(household.HouseholdId.ToString("X16"))
                    .Append("/").Append(household.Citizens != null
                        ? household.Citizens.Length : 0).Append(" people/")
                    .Append(household.OwnedVehicles != null
                        ? household.OwnedVehicles.Length : 0).Append(" vehicles")
                    .Append("/rent=").Append(household.Rent)
                    .Append("/income=").Append(household.SalaryLastDay)
                    .Append("/upkeep=")
                    .Append(Math.Abs((long)household.MoneySpentOnBuildingLevelingLastDay))
                    .Append("/resourceCost=").Append(household.ShoppedValuePerDay)
                    .Append("/savings=").Append(household.Savings)
                    .Append("/money=").Append(household.Money);
                if (household.Departing) roster.Append("/departing");
            }
            Mod.log.Info("[MP][OCC-DEV] " + stage + " house='" + property.PrefabName +
                         "' anchor=(" + property.AnchorX.ToString("F2") + ", " +
                         property.AnchorY.ToString("F2") + ", " +
                         property.AnchorZ.ToString("F2") + ") rev=" + property.Revision +
                         " families=" + property.Households.Length + " roster=[" + roster + "].");
        }

        private static int Clamp(int value, int min, int max) =>
            value < min ? min : value > max ? max : value;
    }
}
