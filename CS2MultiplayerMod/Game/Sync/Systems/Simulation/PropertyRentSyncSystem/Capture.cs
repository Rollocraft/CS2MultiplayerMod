using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.Infrastructure;
using Game;
using Game.Buildings;
using Game.Common;
using Game.Objects;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    // The host's side: walking one partition per update, noticing the rents that changed, and
    // writing them into a page. Also the page plumbing either peer needs - enqueueing an incoming
    // page, seeding a client's baseline, and clearing everything on a world change.
    public partial class PropertyRentSyncSystem
    {
        /// <summary>Called once per CityState snapshot on the host.</summary>
        internal bool Capture(NetworkWriter writer)
        {
            if (writer == null) return false;
            if (_hostSweepEntities == null)
            {
                NativeArray<Entity> properties = _properties.ToEntityArray(Allocator.Temp);
                try
                {
                    if (properties.Length == 0)
                    {
                        var empty = new PropertyRentSnapshot
                        {
                            SweepId = _captureSweepId,
                            PageIndex = 0,
                            EndOfSweep = true,
                        };
                        int emptyBefore = writer.Length;
                        empty.Write(writer);
                        _sentBytes += writer.Length - emptyBefore;
                        _sentPages++;
                        AdvanceHostSweep();
                        return true;
                    }
                    _hostSweepEntities = new Entity[properties.Length];
                    for (int i = 0; i < properties.Length; i++)
                        _hostSweepEntities[i] = properties[i];
                }
                finally { properties.Dispose(); }
            }
            if (_captureCursor < 0 || _captureCursor >= _hostSweepEntities.Length)
            {
                _hostSweepEntities = null;
                _captureCursor = 0;
                AdvanceHostSweep();
                return Capture(writer);
            }

            var snapshot = new PropertyRentSnapshot
            {
                SweepId = _captureSweepId,
                PageIndex = _capturePageIndex,
            };
            var identities = new HashSet<PropertyRentIdentity>();
            AddPriorityEntries(snapshot, identities);

            int index = _captureCursor;
            while (index < _hostSweepEntities.Length &&
                   snapshot.Entries.Count < PropertyRentSnapshot.MaxEntries)
            {
                PropertyRentEntry entry;
                if (TryCaptureEntry(_hostSweepEntities[index], out entry))
                {
                    if (identities.Add(entry.Identity)) snapshot.Entries.Add(entry);
                    else _localIdentityCollisions++;
                }
                else _localCaptureSkips++;
                index++;
            }

            bool cappedSweep = _capturePageIndex + 1 >=
                               PropertyRentSnapshot.MaxPagesPerSweep;
            snapshot.EndOfSweep = index >= _hostSweepEntities.Length || cappedSweep;
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
            snapshot.Write(writer);
            _sentBytes += writer.Length - before;
            _sentPages++;
            _sentEntries += snapshot.Entries.Count;
            return true;
        }

        /// <summary>Called by the state channel; bounded and deliberately never requests resync.</summary>
        internal void Enqueue(PropertyRentSnapshot snapshot)
        {
            if (snapshot == null) return;
            lock (_incoming)
            {
                _incoming.Enqueue(snapshot);
                while (_incoming.Count > MaxIncomingPages)
                {
                    PropertyRentSnapshot dropped;
                    if (!_incoming.TryDequeue(out dropped)) break;
                    _droppedPages++;
                }
            }
        }

        internal void DrainForWorldChange()
        {
            lock (_incoming) SyncInbox.Clear(_incoming);
            _cache.Clear();
            _pending.Clear();
            PropertyRentIdentity discardedPending;
            while (_pendingOrder.TryDequeue(out discardedPending)) { }
            _cacheScratch.Clear();
            ClearBuckets(_cacheBuckets);
            ClearBucketSets(_cacheBucketMembers);
            _clientSweepId = 0;
            _clientNextPage = 0;
            _clientSweepIntact = false;
            _clientBaselineWarned = false;
            _nextPendingPumpMs = 0;
            _prefabIndex = new PrefabIndex(_prefabSystem, _prefabs);

            _hostObserved.Clear();
            ClearBuckets(_hostObservedBuckets);
            Array.Clear(_hostBucketInitialized, 0, _hostBucketInitialized.Length);
            Array.Clear(_hostBucketCursor, 0, _hostBucketCursor.Length);
            _priority.Clear();
            PropertyRentIdentity discardedPriority;
            while (_priorityOrder.TryDequeue(out discardedPriority)) { }
            RestartHostSweep();
        }

        private bool SeedClientBaseline(long installGeneration)
        {
            NativeArray<Entity> properties = default(NativeArray<Entity>);
            try
            {
                properties = _properties.ToEntityArray(Allocator.Temp);
                int seeded = 0;
                for (int i = 0; i < properties.Length; i++)
                {
                    PropertyRentEntry entry;
                    if (!TryCaptureEntry(properties[i], out entry)) continue;
                    int before = _cache.Count;
                    Cache(properties[i], entry, 0);
                    if (_cache.Count > before) seeded++;
                }
                // Advance only after the complete query was consumed. A failed partial pass keeps
                // the old generation so the next UI pump retries.
                _lastSeededWorldInstallGeneration = installGeneration;
                _clientBaselineWarned = false;
                SyncLog.Detail(LogTopic.Residential, "PropertyRent: seeded " + seeded +
                    " loaded property rent(s) before rolling host correction.");
                return true;
            }
            catch (Exception ex)
            {
                // Leave the flag false: UIUpdate will retry. Partial managed-cache inserts are
                // idempotent and bounded, and this path never writes an ECS component.
                if (!_clientBaselineWarned)
                {
                    _clientBaselineWarned = true;
                    SyncLog.Warn(LogTopic.Residential,
                        "PropertyRent: loaded-world baseline seed failed; " +
                        "will retry (logged once): " + ex.Message);
                }
                return false;
            }
            finally
            {
                if (properties.IsCreated) properties.Dispose();
            }
        }

        private static void ClearBuckets(List<Entity>[] buckets)
        {
            for (int i = 0; i < buckets.Length; i++) buckets[i].Clear();
        }

        private static void ClearBucketSets(HashSet<Entity>[] buckets)
        {
            for (int i = 0; i < buckets.Length; i++) buckets[i].Clear();
        }

        private void RestartHostSweep()
        {
            _hostSweepEntities = null;
            _captureCursor = 0;
            _capturePageIndex = 0;
            _captureSweepId = 1;
        }

        private void AdvanceHostSweep()
        {
            _capturePageIndex = 0;
            _captureSweepId = unchecked(_captureSweepId + 1);
            if (_captureSweepId == 0) _captureSweepId = 1;
        }

        private bool TryCaptureEntry(Entity property, out PropertyRentEntry entry)
        {
            entry = default(PropertyRentEntry);
            if (!IsLiveProperty(property)) return false;

            Entity prefab = EntityManager.GetComponentData<PrefabRef>(property).m_Prefab;
            if (prefab == Entity.Null || !EntityManager.Exists(prefab) ||
                !EntityManager.HasComponent<BuildingPropertyData>(prefab)) return false;
            string prefabName = _prefabIndex.NameOf(prefab);
            if (string.IsNullOrEmpty(prefabName) || prefabName.Length > WireGuard.MaxNameLength)
                return false;

            int rent;
            if (EntityManager.HasComponent<PropertyOnMarket>(property))
            {
                rent = EntityManager.GetComponentData<PropertyOnMarket>(property).m_AskingRent;
            }
            else if (!TryReadRenterRent(property, out rent)) return false;
            if (rent < 0 || rent > PropertyRentSnapshot.MaxRent) return false;

            global::Game.Objects.Transform transform =
                EntityManager.GetComponentData<global::Game.Objects.Transform>(property);
            entry = new PropertyRentEntry
            {
                PrefabName = prefabName,
                AnchorX = transform.m_Position.x,
                AnchorY = transform.m_Position.y,
                AnchorZ = transform.m_Position.z,
                Rent = rent,
            };
            // Do not let an unusable local asset name or corrupt transform reach Snapshot.Write:
            // CityState capture is shared, so a throw here would suppress every state channel.
            return PropertyRentSnapshot.IsValidEntry(entry);
        }

        private bool TryReadRenterRent(Entity property, out int rent)
        {
            DynamicBuffer<Renter> renters = EntityManager.GetBuffer<Renter>(property, true);
            for (int i = 0; i < renters.Length; i++)
            {
                Entity renter = renters[i].m_Renter;
                if (!IsValidRenter(renter, property)) continue;
                rent = EntityManager.GetComponentData<PropertyRenter>(renter).m_Rent;
                return true;
            }
            rent = 0;
            return false;
        }

        private bool IsValidRenter(Entity renter, Entity property) =>
            renter != Entity.Null && EntityManager.Exists(renter) &&
            EntityManager.HasComponent<PropertyRenter>(renter) &&
            !EntityManager.HasComponent<Deleted>(renter) &&
            !EntityManager.HasComponent<Temp>(renter) &&
            EntityManager.GetComponentData<PropertyRenter>(renter).m_Property == property;

        private bool IsLiveProperty(Entity property) =>
            property != Entity.Null && EntityManager.Exists(property) &&
            EntityManager.HasComponent<Building>(property) &&
            EntityManager.HasBuffer<Renter>(property) &&
            EntityManager.HasComponent<PrefabRef>(property) &&
            EntityManager.HasComponent<global::Game.Objects.Transform>(property) &&
            EntityManager.HasComponent<UpdateFrame>(property) &&
            !EntityManager.HasComponent<Temp>(property) &&
            !EntityManager.HasComponent<Deleted>(property) &&
            !EntityManager.HasComponent<Owner>(property) &&
            !EntityManager.HasComponent<StorageProperty>(property);

        private void AddPriorityEntries(PropertyRentSnapshot snapshot,
            HashSet<PropertyRentIdentity> identities)
        {
            int added = 0;
            while (added < PriorityEntriesPerPage && _priorityOrder.Count > 0 &&
                   snapshot.Entries.Count < PropertyRentSnapshot.MaxEntries)
            {
                PropertyRentIdentity identity;
                if (!_priorityOrder.TryDequeue(out identity)) break;
                PropertyRentEntry entry;
                if (!_priority.TryGetValue(identity, out entry)) continue;
                _priority.Remove(identity);
                if (!identities.Add(identity)) continue;
                snapshot.Entries.Add(entry);
                added++;
            }
        }

        /// <summary>
        /// Walks at most <see cref="MaxPropertiesObservedPerUpdate"/> properties of one partition
        /// and resumes where it stopped. Without the ceiling a large city examined thousands of
        /// properties in a single frame each time this system came round.
        /// </summary>
        private void ScanHostChanges(int bucket)
        {
            _properties.SetSharedComponentFilter(new UpdateFrame((uint)bucket));
            NativeArray<Entity> properties = default(NativeArray<Entity>);
            bool wrapped = false;
            try
            {
                properties = _properties.ToEntityArray(Allocator.Temp);
                bool initialized = _hostBucketInitialized[bucket];
                int cursor = _hostBucketCursor[bucket];
                if (cursor >= properties.Length) { cursor = 0; wrapped = true; }
                int examine = properties.Length < MaxPropertiesObservedPerUpdate
                    ? properties.Length : MaxPropertiesObservedPerUpdate;
                for (int i = 0; i < examine; i++)
                {
                    if (cursor >= properties.Length) { cursor = 0; wrapped = true; }
                    Entity property = properties[cursor++];
                    PropertyRentEntry entry;
                    if (!TryCaptureEntry(property, out entry)) continue;
                    HostObservedRent observed;
                    if (!_hostObserved.TryGetValue(property, out observed))
                    {
                        observed = new HostObservedRent { Rent = entry.Rent, Bucket = bucket };
                        _hostObserved[property] = observed;
                        _hostObservedBuckets[bucket].Add(property);
                        if (initialized) Prioritize(entry);
                    }
                    else if (observed.Rent != entry.Rent)
                    {
                        observed.Rent = entry.Rent;
                        if (initialized) Prioritize(entry);
                    }
                }
                if (cursor >= properties.Length) { cursor = 0; wrapped = true; }
                _hostBucketCursor[bucket] = cursor;
                if (wrapped) _hostBucketInitialized[bucket] = true;
            }
            finally
            {
                if (properties.IsCreated) properties.Dispose();
                _properties.ResetFilter();
            }
            if (wrapped) PruneHostObservedBucket(bucket);
        }

        private void Prioritize(PropertyRentEntry entry)
        {
            PropertyRentIdentity identity = entry.Identity;
            if (_priority.ContainsKey(identity))
            {
                _priority[identity] = entry;
                return;
            }
            while (_priority.Count >= MaxPriorityEntries && _priorityOrder.Count > 0)
            {
                PropertyRentIdentity oldest;
                if (!_priorityOrder.TryDequeue(out oldest)) break;
                if (_priority.Remove(oldest)) _priorityDrops++;
            }
            if (_priority.Count >= MaxPriorityEntries)
            {
                _priorityDrops++;
                return;
            }
            _priority[identity] = entry;
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
                HostObservedRent observed;
                if (!_hostObserved.TryGetValue(entity, out observed)) continue;
                if (!IsLiveProperty(entity) || observed.Bucket != bucket ||
                    !EntityManager.HasComponent<UpdateFrame>(entity) ||
                    EntityManager.GetSharedComponent<UpdateFrame>(entity).m_Index != (uint)bucket)
                {
                    _hostObserved.Remove(entity);
                    continue;
                }
                entities[write++] = entity;
            }
            if (write < entities.Count) entities.RemoveRange(write, entities.Count - write);
        }
    }
}
