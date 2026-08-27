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
        // Host entity ids are opaque on a client. These maps are the only place where a host id is
        // associated with a local entity, and they are cleared whenever the session world changes.
        private readonly Dictionary<ulong, Entity> _householdsByHostId =
            new Dictionary<ulong, Entity>();
        private readonly Dictionary<Entity, ulong> _hostIdsByHousehold =
            new Dictionary<Entity, ulong>();
        private readonly Dictionary<ulong, Entity> _citizensByHostId =
            new Dictionary<ulong, Entity>();
        private readonly Dictionary<Entity, ulong> _hostIdsByCitizen =
            new Dictionary<Entity, ulong>();
        private readonly Dictionary<PropertyRentIdentity, Entity> _propertiesByIdentity =
            new Dictionary<PropertyRentIdentity, Entity>();

        // Positive locations are learned as soon as a page arrives, before its property resolves.
        // One absolute property page cannot distinguish a departure from a move whose destination
        // page dropped, so absence never changes identity state. Explicit retained tombstones do.
        private readonly Dictionary<ulong, DesiredHouseholdLocation> _desiredHouseholds =
            new Dictionary<ulong, DesiredHouseholdLocation>();
        private readonly Dictionary<ulong, DesiredCitizenLocation> _desiredCitizens =
            new Dictionary<ulong, DesiredCitizenLocation>();
        private readonly Dictionary<ulong, HashSet<ulong>> _desiredCitizensByHousehold =
            new Dictionary<ulong, HashSet<ulong>>();

        private struct DesiredHouseholdLocation
        {
            public PropertyRentIdentity PropertyIdentity;
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

        /// <summary>
        /// Packs a host entity into an opaque, session-scoped identity. A client must never treat
        /// the result as one of its own entity handles.
        /// </summary>
        private static ulong PackHostEntityId(Entity entity)
        {
            if (entity == Entity.Null || entity.Index < 0) return 0;
            return ((ulong)(uint)entity.Version << 32) | (uint)entity.Index;
        }

        private bool TryResolveHousehold(ulong householdId, out Entity household)
        {
            household = Entity.Null;
            if (householdId == 0) return false;

            Entity candidate;
            if (!_householdsByHostId.TryGetValue(householdId, out candidate)) return false;
            if (!IsLiveMappedHousehold(candidate))
            {
                RemoveHouseholdBinding(householdId, candidate);
                return false;
            }

            ulong reverseId;
            if (!_hostIdsByHousehold.TryGetValue(candidate, out reverseId) ||
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

            Entity candidate;
            if (!_citizensByHostId.TryGetValue(citizenId, out candidate)) return false;
            if (!IsLiveMappedCitizen(candidate))
            {
                RemoveCitizenBinding(citizenId, candidate);
                return false;
            }

            ulong reverseId;
            if (!_hostIdsByCitizen.TryGetValue(candidate, out reverseId) ||
                reverseId != citizenId)
            {
                _citizensByHostId.Remove(citizenId);
                return false;
            }

            citizen = candidate;
            return true;
        }

        /// <summary>
        /// Binds one host household id to one local household. Rebinding removes both sides of any
        /// older association first, so forward and reverse lookups can never disagree.
        /// </summary>
        private bool BindHousehold(ulong householdId, Entity household)
        {
            if (householdId == 0 || !IsLiveMappedHousehold(household)) return false;

            Entity previousHousehold;
            if (_householdsByHostId.TryGetValue(householdId, out previousHousehold) &&
                previousHousehold != household)
                RemoveHouseholdBinding(householdId, previousHousehold);

            ulong previousId;
            if (_hostIdsByHousehold.TryGetValue(household, out previousId) &&
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

            Entity previousCitizen;
            if (_citizensByHostId.TryGetValue(citizenId, out previousCitizen) &&
                previousCitizen != citizen)
                RemoveCitizenBinding(citizenId, previousCitizen);

            ulong previousId;
            if (_hostIdsByCitizen.TryGetValue(citizen, out previousId) && previousId != citizenId)
                RemoveCitizenBinding(previousId, citizen);

            _citizensByHostId[citizenId] = citizen;
            _hostIdsByCitizen[citizen] = citizenId;
            _unboundCitizenSince.Remove(citizen);
            return true;
        }

        private void UnbindHousehold(ulong householdId)
        {
            Entity household;
            if (_householdsByHostId.TryGetValue(householdId, out household))
                RemoveHouseholdBinding(householdId, household);
        }

        private void UnbindHousehold(Entity household)
        {
            ulong householdId;
            if (_hostIdsByHousehold.TryGetValue(household, out householdId))
                RemoveHouseholdBinding(householdId, household);
        }

        private void UnbindCitizen(ulong citizenId)
        {
            Entity citizen;
            if (_citizensByHostId.TryGetValue(citizenId, out citizen))
                RemoveCitizenBinding(citizenId, citizen);
        }

        private void UnbindCitizen(Entity citizen)
        {
            ulong citizenId;
            if (_hostIdsByCitizen.TryGetValue(citizen, out citizenId))
                RemoveCitizenBinding(citizenId, citizen);
        }

        /// <summary>
        /// The id map is consulted before the entity is inspected: this is called for every
        /// household a local economy system touched, and most families in a large city were never
        /// bound to a host identity at all. An unmapped entity has nothing to unbind either way.
        /// </summary>
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

            Entity reverse;
            if (_householdsByHostId.TryGetValue(householdId, out reverse) && reverse == household)
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

            Entity reverse;
            if (_citizensByHostId.TryGetValue(citizenId, out reverse) && reverse == citizen)
                return true;

            _hostIdsByCitizen.Remove(citizen);
            citizenId = 0;
            return false;
        }

        /// <summary>
        /// Observe every positive identity directly from wire order. Property resolution is a
        /// separate concern: a move destination may legitimately be pending while the source
        /// property already resolves and reports the household absent.
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

        private void RegisterResolvedProperty(PropertyRentIdentity identity, Entity property)
        {
            Entity previous;
            if (_propertiesByIdentity.TryGetValue(identity, out previous) && previous != property &&
                IsLiveProperty(previous) && PositionMatchesAnchor(previous, identity)) return;
            _propertiesByIdentity[identity] = property;
        }

        private void UnregisterResolvedProperty(PropertyRentIdentity identity, Entity property)
        {
            Entity current;
            if (_propertiesByIdentity.TryGetValue(identity, out current) && current == property)
                _propertiesByIdentity.Remove(identity);
        }

        private bool TryGetPropertyIdentity(Entity property, out PropertyRentIdentity identity)
        {
            identity = default(PropertyRentIdentity);
            CachedProperty cached;
            if (property == Entity.Null || !_cache.TryGetValue(property, out cached)) return false;
            identity = cached.Identity;
            return true;
        }

        private bool TryGetDesiredPropertyIdentity(ulong householdId,
            out PropertyRentIdentity identity)
        {
            identity = default(PropertyRentIdentity);
            DesiredHouseholdLocation location;
            if (householdId == 0 || !_desiredHouseholds.TryGetValue(householdId, out location) ||
                !location.Active || location.Unhoused) return false;
            identity = location.PropertyIdentity;
            return true;
        }

        private bool IsHouseholdDesiredHere(ulong householdId, Entity property)
        {
            PropertyRentIdentity desired, local;
            return TryGetDesiredPropertyIdentity(householdId, out desired) &&
                   TryGetPropertyIdentity(property, out local) && desired.Equals(local);
        }

        private bool TryGetDesiredProperty(ulong householdId, out Entity property)
        {
            property = Entity.Null;
            PropertyRentIdentity identity;
            if (!TryGetDesiredPropertyIdentity(householdId, out identity) ||
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
            DesiredCitizenLocation location;
            return citizenId != 0 && householdId != 0 &&
                   _desiredCitizens.TryGetValue(citizenId, out location) && location.Active &&
                   location.HouseholdId == householdId;
        }

        private bool TryGetDesiredHouseholdId(ulong citizenId, out ulong householdId)
        {
            householdId = 0;
            DesiredCitizenLocation location;
            if (citizenId == 0 || !_desiredCitizens.TryGetValue(citizenId, out location) ||
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
            Entity forward;
            if (_householdsByHostId.TryGetValue(householdId, out forward) && forward == household)
                _householdsByHostId.Remove(householdId);

            ulong reverse;
            if (_hostIdsByHousehold.TryGetValue(household, out reverse) && reverse == householdId)
                _hostIdsByHousehold.Remove(household);
            _arrivalSources.Remove(household);
        }

        private void RemoveCitizenBinding(ulong citizenId, Entity citizen)
        {
            Entity forward;
            if (_citizensByHostId.TryGetValue(citizenId, out forward) && forward == citizen)
                _citizensByHostId.Remove(citizenId);

            ulong reverse;
            if (_hostIdsByCitizen.TryGetValue(citizen, out reverse) && reverse == citizenId)
                _hostIdsByCitizen.Remove(citizen);
        }

        private void ObserveDesiredHousehold(ulong householdId, PropertyRentIdentity property,
            ulong revision, uint sweepId)
        {
            DesiredHouseholdLocation existing;
            if (_desiredHouseholds.TryGetValue(householdId, out existing) &&
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
            DesiredCitizenLocation existing;
            if (_desiredCitizens.TryGetValue(citizenId, out existing) &&
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
            HashSet<ulong> citizens;
            if (!_desiredCitizensByHousehold.TryGetValue(householdId, out citizens))
            {
                citizens = new HashSet<ulong>();
                _desiredCitizensByHousehold[householdId] = citizens;
            }
            citizens.Add(citizenId);
        }

        private void RemoveDesiredCitizenIndex(ulong householdId, ulong citizenId)
        {
            HashSet<ulong> citizens;
            if (householdId == 0 ||
                !_desiredCitizensByHousehold.TryGetValue(householdId, out citizens)) return;
            citizens.Remove(citizenId);
            if (citizens.Count == 0) _desiredCitizensByHousehold.Remove(householdId);
        }

        private void ObserveDepartingHousehold(ulong householdId,
            PropertyRentIdentity property, ulong revision, uint sweepId)
        {
            if (householdId == 0) return;
            DesiredHouseholdLocation existing;
            if (_desiredHouseholds.TryGetValue(householdId, out existing) &&
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
            DesiredHouseholdLocation existing;
            if (_desiredHouseholds.TryGetValue(departure.HouseholdId, out existing) &&
                departure.Revision <= existing.Revision) return;

            Entity property;
            if (existing.PropertyIdentity.PrefabName != null &&
                _propertiesByIdentity.TryGetValue(existing.PropertyIdentity, out property) &&
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
            DesiredHouseholdLocation location;
            return householdId != 0 &&
                   _desiredHouseholds.TryGetValue(householdId, out location) &&
                   location.Active && location.Unhoused;
        }

        private void ObserveCitizenDepartureRecord(OccupancyCitizenDeparture departure,
            uint sweepId)
        {
            if (departure.CitizenId == 0 || departure.Revision == 0) return;
            DesiredCitizenLocation existing;
            if (_desiredCitizens.TryGetValue(departure.CitizenId, out existing) &&
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
            Entity citizen;
            if (TryResolveCitizen(citizenId, out citizen) &&
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

            Entity mappedHousehold;
            if (lastHouseholdId != 0 && TryResolveHousehold(lastHouseholdId, out mappedHousehold) &&
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
            DesiredCitizenLocation existing;
            if (_desiredCitizens.TryGetValue(citizenId, out existing) &&
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
