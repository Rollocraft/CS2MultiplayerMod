using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    // Where a household lives: the renter links between household and property, the checks
    // that decide whether a property can take one, and applying the host's household state.
    public partial class ResidentialOccupancySyncSystem
    {
        private bool IsHouseholdAtProperty(Entity household, Entity property)
        {
            if (!EntityManager.HasComponent<PropertyRenter>(household) ||
                EntityManager.GetComponentData<PropertyRenter>(household).m_Property != property)
                return false;
            DynamicBuffer<Renter> renters = EntityManager.GetBuffer<Renter>(property);
            bool found = false;
            for (int i = renters.Length - 1; i >= 0; i--)
            {
                if (renters[i].m_Renter != household) continue;
                if (!found) found = true;
                else renters.RemoveAt(i);
            }
            if (!found)
            {
                renters.Add(new Renter { m_Renter = household });
                MarkRentersUpdated(property);
            }
            return true;
        }

        private void CancelUnauthorizedDeparture(Entity household)
        {
            _authorizedMoveAways.Remove(household);
            if (EntityManager.HasComponent<MovingAway>(household))
                EntityManager.RemoveComponent<MovingAway>(household);
            if (EntityManager.HasComponent<PropertySeeker>(household))
                EntityManager.SetComponentEnabled<PropertySeeker>(household, false);
        }

        private void RemoveRenterReference(Entity property, Entity household)
        {
            if (!EntityManager.HasBuffer<Renter>(property)) return;
            DynamicBuffer<Renter> renters = EntityManager.GetBuffer<Renter>(property);
            bool changed = false;
            for (int i = renters.Length - 1; i >= 0; i--)
            {
                if (renters[i].m_Renter != household) continue;
                renters.RemoveAt(i);
                changed = true;
            }
            if (changed) MarkRentersUpdated(property);
        }

        private void ReleaseUnhousedHousehold(ulong householdId, Entity household, Entity property)
        {
            _authorizedMoveAways.Remove(household);
            RemoveRenterReference(property, household);
            if (EntityManager.HasComponent<PropertyRenter>(household))
                EntityManager.RemoveComponent<PropertyRenter>(household);
            if (EntityManager.HasComponent<PropertySeeker>(household))
                EntityManager.SetComponentEnabled<PropertySeeker>(household, false);
            _pendingMoveIns.Remove(householdId);
            _stagedTransfers.Remove(householdId);
            _stagedTransferCooldownUntil.Remove(householdId);
            _settling.Remove(household);
            _unreachableSince.Remove(household);
        }

        private void MarkRentersUpdated(Entity property)
        {
            // This notification is an event entity, not a tag on the building. Clean up the old
            // malformed marker as well so upgraded sessions can emit a valid notification.
            if (EntityManager.HasComponent<RentersUpdated>(property) &&
                !EntityManager.HasComponent<global::Game.Common.Event>(property))
                EntityManager.RemoveComponent<RentersUpdated>(property);

            Entity update = EntityManager.CreateEntity();
            EntityManager.AddComponent<global::Game.Common.Event>(update);
            EntityManager.AddComponentData(update, new RentersUpdated(property));
        }

        private int FreeResidentialSlots(Entity property)
        {
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(property).m_Prefab;
            if (!EntityManager.HasComponent<BuildingPropertyData>(prefab)) return 0;
            int capacity = EntityManager.GetComponentData<BuildingPropertyData>(prefab)
                .CountProperties(global::Game.Zones.AreaType.Residential);
            DynamicBuffer<Renter> renters = EntityManager.GetBuffer<Renter>(property, true);
            for (int i = 0; i < renters.Length; i++)
            {
                Entity renter = renters[i].m_Renter;
                if (renter == Entity.Null || !EntityManager.Exists(renter) ||
                    !EntityManager.HasComponent<Household>(renter) ||
                    EntityManager.HasComponent<Deleted>(renter) ||
                    !EntityManager.HasComponent<PropertyRenter>(renter) ||
                    EntityManager.GetComponentData<PropertyRenter>(renter).m_Property != property)
                    continue;
                capacity--;
            }
            return capacity;
        }

        private bool CanStageTransferTo(Entity destination)
        {
            if (!CanEnqueueRentAction() || !IsLiveProperty(destination)) return false;
            CachedProperty cached;
            if (!_cache.TryGetValue(destination, out cached) || cached.RemoveAfterApply ||
                cached.Households == null) return false;
            if (EntityManager.HasComponent<global::Game.Objects.UnderConstruction>(destination) &&
                cached.ConstructionSpeed == 0) return false;

            Entity prefab = EntityManager.GetComponentData<PrefabRef>(destination).m_Prefab;
            if (!EntityManager.HasComponent<BuildingPropertyData>(prefab)) return false;
            int capacity = EntityManager.GetComponentData<BuildingPropertyData>(prefab)
                .CountProperties(global::Game.Zones.AreaType.Residential);

            int desiredCount = 0;
            for (int i = 0; i < cached.Households.Length; i++)
            {
                OccupancyHousehold wanted = cached.Households[i];
                if (!wanted.Departing && IsHouseholdDesiredHere(wanted.HouseholdId, destination))
                    desiredCount++;
            }

            int fixedOccupants = 0;
            DynamicBuffer<Renter> renters = EntityManager.GetBuffer<Renter>(destination, true);
            for (int i = 0; i < renters.Length; i++)
            {
                Entity renter = renters[i].m_Renter;
                if (renter == Entity.Null || !EntityManager.Exists(renter) ||
                    !EntityManager.HasComponent<Household>(renter) ||
                    EntityManager.HasComponent<Deleted>(renter) ||
                    !EntityManager.HasComponent<PropertyRenter>(renter) ||
                    EntityManager.GetComponentData<PropertyRenter>(renter).m_Property != destination)
                    continue;
                if (EntityManager.HasComponent<TouristHousehold>(renter) ||
                    EntityManager.HasComponent<CommuterHousehold>(renter))
                {
                    fixedOccupants++;
                    continue;
                }

                ulong renterId;
                PropertyRentIdentity desiredIdentity, destinationIdentity;
                if (!TryGetBoundHouseholdId(renter, out renterId))
                    continue; // an unbound bootstrap extra is retireable
                if (!TryGetDesiredPropertyIdentity(renterId, out desiredIdentity))
                {
                    if (HasActiveDesiredCitizenStillLinked(renter)) fixedOccupants++;
                    continue;
                }
                if (!TryGetPropertyIdentity(destination, out destinationIdentity))
                {
                    fixedOccupants++;
                    continue;
                }

                if (desiredIdentity.Equals(destinationIdentity))
                {
                    bool inRoster = false;
                    for (int h = 0; h < cached.Households.Length; h++)
                    {
                        if (cached.Households[h].Departing ||
                            cached.Households[h].HouseholdId != renterId) continue;
                        inRoster = true;
                        break;
                    }
                    if (!inRoster) fixedOccupants++;
                    continue;
                }

                Entity outgoingDestination;
                if (!TryGetDesiredProperty(renterId, out outgoingDestination)) fixedOccupants++;
            }
            return desiredCount + fixedOccupants <= capacity;
        }

        private bool HasResidentialRenter(Entity property)
        {
            if (!IsLiveProperty(property)) return false;
            DynamicBuffer<Renter> renters = EntityManager.GetBuffer<Renter>(property, true);
            for (int i = 0; i < renters.Length; i++)
            {
                Entity renter = renters[i].m_Renter;
                if (renter != Entity.Null && EntityManager.Exists(renter) &&
                    EntityManager.HasComponent<Household>(renter) &&
                    !EntityManager.HasComponent<TouristHousehold>(renter) &&
                    !EntityManager.HasComponent<CommuterHousehold>(renter) &&
                    !EntityManager.HasComponent<Deleted>(renter)) return true;
            }
            return false;
        }

        private void ApplyHousehold(Entity household, Entity property, OccupancyHousehold wanted)
        {
            Entity prefab;
            if (ResolvePrefab<HouseholdData>(wanted.PrefabName, out prefab) &&
                EntityManager.HasComponent<PrefabRef>(household) &&
                EntityManager.GetComponentData<PrefabRef>(household).m_Prefab != prefab)
                EntityManager.SetComponentData(household, new PrefabRef(prefab));

            Household data = EntityManager.GetComponentData<Household>(household);
            var flags = (HouseholdFlags)(wanted.Flags & HouseholdFlagMask);
            // Arrival owns this bit on every peer. Preserve a completed local move-in, but never
            // import it early from a host page because that would suppress the population event.
            if ((data.m_Flags & HouseholdFlags.MovedIn) != 0)
                flags |= HouseholdFlags.MovedIn;
            if (data.m_Flags != flags)
            {
                data.m_Flags = flags;
                EntityManager.SetComponentData(household, data);
            }
            ApplyHouseholdEconomy(household, property,
                DesiredHouseholdEconomy.From(wanted, default(PropertyRentIdentity), 0));

            ApplyNameIndices(household, wanted.NameIndices);
            ApplyCitizens(household, property, wanted);
            ApplyPets(household, property, wanted);
            ApplyOwnedVehicles(household, property, wanted);
        }
    }
}
