using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Session;
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
    /// <summary>
    /// Keeps the numeric asking rent calculated by the host on properties that already exist
    /// locally, plus rents for non-household tenants. ResidentialOccupancySyncSystem owns each
    /// identified household's actual PropertyRenter value; keeping that single-writer boundary
    /// prevents a property-wide fallback from overwriting different household contracts during
    /// turnover.
    ///
    /// Vanilla recalculates one of sixteen UpdateFrame partitions in RentAdjustSystem. This system
    /// is ordered after that calculation and earlier in the phase than PropertyRenterSystem, and
    /// runs at the same interval, so only the partition vanilla just touched is walked. It corrects
    /// asking rent and company rent that later systems consume. Household lifecycle and household
    /// rent use the identity-aware occupancy channel.
    /// </summary>
    public partial class PropertyRentSyncSystem : GameSystemBase
    {
        private const int UpdatePartitions = 16;
        private const int RentUpdateInterval = 262144 / (16 * UpdatePartitions);
        private const float AnchorMatchDistance = 4f;
        private const float AnchorSearchRadius = 8f;
        private const float AmbiguousDistanceEpsilon = 0.01f;
        private const int MaxIncomingPages = 8;
        private const int MaxPumpPages = 2;
        private const int MaxCachedProperties = 131072;
        private const int MaxPendingIdentities = 4096;
        private const int MaxPendingRetriesPerUpdate = 192;
        private const long ResolveRetryMs = 5000;
        private const long ResolveTimeoutMs = 120000;
        private const int MaxPriorityEntries = 2048;
        private const int PriorityEntriesPerPage = 24;
        private const long StatsIntervalMs = 30000;

        private readonly ConcurrentQueue<PropertyRentSnapshot> _incoming =
            new ConcurrentQueue<PropertyRentSnapshot>();
        private readonly Dictionary<Entity, CachedProperty> _cache =
            new Dictionary<Entity, CachedProperty>();
        private readonly List<Entity>[] _cacheBuckets = CreateBuckets();
        private readonly HashSet<Entity>[] _cacheBucketMembers = CreateBucketSets();
        private readonly Dictionary<PropertyRentIdentity, PendingProperty> _pending =
            new Dictionary<PropertyRentIdentity, PendingProperty>();
        private readonly ConcurrentQueue<PropertyRentIdentity> _pendingOrder =
            new ConcurrentQueue<PropertyRentIdentity>();
        private readonly List<Entity> _cacheScratch = new List<Entity>();

        // Host-side change priority. The rolling baseline is always sent; these entries merely
        // shorten the time from a newly changed rent to the next page that carries it.
        private readonly Dictionary<Entity, HostObservedRent> _hostObserved =
            new Dictionary<Entity, HostObservedRent>();
        private readonly List<Entity>[] _hostObservedBuckets = CreateBuckets();
        private readonly bool[] _hostBucketInitialized = new bool[UpdatePartitions];
        private readonly int[] _hostBucketCursor = new int[UpdatePartitions];

        /// <summary>
        /// Properties the rolling rent observer examines per update. See the same ceiling in
        /// <see cref="ResidentialOccupancySyncSystem"/>: the observer only shortens latency, and a
        /// city large enough to hit this simply takes longer to come all the way round.
        /// </summary>
        private const int MaxPropertiesObservedPerUpdate = 256;
        private readonly Dictionary<PropertyRentIdentity, PropertyRentEntry> _priority =
            new Dictionary<PropertyRentIdentity, PropertyRentEntry>();
        private readonly ConcurrentQueue<PropertyRentIdentity> _priorityOrder =
            new ConcurrentQueue<PropertyRentIdentity>();

        private EntityQuery _properties;
        private EntityQuery _prefabs;
        private Entity[] _hostSweepEntities;
        private PrefabSystem _prefabSystem;
        private PrefabIndex _prefabIndex;
        private ObjectSearch _objectSearch;
        private SimulationSystem _simulationSystem;
        private ResidentialOccupancySyncSystem _occupancy;

        private int _captureCursor;
        private uint _captureSweepId = 1;
        private int _capturePageIndex;
        private uint _clientSweepId;
        private int _clientNextPage;
        private bool _clientSweepIntact;
        private long _lastSeededWorldInstallGeneration;
        private bool _clientBaselineWarned;
        private bool _syncWasReady;
        private long _nextPendingPumpMs;

        private long _lastStatsMs;
        private long _sentBytes;
        private int _sentPages;
        private int _sentEntries;
        private int _priorityChanges;
        private int _priorityDrops;
        private int _localCaptureSkips;
        private int _localIdentityCollisions;
        private int _receivedPages;
        private int _droppedPages;
        private int _resolved;
        private int _unresolved;
        private int _ambiguous;
        private int _expired;
        private int _cacheDrops;
        private int _pruned;
        private int _appliedProperties;
        private int _appliedRenters;
        private int _appliedMarkets;

        private sealed class CachedProperty
        {
            public PropertyRentIdentity Identity;
            public Entity Prefab;
            public int Rent;
            public int Bucket;
            public uint LastSeenSweep;
        }

        private sealed class PendingProperty
        {
            public PropertyRentEntry Entry;
            public uint SweepId;
            public long ExpiresMs;
            public long NextAttemptMs;
        }

        private sealed class HostObservedRent
        {
            public int Rent;
            public int Bucket;
        }

        private static List<Entity>[] CreateBuckets()
        {
            var result = new List<Entity>[UpdatePartitions];
            for (int i = 0; i < result.Length; i++) result[i] = new List<Entity>();
            return result;
        }

        private static HashSet<Entity>[] CreateBucketSets()
        {
            var result = new HashSet<Entity>[UpdatePartitions];
            for (int i = 0; i < result.Length; i++) result[i] = new HashSet<Entity>();
            return result;
        }

        public override int GetUpdateInterval(SystemUpdatePhase phase) =>
            phase == SystemUpdatePhase.GameSimulation ? RentUpdateInterval : 1;

        protected override void OnCreate()
        {
            base.OnCreate();
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _prefabs = GetEntityQuery(ComponentType.ReadOnly<PrefabData>());
            _prefabIndex = new PrefabIndex(_prefabSystem, _prefabs);
            _objectSearch = new ObjectSearch(
                World.GetOrCreateSystemManaged<global::Game.Objects.SearchSystem>());
            _simulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
            _occupancy = World.GetOrCreateSystemManaged<ResidentialOccupancySyncSystem>();
            _properties = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<Building, Renter, PrefabRef,
                    global::Game.Objects.Transform, UpdateFrame>(),
                None = SyncQuery.ReadOnly<Temp, Deleted, Owner, StorageProperty>(),
            });
            SyncInbox.RegisterDrain(DrainForWorldChange);
            Mod.log.Info(nameof(PropertyRentSyncSystem) +
                         " ready (market and non-household rent authority). ");
        }

        protected override void OnDestroy()
        {
            SyncInbox.UnregisterDrain(DrainForWorldChange);
            DrainForWorldChange();
            base.OnDestroy();
        }

        protected override void OnUpdate()
        {
            using (Diagnostics.SyncProfiler.Measure("PropertyRent"))
            {
                MultiplayerService service = Mod.Service;
                if (service == null || !service.GameplaySyncReady)
                {
                    if (_syncWasReady) DrainForWorldChange();
                    _syncWasReady = false;
                    return;
                }
                _syncWasReady = true;

                MultiplayerSession session = service.Session;
                uint updateFrame = SimulationUtils.GetUpdateFrame(
                    _simulationSystem.frameIndex, UpdatePartitions, 16);
                int bucket = (int)(updateFrame % UpdatePartitions);
                if (session.Role == SessionRole.Host)
                {
                    DropIncomingPages();
                    ScanHostChanges(bucket);
                    ReportStats(session, service.NowMs);
                    return;
                }

                // Normally CityState's UIUpdate pump has already merged all pages into the managed
                // cache. Pump once more here as a harmless fallback before this bucket is consumed.
                PumpIncoming();
                ApplyBucket(bucket);
                // RentAdjust also rewrites household contracts. Channel 20 intentionally skips them,
                // so restore each channel-21 identity at this same pre-payment boundary.
                if (_occupancy != null) _occupancy.CorrectHouseholdRentsAfterRentAdjust(bucket);
                ReportStats(session, service.NowMs);
            }
        }

        /// <summary>
        /// Resolve and cache a bounded number of absolute pages from CityState's every-frame pump.
        /// This path performs only reads against ECS; the economic component writes remain solely
        /// in <see cref="OnUpdate"/> at the RentAdjustSystem cadence/order.
        /// </summary>
        internal void PumpIncoming()
        {
            MultiplayerService service = Mod.Service;
            if (service == null || !service.GameplaySyncReady) return;
            if (service.Session.Role == SessionRole.Host)
            {
                DropIncomingPages();
                return;
            }

            // The world transfer already contains the host's rents at its save cut. Seed those
            // values before a local RentAdjust partition can replace them while the 96-entry/s
            // rolling correction is still warming a large city.
            long installGeneration = service.WorldInstallGeneration;
            if (installGeneration > _lastSeededWorldInstallGeneration &&
                !SeedClientBaseline(installGeneration)) return;

            long now = service.NowMs;
            bool retryDue = _pending.Count > 0 && now >= _nextPendingPumpMs;
            if (_incoming.IsEmpty && !retryDue) return;

            ObjectSearch.Batch search = _objectSearch.BeginBatch();
            var candidates = new NativeList<Entity>(16, Allocator.Temp);
            try
            {
                DrainIncoming(now, search, candidates, MaxPumpPages);
                if (retryDue)
                {
                    RetryPending(now, search, candidates);
                    _nextPendingPumpMs = now + ResolveRetryMs;
                }
            }
            finally
            {
                candidates.Dispose();
            }
        }

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
                Mod.Verbose("[MP] PropertyRent: seeded " + seeded +
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
                    Mod.log.Warn("[MP] PropertyRent: loaded-world baseline seed failed; " +
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

        private void DropIncomingPages()
        {
            if (_incoming.IsEmpty) return;
            lock (_incoming)
            {
                PropertyRentSnapshot ignored;
                while (_incoming.TryDequeue(out ignored)) _droppedPages++;
            }
        }

        private void DrainIncoming(long now, ObjectSearch.Batch search,
            NativeList<Entity> candidates, int maxPages)
        {
            PropertyRentSnapshot snapshot;
            int pages = 0;
            while (pages < maxPages && _incoming.TryDequeue(out snapshot))
            {
                pages++;
                _receivedPages++;
                NotePageContinuity(snapshot);
                for (int i = 0; i < snapshot.Entries.Count; i++)
                    ResolveOrPend(snapshot.Entries[i], snapshot.SweepId, now, search, candidates);
                if (snapshot.EndOfSweep && _clientSweepIntact &&
                    snapshot.SweepId == _clientSweepId &&
                    snapshot.PageIndex + 1 == _clientNextPage)
                    PruneCacheAfterCompleteSweep(snapshot.SweepId);
            }
        }

        private void NotePageContinuity(PropertyRentSnapshot snapshot)
        {
            if (snapshot.SweepId != _clientSweepId)
            {
                _clientSweepId = snapshot.SweepId;
                _clientNextPage = 0;
                _clientSweepIntact = snapshot.PageIndex == 0;
            }
            if (snapshot.PageIndex != _clientNextPage) _clientSweepIntact = false;
            if (snapshot.PageIndex >= _clientNextPage)
                _clientNextPage = snapshot.PageIndex + 1;
        }

        private void ResolveOrPend(PropertyRentEntry entry, uint sweepId, long now,
            ObjectSearch.Batch search, NativeList<Entity> candidates)
        {
            bool ambiguous;
            Entity property = ResolveProperty(entry, search, candidates, out ambiguous);
            if (property != Entity.Null)
            {
                Cache(property, entry, sweepId);
                _pending.Remove(entry.Identity);
                _resolved++;
                return;
            }
            if (ambiguous) _ambiguous++;
            else _unresolved++;

            PropertyRentIdentity identity = entry.Identity;
            PendingProperty pending;
            if (_pending.TryGetValue(identity, out pending))
            {
                pending.Entry = entry;
                pending.SweepId = sweepId;
                return;
            }
            if (_pending.Count >= MaxPendingIdentities)
            {
                _cacheDrops++;
                return;
            }
            _pending[identity] = new PendingProperty
            {
                Entry = entry,
                SweepId = sweepId,
                ExpiresMs = now + ResolveTimeoutMs,
                NextAttemptMs = now + ResolveRetryMs,
            };
            _pendingOrder.Enqueue(identity);
            long firstRetry = now + ResolveRetryMs;
            if (_nextPendingPumpMs == 0 || firstRetry < _nextPendingPumpMs)
                _nextPendingPumpMs = firstRetry;
        }

        private void RetryPending(long now, ObjectSearch.Batch search,
            NativeList<Entity> candidates)
        {
            if (_pending.Count == 0) return;
            int examined = 0;
            PropertyRentIdentity identity;
            while (examined++ < MaxPendingRetriesPerUpdate &&
                   _pendingOrder.TryDequeue(out identity))
            {
                PendingProperty pending;
                if (!_pending.TryGetValue(identity, out pending)) continue;
                if (pending.ExpiresMs <= now)
                {
                    _pending.Remove(identity);
                    _expired++;
                    continue;
                }
                if (pending.NextAttemptMs > now)
                {
                    _pendingOrder.Enqueue(identity);
                    continue;
                }
                bool ambiguous;
                Entity property = ResolveProperty(pending.Entry, search, candidates, out ambiguous);
                if (property != Entity.Null)
                {
                    Cache(property, pending.Entry, pending.SweepId);
                    _pending.Remove(identity);
                    _resolved++;
                }
                else
                {
                    pending.NextAttemptMs = now + ResolveRetryMs;
                    _pendingOrder.Enqueue(identity);
                }
            }
        }

        private Entity ResolveProperty(PropertyRentEntry entry, ObjectSearch.Batch search,
            NativeList<Entity> candidates, out bool ambiguous)
        {
            ambiguous = false;
            Entity prefab;
            _prefabIndex.TryResolve(entry.PrefabName,
                candidate => EntityManager.HasComponent<BuildingPropertyData>(candidate),
                out prefab);

            float3 anchor = new float3(entry.AnchorX, entry.AnchorY, entry.AnchorZ);
            search.CollectNear(anchor, AnchorSearchRadius, candidates);
            Entity exact = Entity.Null, nearest = Entity.Null;
            float exactDistance = 0f, nearestDistance = 0f;
            bool exactAmbiguous = false, nearestAmbiguous = false;
            for (int i = 0; i < candidates.Length; i++)
            {
                Entity candidate = candidates[i];
                if (!IsLiveProperty(candidate)) continue;
                float distance = math.distancesq(
                    EntityManager.GetComponentData<global::Game.Objects.Transform>(candidate)
                        .m_Position.xz, anchor.xz);
                if (distance > AnchorMatchDistance * AnchorMatchDistance) continue;
                if (prefab != Entity.Null &&
                    EntityManager.GetComponentData<PrefabRef>(candidate).m_Prefab == prefab)
                    ConsiderRentCandidate(candidate, distance, ref exact, ref exactDistance,
                        ref exactAmbiguous);
                ConsiderRentCandidate(candidate, distance, ref nearest, ref nearestDistance,
                    ref nearestAmbiguous);
            }
            if (exact != Entity.Null && !exactAmbiguous) return exact;
            ambiguous = exact != Entity.Null ? exactAmbiguous : nearestAmbiguous;
            return nearest != Entity.Null && !nearestAmbiguous ? nearest : Entity.Null;
        }

        private static void ConsiderRentCandidate(Entity candidate, float distance, ref Entity best,
            ref float bestDistance, ref bool ambiguous)
        {
            if (best == Entity.Null || distance < bestDistance - AmbiguousDistanceEpsilon)
            {
                best = candidate;
                bestDistance = distance;
                ambiguous = false;
                return;
            }
            if (math.abs(distance - bestDistance) <= AmbiguousDistanceEpsilon && candidate != best)
                ambiguous = true;
        }

        private void Cache(Entity property, PropertyRentEntry entry, uint sweepId)
        {
            int bucket = (int)(EntityManager.GetSharedComponent<UpdateFrame>(property).m_Index %
                               UpdatePartitions);
            CachedProperty cached;
            if (_cache.TryGetValue(property, out cached))
            {
                cached.Identity = entry.Identity;
                cached.Prefab = EntityManager.GetComponentData<PrefabRef>(property).m_Prefab;
                cached.Rent = entry.Rent;
                cached.Bucket = bucket;
                cached.LastSeenSweep = sweepId;
                AddToCacheBucket(bucket, property);
                return;
            }
            if (_cache.Count >= MaxCachedProperties)
            {
                _cacheDrops++;
                return;
            }
            _cache[property] = new CachedProperty
            {
                Identity = entry.Identity,
                Prefab = EntityManager.GetComponentData<PrefabRef>(property).m_Prefab,
                Rent = entry.Rent,
                Bucket = bucket,
                LastSeenSweep = sweepId,
            };
            AddToCacheBucket(bucket, property);
        }

        private void AddToCacheBucket(int bucket, Entity property)
        {
            if (_cacheBucketMembers[bucket].Add(property))
                _cacheBuckets[bucket].Add(property);
        }

        private void PruneCacheAfterCompleteSweep(uint sweepId)
        {
            _cacheScratch.Clear();
            foreach (KeyValuePair<Entity, CachedProperty> pair in _cache)
                if (pair.Value.LastSeenSweep != sweepId) _cacheScratch.Add(pair.Key);
            for (int i = 0; i < _cacheScratch.Count; i++)
                if (_cache.Remove(_cacheScratch[i])) _pruned++;
            _cacheScratch.Clear();
        }

        private void ApplyBucket(int bucket)
        {
            List<Entity> entities = _cacheBuckets[bucket];
            HashSet<Entity> members = _cacheBucketMembers[bucket];
            // Rebuild membership while compacting. This removes complete-sweep tombstones and any
            // legacy duplicate list entries, and preserves exactly one slot for each live cache.
            members.Clear();
            int write = 0;
            for (int i = 0; i < entities.Count; i++)
            {
                Entity property = entities[i];
                CachedProperty cached;
                if (!_cache.TryGetValue(property, out cached))
                {
                    continue;
                }
                // A stale entry may remain in its old bucket list after a local UpdateFrame move.
                // Do not delete the live cache now owned by the new bucket.
                if (cached.Bucket != bucket) continue;
                if (!MatchesCachedProperty(property, cached))
                {
                    _cache.Remove(property);
                    continue;
                }

                int currentBucket = (int)(EntityManager
                    .GetSharedComponent<UpdateFrame>(property).m_Index % UpdatePartitions);
                if (currentBucket != cached.Bucket)
                {
                    cached.Bucket = currentBucket;
                    AddToCacheBucket(currentBucket, property);
                    continue;
                }

                if (!members.Add(property)) continue;

                bool wrote = false;

                if (EntityManager.HasComponent<PropertyOnMarket>(property))
                {
                    PropertyOnMarket market =
                        EntityManager.GetComponentData<PropertyOnMarket>(property);
                    if (market.m_AskingRent != cached.Rent)
                    {
                        market.m_AskingRent = cached.Rent;
                        EntityManager.SetComponentData(property, market);
                        _appliedMarkets++;
                        wrote = true;
                    }
                }

                DynamicBuffer<Renter> renters = EntityManager.GetBuffer<Renter>(property, true);
                for (int j = 0; j < renters.Length; j++)
                {
                    Entity renter = renters[j].m_Renter;
                    if (!IsValidRenter(renter, property)) continue;
                    if (EntityManager.HasComponent<global::Game.Citizens.Household>(renter))
                        continue;
                    PropertyRenter propertyRenter =
                        EntityManager.GetComponentData<PropertyRenter>(renter);
                    if (propertyRenter.m_Rent == cached.Rent) continue;
                    propertyRenter.m_Rent = cached.Rent;
                    EntityManager.SetComponentData(renter, propertyRenter);
                    _appliedRenters++;
                    wrote = true;
                }
                if (wrote) _appliedProperties++;
                entities[write++] = property;
            }
            if (write < entities.Count) entities.RemoveRange(write, entities.Count - write);
        }

        private bool MatchesCachedProperty(Entity property, CachedProperty cached)
        {
            if (!IsLiveProperty(property)) return false;
            cached.Prefab = EntityManager.GetComponentData<PrefabRef>(property).m_Prefab;
            float3 position = EntityManager
                .GetComponentData<global::Game.Objects.Transform>(property).m_Position;
            float2 anchor = new float2(cached.Identity.AnchorX, cached.Identity.AnchorZ);
            return math.distancesq(position.xz, anchor) <=
                   AnchorMatchDistance * AnchorMatchDistance;
        }

        private void ReportStats(MultiplayerSession session, long now)
        {
            if (_lastStatsMs == 0) { _lastStatsMs = now; return; }
            if (now - _lastStatsMs < StatsIntervalMs) return;
            _lastStatsMs = now;

            if (session.Role == SessionRole.Host)
            {
                int clients = 0;
                foreach (Peer peer in session.Peers)
                    if (peer.Handshaked) clients++;
                long estimatedFanoutBytes = _sentBytes * clients;
                Mod.Verbose("[MP] PropertyRent/30s host: pages=" + _sentPages +
                            ", entries=" + _sentEntries + ", bytes=" + _sentBytes +
                            ", clients=" + clients + ", estimatedFanoutBytes=" +
                            estimatedFanoutBytes + ", transportPendingBytes=" +
                            session.PendingSendBytes +
                            ", changedPriority=" + _priorityChanges +
                            ", priorityQueued=" + _priority.Count +
                            ", priorityDropped=" + _priorityDrops +
                            ", captureSkipped=" + _localCaptureSkips +
                            ", identityCollision=" + _localIdentityCollisions + ".");
            }
            else
            {
                Mod.Verbose("[MP] PropertyRent/30s client: pages=" + _receivedPages +
                            ", queueDropped=" + _droppedPages + ", cached=" + _cache.Count +
                            ", pending=" + _pending.Count + ", resolved=" + _resolved +
                            ", unresolved=" + _unresolved + ", ambiguous=" + _ambiguous +
                            ", expired=" + _expired + ", cacheDropped=" + _cacheDrops +
                            ", pruned=" + _pruned + ", appliedProperties=" +
                            _appliedProperties + ", renterWrites=" + _appliedRenters +
                            ", marketWrites=" + _appliedMarkets + ".");
            }
            _sentPages = _sentEntries = _priorityChanges = _priorityDrops = 0;
            _localCaptureSkips = _localIdentityCollisions = 0;
            _sentBytes = 0;
            _receivedPages = _droppedPages = _resolved = _unresolved = _ambiguous = 0;
            _expired = _cacheDrops = _pruned = 0;
            _appliedProperties = _appliedRenters = _appliedMarkets = 0;
        }
    }
}
