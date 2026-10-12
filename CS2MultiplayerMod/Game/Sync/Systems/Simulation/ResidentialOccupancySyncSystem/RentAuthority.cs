using CS2MultiplayerMod.Game.Sync.Infrastructure;
using System;
using System.Collections.Generic;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Commands;
using Game.Buildings;
using Game.Citizens;
using Game.Common;
using Game.Simulation;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    public partial class ResidentialOccupancySyncSystem
    {
        /// <summary>A save-cut household contract, kept until channel 21 binds the household.</summary>
        private sealed class LoadedWorldHouseholdRent
        {
            public Entity Property;
            public int Rent;
            public int Bucket;
        }

        private readonly Dictionary<Entity, LoadedWorldHouseholdRent> _loadedWorldHouseholdRents =
            new Dictionary<Entity, LoadedWorldHouseholdRent>();
        private readonly PropertyPartitions _loadedRentPartitions = new PropertyPartitions();
        private List<Entity>[] _loadedWorldRentBuckets => _loadedRentPartitions.Buckets;
        private HashSet<Entity>[] _loadedWorldRentBucketMembers => _loadedRentPartitions.Members;

        private long _loadedWorldRentSeedGeneration;
        private bool _loadedWorldRentSeeded;
        private bool _loadedWorldRentSeedWarned;

        /// <summary>
        /// Before RentAdjust: keeps the world transfer's exact contracts until identity pages arrive, once
        /// per installed world.
        /// </summary>
        internal void SeedLoadedWorldHouseholdRents()
        {
            MultiplayerService service = Mod.Service;
            if (service == null || service.Session.Role != SessionRole.Client)
            {
                if (_loadedWorldRentSeeded || _loadedWorldHouseholdRents.Count != 0)
                    ClearRentAuthorityState();
                return;
            }

            long installGeneration = service.WorldInstallGeneration;
            if (installGeneration <= 0 ||
                (_loadedWorldRentSeeded &&
                 installGeneration == _loadedWorldRentSeedGeneration)) return;

            NativeArray<Entity> properties = default(NativeArray<Entity>);
            try
            {
                ClearLoadedWorldRentBaseline();
                properties = _properties.ToEntityArray(Allocator.Temp);
                for (int i = 0; i < properties.Length; i++)
                    SeedPropertyHouseholdRents(properties[i]);

                // Advance only after the whole query; a failed pass retries.
                _loadedWorldRentSeedGeneration = installGeneration;
                _loadedWorldRentSeeded = true;
                _loadedWorldRentSeedWarned = false;
                SyncLog.Detail(LogTopic.Residential, "Occupancy: seeded " +
                    _loadedWorldHouseholdRents.Count +
                    " loaded household rent contract(s) before local RentAdjust.");
            }
            catch (Exception ex)
            {
                ClearLoadedWorldRentBaseline();
                _loadedWorldRentSeeded = false;
                if (!_loadedWorldRentSeedWarned)
                {
                    _loadedWorldRentSeedWarned = true;
                    SyncLog.Warn(LogTopic.Residential,
                        "Occupancy: loaded-world household rent seed failed; " +
                        "will retry (logged once): " + ex.Message);
                }
            }
            finally
            {
                if (properties.IsCreated) properties.Dispose();
            }
        }

        private void SeedPropertyHouseholdRents(Entity property)
        {
            if (!IsLiveProperty(property)) return;

            int bucket = (int)(EntityManager.GetSharedComponent<UpdateFrame>(property).m_Index %
                               UpdatePartitions);
            DynamicBuffer<Renter> renters = EntityManager.GetBuffer<Renter>(property, true);
            for (int i = 0; i < renters.Length; i++)
            {
                Entity household = renters[i].m_Renter;
                // Channel 21's scope: tourist and commuter contracts stay native.
                if (!IsCapturableHousehold(household, property)) continue;

                PropertyRenter rented = EntityManager.GetComponentData<PropertyRenter>(household);
                if (rented.m_Property != property) continue;

                _loadedWorldHouseholdRents[household] = new LoadedWorldHouseholdRent
                {
                    Property = property,
                    Rent = rented.m_Rent,
                    Bucket = bucket,
                };
                AddLoadedWorldRentToBucket(bucket, household);
            }
        }

        /// <summary>
        /// After RentAdjust: rewrites only contracts channel 21 has an identity for, plus save-cut entries
        /// not yet bound.
        /// </summary>
        internal void CorrectHouseholdRentsAfterRentAdjust(int bucket)
        {
            MultiplayerService service = Mod.Service;
            if (service == null || !service.SimulationSyncReady ||
                service.Session.Role != SessionRole.Client ||
                bucket < 0 || bucket >= UpdatePartitions) return;

            CorrectMappedHouseholdRents(bucket);
            CorrectLoadedWorldHouseholdRents(bucket);
        }

        private void CorrectMappedHouseholdRents(int bucket)
        {
            List<Entity> properties = _cacheBuckets[bucket];
            for (int i = 0; i < properties.Count; i++)
            {
                Entity property = properties[i];
                if (!_cache.TryGetValue(property, out CachedProperty cached) || cached.Bucket != bucket) continue;
                // Consumes a valid entry only; roster state is the reconcile's.
                if (!MatchesCachedProperty(property, cached)) continue;

                int currentBucket = (int)(EntityManager
                    .GetSharedComponent<UpdateFrame>(property).m_Index % UpdatePartitions);
                if (currentBucket != cached.Bucket)
                {
                    cached.Bucket = currentBucket;
                    AddToCacheBucket(currentBucket, property);
                    continue;
                }

                OccupancyHousehold[] wanted = cached.Households;
                if (wanted == null) continue;
                for (int h = 0; h < wanted.Length; h++)
                {
                    OccupancyHousehold desired = wanted[h];
                    if (!IsHouseholdDesiredHere(desired.HouseholdId, property)) continue;

                    if (!TryResolveHousehold(desired.HouseholdId, out Entity household) ||
                        !EntityManager.HasComponent<PropertyRenter>(household)) continue;

                    // A bound identity supersedes its save-cut value.
                    ForgetLoadedWorldHouseholdRent(household);

                    PropertyRenter rented = EntityManager.GetComponentData<PropertyRenter>(household);
                    if (rented.m_Property != property ||
                        !HasRenterBufferLink(property, household) ||
                        rented.m_Rent == desired.Rent) continue;
                    rented.m_Rent = desired.Rent;
                    EntityManager.SetComponentData(household, rented);
                }
            }
        }

        private void CorrectLoadedWorldHouseholdRents(int bucket)
        {
            List<Entity> households = _loadedWorldRentBuckets[bucket];
            HashSet<Entity> members = _loadedWorldRentBucketMembers[bucket];
            members.Clear();
            int write = 0;
            for (int i = 0; i < households.Count; i++)
            {
                Entity household = households[i];
                if (!_loadedWorldHouseholdRents.TryGetValue(household, out LoadedWorldHouseholdRent bootstrap) ||
                    bootstrap.Bucket != bucket) continue;

                if (TryGetBoundHouseholdId(household, out ulong boundId))
                {
                    _loadedWorldHouseholdRents.Remove(household);
                    continue;
                }

                if (!IsLiveLoadedWorldHousehold(household) ||
                    !IsLiveProperty(bootstrap.Property) ||
                    !EntityManager.HasComponent<PropertyRenter>(household))
                {
                    _loadedWorldHouseholdRents.Remove(household);
                    continue;
                }

                PropertyRenter rented = EntityManager.GetComponentData<PropertyRenter>(household);
                if (rented.m_Property != bootstrap.Property ||
                    !HasRenterBufferLink(bootstrap.Property, household))
                {
                    _loadedWorldHouseholdRents.Remove(household);
                    continue;
                }

                int currentBucket = (int)(EntityManager
                    .GetSharedComponent<UpdateFrame>(bootstrap.Property).m_Index % UpdatePartitions);
                if (currentBucket != bootstrap.Bucket)
                {
                    bootstrap.Bucket = currentBucket;
                    AddLoadedWorldRentToBucket(currentBucket, household);
                    continue;
                }

                if (!members.Add(household)) continue;
                households[write++] = household;
                if (rented.m_Rent == bootstrap.Rent) continue;
                rented.m_Rent = bootstrap.Rent;
                EntityManager.SetComponentData(household, rented);
            }
            if (write < households.Count)
                households.RemoveRange(write, households.Count - write);
        }

        private void AddLoadedWorldRentToBucket(int bucket, Entity household)
        {
            if (_loadedWorldRentBucketMembers[bucket].Add(household))
                _loadedWorldRentBuckets[bucket].Add(household);
        }

        private bool HasRenterBufferLink(Entity property, Entity household)
        {
            if (property == Entity.Null || !EntityManager.Exists(property) ||
                !EntityManager.HasBuffer<Renter>(property)) return false;
            DynamicBuffer<Renter> renters = EntityManager.GetBuffer<Renter>(property, true);
            for (int i = 0; i < renters.Length; i++)
                if (renters[i].m_Renter == household) return true;
            return false;
        }

        private bool IsLiveLoadedWorldHousehold(Entity household) =>
            household != Entity.Null && EntityManager.Exists(household) &&
            EntityManager.HasComponent<Household>(household) &&
            !EntityManager.HasComponent<Deleted>(household) &&
            !EntityManager.HasComponent<Temp>(household) &&
            !EntityManager.HasComponent<TouristHousehold>(household) &&
            !EntityManager.HasComponent<CommuterHousehold>(household);

        /// <summary>Binding to channel 21 permanently supersedes the save-cut fallback.</summary>
        private void ForgetLoadedWorldHouseholdRent(Entity household)
        {
            if (!_loadedWorldHouseholdRents.TryGetValue(household, out LoadedWorldHouseholdRent bootstrap)) return;
            _loadedWorldHouseholdRents.Remove(household);
            if (bootstrap.Bucket >= 0 && bootstrap.Bucket < UpdatePartitions)
                _loadedWorldRentBucketMembers[bootstrap.Bucket].Remove(household);
        }

        /// <summary>World/session reset hook; called beside the occupancy cache/identity clears.</summary>
        private void ClearRentAuthorityState()
        {
            ClearLoadedWorldRentBaseline();
            _loadedWorldRentSeedGeneration = 0;
            _loadedWorldRentSeeded = false;
            _loadedWorldRentSeedWarned = false;
        }

        private void ClearLoadedWorldRentBaseline()
        {
            _loadedWorldHouseholdRents.Clear();
            ClearBuckets(_loadedWorldRentBuckets);
            ClearBucketSets(_loadedWorldRentBucketMembers);
        }
    }
}
