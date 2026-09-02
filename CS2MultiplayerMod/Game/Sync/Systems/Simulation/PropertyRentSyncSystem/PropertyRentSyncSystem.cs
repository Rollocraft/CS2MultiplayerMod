using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Session;
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
    // State, lifecycle and the per-update cycle. The host's side - sweeping partitions and writing
    // a page - is in Capture.cs; the client's - resolving a page's properties and applying the
    // rents - is in Realize.cs.
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
        private const int PriorityEntriesPerPage = 64;
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
    }
}
