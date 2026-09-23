using System.Collections.Generic;
using CS2MultiplayerMod.Game.Sync.Commands;
using Game.Buildings;
using Game.Citizens;
using Game.Common;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    public partial class ResidentialOccupancySyncSystem
    {
        // The only host-id-to-local-entity maps; cleared when the session world changes.
        private readonly Dictionary<ulong, Entity> _householdsByHostId =
            new Dictionary<ulong, Entity>();
        private readonly Dictionary<Entity, ulong> _hostIdsByHousehold =
            new Dictionary<Entity, ulong>();
        private readonly Dictionary<ulong, Entity> _citizensByHostId =
            new Dictionary<ulong, Entity>();
        private readonly Dictionary<Entity, ulong> _hostIdsByCitizen =
            new Dictionary<Entity, ulong>();
        private readonly Dictionary<PropertyIdentity, Entity> _propertiesByIdentity =
            new Dictionary<PropertyIdentity, Entity>();

        // Learned when a page arrives. Absence never changes identity; explicit tombstones do.
        private readonly Dictionary<ulong, DesiredHouseholdLocation> _desiredHouseholds =
            new Dictionary<ulong, DesiredHouseholdLocation>();
        private readonly Dictionary<ulong, DesiredCitizenLocation> _desiredCitizens =
            new Dictionary<ulong, DesiredCitizenLocation>();
        private readonly Dictionary<ulong, HashSet<ulong>> _desiredCitizensByHousehold =
            new Dictionary<ulong, HashSet<ulong>>();

        private struct DesiredHouseholdLocation
        {
            public PropertyIdentity PropertyIdentity;
            public ulong Revision;
            public uint LastSeenSweep;
            public bool Active;
            public bool Unhoused;
        }

        private struct DesiredCitizenLocation
        {
            public ulong HouseholdId;
            public ulong Revision;
            public uint LastSeenSweep;
            public bool Active;
        }

        /// <summary>An opaque, session-scoped key; never a local entity handle.</summary>
        private static ulong PackHostEntityId(Entity entity)
        {
            if (entity == Entity.Null || entity.Index < 0) return 0;
            return ((ulong)(uint)entity.Version << 32) | (uint)entity.Index;
        }

        /// <summary>Citizen key shared with company snapshots; resolve only through this system's map.</summary>
        internal static ulong PackNetworkCitizenId(Entity citizen) =>
            PackHostEntityId(citizen);

        /// <summary>Lets the company channel attach a job to a real local citizen.</summary>
        internal bool TryResolveCompanyCitizen(ulong citizenId, out Entity citizen) =>
            TryResolveCitizen(citizenId, out citizen);

        private bool TryResolveHousehold(ulong householdId, out Entity household)
        {
            household = Entity.Null;
            if (householdId == 0) return false;

            if (!_householdsByHostId.TryGetValue(householdId, out Entity candidate)) return false;
            if (!IsLiveMappedHousehold(candidate))
            {
                RemoveHouseholdBinding(householdId, candidate);
                return false;
            }

            if (!_hostIdsByHousehold.TryGetValue(candidate, out ulong reverseId) ||
                reverseId != householdId)
            {
                // Never return one half of a conflicting association.
                _householdsByHostId.Remove(householdId);
                return false;
            }

            household = candidate;
            return true;
        }

        private bool TryResolveCitizen(ulong citizenId, out Entity citizen)
        {
            citizen = Entity.Null;
            if (citizenId == 0) return false;

            if (!_citizensByHostId.TryGetValue(citizenId, out Entity candidate)) return false;
            if (!IsLiveMappedCitizen(candidate))
            {
                RemoveCitizenBinding(citizenId, candidate);
                return false;
            }

            if (!_hostIdsByCitizen.TryGetValue(candidate, out ulong reverseId) ||
                reverseId != citizenId)
            {
                _citizensByHostId.Remove(citizenId);
                return false;
            }

            citizen = candidate;
            return true;
        }

        /// <summary>Rebinding removes both sides of any older association first.</summary>
        private bool BindHousehold(ulong householdId, Entity household)
        {
            if (householdId == 0 || !IsLiveMappedHousehold(household)) return false;

            if (_householdsByHostId.TryGetValue(householdId, out Entity previousHousehold) &&
                previousHousehold != household)
                RemoveHouseholdBinding(householdId, previousHousehold);

            if (_hostIdsByHousehold.TryGetValue(household, out ulong previousId) &&
                previousId != householdId)
                RemoveHouseholdBinding(previousId, household);

            _householdsByHostId[householdId] = household;
            _hostIdsByHousehold[household] = householdId;
            _unboundHouseholdSince.Remove(household);
            ForgetLoadedWorldHouseholdRent(household);
            return true;
        }

        /// <summary>Citizen counterpart of <see cref="BindHousehold"/>.</summary>
        private bool BindCitizen(ulong citizenId, Entity citizen)
        {
            if (citizenId == 0 || !IsLiveMappedCitizen(citizen)) return false;

            if (_citizensByHostId.TryGetValue(citizenId, out Entity previousCitizen) &&
                previousCitizen != citizen)
                RemoveCitizenBinding(citizenId, previousCitizen);

            if (_hostIdsByCitizen.TryGetValue(citizen, out ulong previousId) && previousId != citizenId)
                RemoveCitizenBinding(previousId, citizen);

            _citizensByHostId[citizenId] = citizen;
            _hostIdsByCitizen[citizen] = citizenId;
            _unboundCitizenSince.Remove(citizen);
            return true;
        }

        private void UnbindHousehold(ulong householdId)
        {
            if (_householdsByHostId.TryGetValue(householdId, out Entity household))
                RemoveHouseholdBinding(householdId, household);
        }

        private void UnbindHousehold(Entity household)
        {
            if (_hostIdsByHousehold.TryGetValue(household, out ulong householdId))
                RemoveHouseholdBinding(householdId, household);
        }

        private void UnbindCitizen(ulong citizenId)
        {
            if (_citizensByHostId.TryGetValue(citizenId, out Entity citizen))
                RemoveCitizenBinding(citizenId, citizen);
        }

        private void UnbindCitizen(Entity citizen)
        {
            if (_hostIdsByCitizen.TryGetValue(citizen, out ulong citizenId))
                RemoveCitizenBinding(citizenId, citizen);
        }

        /// <summary>Map lookup first: most households touched by local systems were never bound.</summary>
        private bool TryGetBoundHouseholdId(Entity household, out ulong householdId)
        {
            if (!_hostIdsByHousehold.TryGetValue(household, out householdId))
            {
                householdId = 0;
                return false;
            }
            if (!IsLiveMappedHousehold(household))
            {
                UnbindHousehold(household);
                householdId = 0;
                return false;
            }

            if (_householdsByHostId.TryGetValue(householdId, out Entity reverse) && reverse == household)
                return true;

            _hostIdsByHousehold.Remove(household);
            householdId = 0;
            return false;
        }

        private bool TryGetBoundCitizenId(Entity citizen, out ulong citizenId)
        {
            if (!_hostIdsByCitizen.TryGetValue(citizen, out citizenId))
            {
                citizenId = 0;
                return false;
            }
            if (!IsLiveMappedCitizen(citizen))
            {
                UnbindCitizen(citizen);
                citizenId = 0;
                return false;
            }

            if (_citizensByHostId.TryGetValue(citizenId, out Entity reverse) && reverse == citizen)
                return true;

            _hostIdsByCitizen.Remove(citizen);
            citizenId = 0;
            return false;
        }

        /// <summary>
        /// Observes positive identities in wire order, independent of property resolution: a move
        /// destination may be pending while the source already reports the household absent.
        /// </summary>
        private void ObserveIncomingRoster(OccupancyProperty property, uint sweepId)
        {
            OccupancyHousehold[] current = property.Households;
            if (current == null) return;
            for (int i = 0; i < current.Length; i++)
            {
                OccupancyHousehold household = current[i];
                ulong householdId = household.HouseholdId;
                if (householdId == 0) continue;

                if (household.Departing)
                {
                    ObserveDepartingHousehold(householdId, property.Identity,
                        property.Revision, sweepId);
                    ForgetDesiredHouseholdEconomy(householdId, property.Revision);
                    OccupancyCitizen[] leavingCitizens = household.Citizens;
                    if (leavingCitizens != null)
                        for (int j = 0; j < leavingCitizens.Length; j++)
                            ObserveDepartingCitizen(leavingCitizens[j].CitizenId,
                                householdId, property.Revision, sweepId);
                    continue;
                }

                ObserveDesiredHousehold(householdId, property.Identity,
                    property.Revision, sweepId);
                ObserveDesiredHouseholdEconomy(householdId, property.Identity,
                    property.Revision, household);
                OccupancyCitizen[] citizens = household.Citizens;
                if (citizens == null) continue;
                for (int j = 0; j < citizens.Length; j++)
                {
                    ulong citizenId = citizens[j].CitizenId;
                    if (citizenId != 0)
                        ObserveDesiredCitizen(citizenId, householdId,
                            property.Revision, sweepId);
                }
            }
        }

        private void RegisterResolvedProperty(PropertyIdentity identity, Entity property)
        {
            if (_propertiesByIdentity.TryGetValue(identity, out Entity previous) && previous != property &&
                IsLiveProperty(previous) && PositionMatchesAnchor(previous, identity)) return;
            _propertiesByIdentity[identity] = property;
        }

        private void UnregisterResolvedProperty(PropertyIdentity identity, Entity property)
        {
            if (_propertiesByIdentity.TryGetValue(identity, out Entity current) && current == property)
                _propertiesByIdentity.Remove(identity);
        }

        private bool TryGetPropertyIdentity(Entity property, out PropertyIdentity identity)
        {
            identity = default(PropertyIdentity);
            if (property == Entity.Null || !_cache.TryGetValue(property, out CachedProperty cached)) return false;
            identity = cached.Identity;
            return true;
        }

        private bool TryGetDesiredPropertyIdentity(ulong householdId,
            out PropertyIdentity identity)
        {
            identity = default(PropertyIdentity);
            if (householdId == 0 ||
                !_desiredHouseholds.TryGetValue(householdId, out DesiredHouseholdLocation location) ||
                !location.Active || location.Unhoused) return false;
            identity = location.PropertyIdentity;
            return true;
        }

        private bool IsHouseholdDesiredHere(ulong householdId, Entity property)
        {
            return TryGetDesiredPropertyIdentity(householdId, out PropertyIdentity desired) &&
                   TryGetPropertyIdentity(property, out PropertyIdentity local) && desired.Equals(local);
        }

        private bool TryGetDesiredProperty(ulong householdId, out Entity property)
        {
            property = Entity.Null;
            if (!TryGetDesiredPropertyIdentity(householdId, out PropertyIdentity identity) ||
                !_propertiesByIdentity.TryGetValue(identity, out property) ||
                !IsLiveProperty(property))
            {
                property = Entity.Null;
                return false;
            }
            return true;
        }

        private bool IsCitizenDesiredHere(ulong citizenId, ulong householdId)
        {
            return citizenId != 0 && householdId != 0 &&
                   _desiredCitizens.TryGetValue(citizenId, out DesiredCitizenLocation location) && location.Active &&
                   location.HouseholdId == householdId;
        }

        private bool TryGetDesiredHouseholdId(ulong citizenId, out ulong householdId)
        {
            householdId = 0;
            if (citizenId == 0 || !_desiredCitizens.TryGetValue(citizenId, out DesiredCitizenLocation location) ||
                !location.Active || location.HouseholdId == 0) return false;
            householdId = location.HouseholdId;
            return true;
        }

        private void ClearIdentityState()
        {
            _householdsByHostId.Clear();
            _hostIdsByHousehold.Clear();
            _citizensByHostId.Clear();
            _hostIdsByCitizen.Clear();
            _propertiesByIdentity.Clear();
            _desiredHouseholds.Clear();
            _desiredHouseholdEconomies.Clear();
            ClearHouseholdEconomyCorrections();
            ClearPropertyFeeCorrections();
            _desiredCitizens.Clear();
            _desiredCitizensByHousehold.Clear();
        }

        private bool IsLiveMappedHousehold(Entity household) =>
            household != Entity.Null && EntityManager.Exists(household) &&
            EntityManager.HasComponent<Household>(household) &&
            !EntityManager.HasComponent<Deleted>(household);

        private bool IsLiveMappedCitizen(Entity citizen) =>
            citizen != Entity.Null && EntityManager.Exists(citizen) &&
            EntityManager.HasComponent<Citizen>(citizen) &&
            !EntityManager.HasComponent<Deleted>(citizen);

        private void RemoveHouseholdBinding(ulong householdId, Entity household)
        {
            if (_householdsByHostId.TryGetValue(householdId, out Entity forward) && forward == household)
                _householdsByHostId.Remove(householdId);

            if (_hostIdsByHousehold.TryGetValue(household, out ulong reverse) && reverse == householdId)
                _hostIdsByHousehold.Remove(household);
            _arrivalSources.Remove(household);
        }

        private void RemoveCitizenBinding(ulong citizenId, Entity citizen)
        {
            if (_citizensByHostId.TryGetValue(citizenId, out Entity forward) && forward == citizen)
                _citizensByHostId.Remove(citizenId);

            if (_hostIdsByCitizen.TryGetValue(citizen, out ulong reverse) && reverse == citizenId)
                _hostIdsByCitizen.Remove(citizen);
        }

        private void ObserveDesiredHousehold(ulong householdId, PropertyIdentity property,
            ulong revision, uint sweepId)
        {
            if (_desiredHouseholds.TryGetValue(householdId, out DesiredHouseholdLocation existing) &&
                revision <= existing.Revision) return;
            _desiredHouseholds[householdId] = new DesiredHouseholdLocation
            {
                PropertyIdentity = property,
                Revision = revision,
                LastSeenSweep = sweepId,
                Active = true,
                Unhoused = false,
            };
        }

        private void ObserveDesiredCitizen(ulong citizenId, ulong householdId, ulong revision,
            uint sweepId)
        {
            if (_desiredCitizens.TryGetValue(citizenId, out DesiredCitizenLocation existing) &&
                revision <= existing.Revision) return;
            if (existing.Active && existing.HouseholdId != householdId)
                RemoveDesiredCitizenIndex(existing.HouseholdId, citizenId);
            _desiredCitizens[citizenId] = new DesiredCitizenLocation
            {
                HouseholdId = householdId,
                Revision = revision,
                LastSeenSweep = sweepId,
                Active = true,
            };
            AddDesiredCitizenIndex(householdId, citizenId);
        }

        private void AddDesiredCitizenIndex(ulong householdId, ulong citizenId)
        {
            if (householdId == 0 || citizenId == 0) return;
            if (!_desiredCitizensByHousehold.TryGetValue(householdId, out HashSet<ulong> citizens))
            {
                citizens = new HashSet<ulong>();
                _desiredCitizensByHousehold[householdId] = citizens;
            }
            citizens.Add(citizenId);
        }

        private void RemoveDesiredCitizenIndex(ulong householdId, ulong citizenId)
        {
            if (householdId == 0 ||
                !_desiredCitizensByHousehold.TryGetValue(householdId, out HashSet<ulong> citizens)) return;
            citizens.Remove(citizenId);
            if (citizens.Count == 0) _desiredCitizensByHousehold.Remove(householdId);
        }

        private void ObserveDepartingHousehold(ulong householdId,
            PropertyIdentity property, ulong revision, uint sweepId)
        {
            if (householdId == 0) return;
            if (_desiredHouseholds.TryGetValue(householdId, out DesiredHouseholdLocation existing) &&
                revision <= existing.Revision) return;
            _desiredHouseholds[householdId] = new DesiredHouseholdLocation
            {
                PropertyIdentity = property,
                Revision = revision,
                LastSeenSweep = sweepId,
                Active = false,
                Unhoused = false,
            };
        }

        private void ObserveDepartureRecord(OccupancyDeparture departure, uint sweepId)
        {
            if (departure.HouseholdId == 0 || departure.Revision == 0) return;
            if (_desiredHouseholds.TryGetValue(departure.HouseholdId, out DesiredHouseholdLocation existing) &&
                departure.Revision <= existing.Revision) return;

            if (existing.PropertyIdentity.PrefabName != null &&
                _propertiesByIdentity.TryGetValue(existing.PropertyIdentity, out Entity property) &&
                IsLiveProperty(property)) MarkDirty(property);
            existing.Revision = departure.Revision;
            existing.LastSeenSweep = sweepId;
            existing.Active = departure.Unhoused;
            existing.Unhoused = departure.Unhoused;
            _desiredHouseholds[departure.HouseholdId] = existing;
            ForgetDesiredHouseholdEconomy(departure.HouseholdId, departure.Revision);
        }

        private bool IsHouseholdDesiredUnhoused(ulong householdId)
        {
            return householdId != 0 &&
                   _desiredHouseholds.TryGetValue(householdId, out DesiredHouseholdLocation location) &&
                   location.Active && location.Unhoused;
        }

        private void ObserveCitizenDepartureRecord(OccupancyCitizenDeparture departure,
            uint sweepId)
        {
            if (departure.CitizenId == 0 || departure.Revision == 0) return;
            if (_desiredCitizens.TryGetValue(departure.CitizenId, out DesiredCitizenLocation existing) &&
                departure.Revision <= existing.Revision) return;

            MarkCitizenHouseholdDirty(departure.CitizenId, existing.HouseholdId);
            if (existing.Active)
                RemoveDesiredCitizenIndex(existing.HouseholdId, departure.CitizenId);
            existing.Revision = departure.Revision;
            existing.LastSeenSweep = sweepId;
            existing.Active = false;
            _desiredCitizens[departure.CitizenId] = existing;
            QueueCitizenRetirement(departure.CitizenId);
        }

        private void MarkCitizenHouseholdDirty(ulong citizenId, ulong lastHouseholdId)
        {
            if (TryResolveCitizen(citizenId, out Entity citizen) &&
                EntityManager.HasComponent<HouseholdMember>(citizen))
            {
                Entity household = EntityManager.GetComponentData<HouseholdMember>(citizen)
                    .m_Household;
                if (household != Entity.Null && EntityManager.Exists(household) &&
                    EntityManager.HasComponent<PropertyRenter>(household))
                {
                    Entity property = EntityManager.GetComponentData<PropertyRenter>(household)
                        .m_Property;
                    if (property != Entity.Null && EntityManager.Exists(property))
                        MarkDirty(property);
                    return;
                }
            }

            if (lastHouseholdId != 0 && TryResolveHousehold(lastHouseholdId, out Entity mappedHousehold) &&
                EntityManager.HasComponent<PropertyRenter>(mappedHousehold))
            {
                Entity property = EntityManager.GetComponentData<PropertyRenter>(mappedHousehold)
                    .m_Property;
                if (property != Entity.Null && EntityManager.Exists(property)) MarkDirty(property);
            }
        }

        private void QueueCitizenRetirement(ulong citizenId)
        {
            if (citizenId == 0 || !_pendingCitizenRetirementIds.Add(citizenId)) return;
            _pendingCitizenRetirements.Enqueue(citizenId);
        }

        private void ObserveDepartingCitizen(ulong citizenId, ulong householdId, ulong revision,
            uint sweepId)
        {
            if (citizenId == 0) return;
            if (_desiredCitizens.TryGetValue(citizenId, out DesiredCitizenLocation existing) &&
                revision <= existing.Revision) return;
            if (existing.Active) RemoveDesiredCitizenIndex(existing.HouseholdId, citizenId);
            _desiredCitizens[citizenId] = new DesiredCitizenLocation
            {
                HouseholdId = householdId,
                Revision = revision,
                LastSeenSweep = sweepId,
                Active = false,
            };
            QueueCitizenRetirement(citizenId);
        }
    }
}
