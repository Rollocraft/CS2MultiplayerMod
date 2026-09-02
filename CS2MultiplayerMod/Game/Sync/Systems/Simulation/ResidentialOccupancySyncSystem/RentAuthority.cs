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
        /// <summary>
        /// One exact household contract already present in the downloaded world. Channel 21
        /// replaces this bootstrap as soon as it binds the host household identity; until then it
        /// prevents the client's first RentAdjust pass from erasing the save-cut value.
        /// </summary>
        private sealed class LoadedWorldHouseholdRent
        {
            public Entity Property;
            public int Rent;
            public int Bucket;
        }

        private readonly Dictionary<Entity, LoadedWorldHouseholdRent> _loadedWorldHouseholdRents =
            new Dictionary<Entity, LoadedWorldHouseholdRent>();
        private readonly List<Entity>[] _loadedWorldRentBuckets = CreateBuckets();
        private readonly HashSet<Entity>[] _loadedWorldRentBucketMembers = CreateBucketSets();

        private long _loadedWorldRentSeedGeneration;
        private bool _loadedWorldRentSeeded;
        private bool _loadedWorldRentSeedWarned;

        /// <summary>
        /// Called from the small pre-RentAdjust ordering system. The world transfer already carries
        /// every household's exact PropertyRenter contract, but host identity pages arrive on a
        /// rolling schedule. Preserve those local save-cut contracts once per installed world so
        /// the first native rent update has an identity-safe value to restore afterwards.
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

                // Advance only after the complete query was consumed. A failed partial pass is
                // cleared below and retried before the next RentAdjust update.
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
                // Match channel 21's scope exactly. Tourist and commuter household contracts
                // remain native and must not be frozen at the downloaded save cut.
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
        /// Runs at PropertyRentSyncSystem's existing post-RentAdjust boundary. Native rent
        /// calculation remains enabled for all of its property maintenance side effects; this
        /// method changes only the household contracts for which channel 21 has an exact identity,
        /// plus the short-lived loaded-world bootstrap entries that precede identity binding.
        /// </summary>
        internal void CorrectHouseholdRentsAfterRentAdjust(int bucket)
        {
            MultiplayerService service = Mod.Service;
            if (service == null || !service.GameplaySyncReady ||
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
                CachedProperty cached;
                if (!_cache.TryGetValue(property, out cached) || cached.Bucket != bucket) continue;
                // Cache ownership/pruning belongs to the normal occupancy reconcile. This narrow
                // boundary only consumes a still-valid entry and never changes roster state.
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

                    Entity household;
                    if (!TryResolveHousehold(desired.HouseholdId, out household) ||
                        !EntityManager.HasComponent<PropertyRenter>(household)) continue;

                    // A bound identity has superseded its save-cut bootstrap even when no write is
                    // needed. Keeping that old value could otherwise resurrect it after turnover.
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
                LoadedWorldHouseholdRent bootstrap;
                if (!_loadedWorldHouseholdRents.TryGetValue(household, out bootstrap) ||
                    bootstrap.Bucket != bucket) continue;

                ulong boundId;
                if (TryGetBoundHouseholdId(household, out boundId))
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
            LoadedWorldHouseholdRent bootstrap;
            if (!_loadedWorldHouseholdRents.TryGetValue(household, out bootstrap)) return;
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
