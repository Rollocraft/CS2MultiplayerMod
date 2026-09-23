using CS2MultiplayerMod.Game.Sync.Infrastructure;
using CS2MultiplayerMod.Game.Sync.Commands;
using Game.Agents;
using Game.Buildings;
using Game.Citizens;
using Game.Common;
using Game.Prefabs;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    public partial class ResidentialOccupancySyncSystem
    {
        private bool IsHouseholdAtProperty(Entity household, Entity property)
        {
            if (!EntityManager.HasComponent<PropertyRenter>(household) ||
                EntityManager.GetComponentData<PropertyRenter>(household).m_Property != property)
                return false;
            var renters = new BufferEdit<Renter>(EntityManager, property);
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
            var renters = new BufferEdit<Renter>(EntityManager, property);
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
            // The notification is an event entity, not a tag; clean up the old malformed marker too.
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
            if (!_cache.TryGetValue(destination, out CachedProperty cached) || cached.RemoveAfterApply ||
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

                if (!TryGetBoundHouseholdId(renter, out ulong renterId))
                    continue; // an unbound bootstrap extra is retireable
                if (!TryGetDesiredPropertyIdentity(renterId, out PropertyIdentity desiredIdentity))
                {
                    if (HasActiveDesiredCitizenStillLinked(renter)) fixedOccupants++;
                    continue;
                }
                if (!TryGetPropertyIdentity(destination, out PropertyIdentity destinationIdentity))
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

                if (!TryGetDesiredProperty(renterId, out Entity outgoingDestination)) fixedOccupants++;
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
            if (ResolvePrefab<HouseholdData>(wanted.PrefabName, out Entity prefab) &&
                EntityManager.HasComponent<PrefabRef>(household) &&
                EntityManager.GetComponentData<PrefabRef>(household).m_Prefab != prefab)
                EntityManager.SetComponentData(household, new PrefabRef(prefab));

            Household data = EntityManager.GetComponentData<Household>(household);
            var flags = (HouseholdFlags)(wanted.Flags & HouseholdFlagMask);
            // Arrival owns MovedIn on every peer; never import it early.
            if ((data.m_Flags & HouseholdFlags.MovedIn) != 0)
                flags |= HouseholdFlags.MovedIn;
            if (data.m_Flags != flags)
            {
                data.m_Flags = flags;
                EntityManager.SetComponentData(household, data);
            }
            ApplyHouseholdEconomy(household, property,
                DesiredHouseholdEconomy.From(wanted, default(PropertyIdentity), 0));

            ApplyNameIndices(household, wanted.NameIndices);
            ApplyCitizens(household, property, wanted);
            ApplyPets(household, property, wanted);
            ApplyOwnedVehicles(household, property, wanted);
        }
    }
}
