using System;
using System.Collections.Generic;
using System.Diagnostics;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Session;
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
    // Finishing a move-in. A household created for a property is not housed until the game's
    // own rent action completes, so the ones in flight are tracked, counted once realized, and
    // cleaned up if the move never happened.
    public partial class ResidentialOccupancySyncSystem
    {
        /// <summary>
        /// Hand the move-in to the game's own renter pipeline instead of writing the link by hand.
        /// It is the code that clears the old property, adds PropertyRenter, appends to the renter
        /// list, drops HomelessHousehold and raises RentersUpdated — all in the order the rest of
        /// the simulation expects.
        /// </summary>
        private bool CanEnqueueRentAction() =>
            _propertyProcessing != null && _propertyProcessing.Enabled;

        private void TrackPendingMoveIn(OccupancyHousehold wanted, Entity household,
            Entity property, ulong revision, bool createdLocally)
        {
            PendingMoveIn pending;
            if (_pendingMoveIns.TryGetValue(wanted.HouseholdId, out pending))
            {
                if (revision < pending.Revision) return;
                pending.Household = household;
                pending.Property = property;
                pending.Rent = wanted.Rent;
                pending.Revision = revision;
                pending.CreatedLocally |= createdLocally;
                return;
            }

            while (_pendingMoveIns.Count >= MaxPendingMoveIns &&
                   _pendingMoveInOrder.TryDequeue(out ulong oldest))
                _pendingMoveIns.Remove(oldest);
            if (_pendingMoveIns.Count >= MaxPendingMoveIns) return;
            _pendingMoveIns[wanted.HouseholdId] = new PendingMoveIn
            {
                HouseholdId = wanted.HouseholdId,
                Household = household,
                Property = property,
                Rent = wanted.Rent,
                Revision = revision,
                CreatedLocally = createdLocally,
            };
            _pendingMoveInOrder.Enqueue(wanted.HouseholdId);
        }

        /// <summary>
        /// Runs immediately after PropertyProcessingSystem. A queued move-in is complete only when
        /// both native renter directions agree; at that point its host rent is installed before the
        /// later payment pass can consume the locally selected default.
        /// </summary>
        internal void FinalizeMoveIns()
        {
            MultiplayerService service = Mod.Service;
            if (service == null || !service.GameplaySyncReady ||
                service.Session.Role != SessionRole.Client || _pendingMoveIns.Count == 0) return;

            int remaining = math.min(MaxMoveInFinalizationsPerUpdate, _pendingMoveInOrder.Count);
            while (remaining-- > 0 && _pendingMoveInOrder.TryDequeue(out ulong householdId))
            {
                PendingMoveIn pending;
                if (!_pendingMoveIns.TryGetValue(householdId, out pending)) continue;
                Entity mapped;
                bool mappingValid = TryResolveHousehold(householdId, out mapped) &&
                                    mapped == pending.Household;
                if (!IsHouseholdDesiredHere(householdId, pending.Property) ||
                    !mappingValid || !IsLiveProperty(pending.Property))
                {
                    _pendingMoveIns.Remove(householdId);
                    PropertyRentIdentity desiredIdentity;
                    if (pending.CreatedLocally && mappingValid &&
                        !TryGetDesiredPropertyIdentity(householdId, out desiredIdentity))
                    {
                        if (!CleanupCancelledCreatedHousehold(pending.Household))
                        {
                            _pendingMoveIns[householdId] = pending;
                            _pendingMoveInOrder.Enqueue(householdId);
                        }
                    }
                    else
                    {
                        Entity destination;
                        if (TryGetDesiredProperty(householdId, out destination))
                            MarkDirty(destination);
                    }
                    continue;
                }
                if (!IsHouseholdAtProperty(pending.Household, pending.Property))
                {
                    _pendingMoveInOrder.Enqueue(householdId);
                    continue;
                }

                OccupancyHousehold newest;
                ulong newestRevision;
                if (TryGetCachedHousehold(pending.Property, householdId, out newest,
                    out newestRevision) && newestRevision >= pending.Revision)
                {
                    pending.Rent = newest.Rent;
                    pending.Revision = newestRevision;
                    CachedProperty cached;
                    if (_cache.TryGetValue(pending.Property, out cached))
                        NotePlacedHousehold(cached, newest, pending.Household);
                }
                CancelUnauthorizedDeparture(pending.Household);
                PropertyRenter rented =
                    EntityManager.GetComponentData<PropertyRenter>(pending.Household);
                if (rented.m_Rent != pending.Rent)
                {
                    rented.m_Rent = pending.Rent;
                    EntityManager.SetComponentData(pending.Household, rented);
                }
                _pendingMoveIns.Remove(householdId);
                _stagedTransfers.Remove(householdId);
                _stagedTransferCooldownUntil.Remove(householdId);
                MarkDirty(pending.Property);
                ScheduleReapply(pending.Property);
            }
        }

        /// <summary>
        /// Every person and vehicle the host listed for this family is linked here now, so it no
        /// longer counts as arriving: a car it buys later has to leave from the house rather than
        /// from the outside connection it came in by.
        /// </summary>
        private void NotePlacedHousehold(CachedProperty property, OccupancyHousehold household,
            Entity localHousehold)
        {
            int localPeople = CountRealizedCitizens(localHousehold, household);
            int wantedPeople = household.Citizens != null ? household.Citizens.Length : 0;
            if (localPeople != wantedPeople) return;
            int localVehicles = CountLiveOwnedVehicles(localHousehold);
            int wantedVehicles = household.OwnedVehicles != null
                ? household.OwnedVehicles.Length : 0;
            if (localVehicles < wantedVehicles) return;
            _arrivalSources.Remove(localHousehold);
            TracePlacedHousehold(property, household, localPeople, wantedPeople, localVehicles,
                wantedVehicles);
        }

        // Reached only once every exact citizen identity is linked, so a missing family member
        // shows up as RECEIVED-without-PLACED in a host/client log comparison.
        [Conditional(DevTrace.Symbol)]
        private void TracePlacedHousehold(CachedProperty property, OccupancyHousehold household,
            int localPeople, int wantedPeople, int localVehicles, int wantedVehicles)
        {
            PropertyRentIdentity previous;
            if (_tracePlacedHouseholds.TryGetValue(household.HouseholdId, out previous) &&
                previous.Equals(property.Identity)) return;
            _tracePlacedHouseholds[household.HouseholdId] = property.Identity;
            SyncLog.Detail(LogTopic.Residential, "PLACED house='" + property.Identity.PrefabName +
                "' anchor=(" + property.Identity.AnchorX.ToString("F2") + ", " +
                property.Identity.AnchorY.ToString("F2") + ", " +
                property.Identity.AnchorZ.ToString("F2") + ") rev=" + property.Revision +
                " family=0x" + household.HouseholdId.ToString("X16") + " people=" + localPeople +
                "/" + wantedPeople + " vehicles=" + localVehicles + "/" + wantedVehicles + ".");
        }

        private int CountRealizedCitizens(Entity household, OccupancyHousehold wanted)
        {
            if (household == Entity.Null || !EntityManager.Exists(household) ||
                !EntityManager.HasBuffer<HouseholdCitizen>(household) || wanted.Citizens == null)
                return 0;
            DynamicBuffer<HouseholdCitizen> members =
                EntityManager.GetBuffer<HouseholdCitizen>(household, true);
            int liveMembers = 0;
            for (int i = 0; i < members.Length; i++)
            {
                Entity citizen = members[i].m_Citizen;
                if (citizen != Entity.Null && EntityManager.Exists(citizen) &&
                    !EntityManager.HasComponent<Deleted>(citizen) &&
                    EntityManager.HasComponent<HouseholdMember>(citizen) &&
                    EntityManager.GetComponentData<HouseholdMember>(citizen).m_Household == household)
                    liveMembers++;
            }
            if (liveMembers != wanted.Citizens.Length) return liveMembers;

            int matched = 0;
            for (int i = 0; i < wanted.Citizens.Length; i++)
            {
                Entity citizen;
                if (TryResolveCitizen(wanted.Citizens[i].CitizenId, out citizen) &&
                    CitizenBelongsToHousehold(citizen, household)) matched++;
            }
            return matched;
        }

        private int CountLiveOwnedVehicles(Entity household)
        {
            if (household == Entity.Null || !EntityManager.Exists(household) ||
                !EntityManager.HasBuffer<OwnedVehicle>(household)) return 0;
            DynamicBuffer<OwnedVehicle> owned = EntityManager.GetBuffer<OwnedVehicle>(household, true);
            int count = 0;
            for (int i = 0; i < owned.Length; i++)
            {
                Entity vehicle = owned[i].m_Vehicle;
                if (vehicle == Entity.Null || !EntityManager.Exists(vehicle) ||
                    EntityManager.HasComponent<Deleted>(vehicle) ||
                    !EntityManager.HasComponent<global::Game.Vehicles.PersonalCar>(vehicle) ||
                    !EntityManager.HasComponent<Owner>(vehicle) ||
                    EntityManager.GetComponentData<Owner>(vehicle).m_Owner != household) continue;
                count++;
            }
            return count;
        }

        private bool TryGetCachedHousehold(Entity property, ulong householdId,
            out OccupancyHousehold household, out ulong revision)
        {
            household = default(OccupancyHousehold);
            revision = 0;
            CachedProperty cached;
            if (!_cache.TryGetValue(property, out cached) || cached.Households == null) return false;
            for (int i = 0; i < cached.Households.Length; i++)
            {
                if (cached.Households[i].HouseholdId != householdId ||
                    cached.Households[i].Departing) continue;
                household = cached.Households[i];
                revision = cached.Revision;
                return true;
            }
            return false;
        }

        private bool CleanupCancelledCreatedHousehold(Entity household)
        {
            if (!IsLiveMappedHousehold(household)) return true;
            Entity rentedProperty = Entity.Null;
            if (EntityManager.HasComponent<PropertyRenter>(household))
            {
                rentedProperty = EntityManager.GetComponentData<PropertyRenter>(household)
                    .m_Property;
            }

            if (EntityManager.HasBuffer<HouseholdCitizen>(household))
            {
                _memberScratch.Clear();
                DynamicBuffer<HouseholdCitizen> members =
                    EntityManager.GetBuffer<HouseholdCitizen>(household, true);
                for (int i = 0; i < members.Length; i++)
                    _memberScratch.Add(members[i].m_Citizen);
                bool waitingForDestination = false;
                for (int i = 0; i < _memberScratch.Count; i++)
                {
                    Entity citizen = _memberScratch[i];
                    if (citizen == Entity.Null || !EntityManager.Exists(citizen)) continue;
                    ulong citizenId, desiredHouseholdId;
                    if (TryGetBoundCitizenId(citizen, out citizenId) &&
                        TryGetDesiredHouseholdId(citizenId, out desiredHouseholdId))
                    {
                        Entity destinationHousehold, destinationProperty;
                        if (TryResolveHousehold(desiredHouseholdId, out destinationHousehold) &&
                            TryGetDesiredProperty(desiredHouseholdId, out destinationProperty) &&
                            destinationHousehold != household)
                        {
                            MoveCitizenToHousehold(citizen, destinationHousehold);
                            MarkDirty(destinationProperty);
                        }
                        else
                        {
                            if (TryGetDesiredProperty(desiredHouseholdId, out destinationProperty))
                                MarkDirty(destinationProperty);
                            waitingForDestination = true;
                        }
                        continue;
                    }
                    UnbindCitizen(citizen);
                    if (!EntityManager.HasComponent<Deleted>(citizen))
                        EntityManager.AddComponent<Deleted>(citizen);
                }
                if (waitingForDestination)
                {
                    _memberScratch.Clear();
                    return false;
                }
            }
            // Keep the shell's native two-way rent link intact while any active resident still
            // waits for a resolvable destination. Once everyone is detached, the shell can no
            // longer strand a person and its slot is safe to release.
            if (rentedProperty != Entity.Null && EntityManager.Exists(rentedProperty))
                RemoveRenterReference(rentedProperty, household);
            if (EntityManager.HasBuffer<HouseholdAnimal>(household))
            {
                _memberScratch.Clear();
                DynamicBuffer<HouseholdAnimal> pets =
                    EntityManager.GetBuffer<HouseholdAnimal>(household, true);
                for (int i = 0; i < pets.Length; i++)
                    _memberScratch.Add(pets[i].m_HouseholdPet);
                for (int i = 0; i < _memberScratch.Count; i++)
                {
                    Entity pet = _memberScratch[i];
                    if (pet != Entity.Null && EntityManager.Exists(pet) &&
                        !EntityManager.HasComponent<Deleted>(pet))
                        EntityManager.AddComponent<Deleted>(pet);
                }
            }
            _memberScratch.Clear();
            UnbindHousehold(household);
            if (!EntityManager.HasComponent<Deleted>(household))
                EntityManager.AddComponent<Deleted>(household);
            return true;
        }
    }
}
