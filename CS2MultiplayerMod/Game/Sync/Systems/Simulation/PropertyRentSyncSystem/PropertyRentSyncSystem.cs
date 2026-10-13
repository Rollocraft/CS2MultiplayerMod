using System.Collections.Concurrent;
using System.Collections.Generic;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.Infrastructure;
using Game;
using Game.Buildings;
using Game.Common;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Keeps the host's asking rent and non-household rents. Household rent is owned by
    /// ResidentialOccupancySyncSystem. Runs after RentAdjustSystem and before PropertyRenterSystem at
    /// the same interval, so only the partition vanilla just recalculated is walked.
    /// </summary>
    public partial class PropertyRentSyncSystem : GameSystemBase,
        Channels.IPagedPropertyRuntime<PropertyRentSnapshot>
    {
        private readonly PagedPropertySyncState<PropertyRentSnapshot, CachedProperty, PendingProperty, HostObservedRent, PropertyRentEntry>
            _propertyState = new PagedPropertySyncState<PropertyRentSnapshot, CachedProperty, PendingProperty, HostObservedRent, PropertyRentEntry>();
        private const int UpdatePartitions = PropertySyncLimits.UpdatePartitions;
        private const int RentUpdateInterval = 262144 / (16 * UpdatePartitions);
        private const float AnchorMatchDistance = 4f;
        private const float AnchorSearchRadius = 8f;
        private const float AmbiguousDistanceEpsilon = 0.01f;
        private const int MaxIncomingPages = PropertySyncLimits.MaxIncomingPages;
        private const int MaxPumpPages = PropertySyncLimits.MaxPumpPages;
        private const int MaxCachedProperties = PropertySyncLimits.MaxCachedProperties;
        private const int MaxPendingRetriesPerUpdate = 192;
        private const long ResolveRetryMs = PropertySyncLimits.ResolveRetryMs;
        private const long ResolveTimeoutMs = 120000;
        private const int MaxPriorityEntries = PropertySyncLimits.MaxPriorityEntries;
        private const int PriorityEntriesPerPage = 64;
        private const long StatsIntervalMs = PropertySyncLimits.StatsIntervalMs;

        private ConcurrentQueue<PropertyRentSnapshot> _incoming => _propertyState.Incoming;
        private Dictionary<Entity, CachedProperty> _cache => _propertyState.Cache;
        private List<Entity>[] _cacheBuckets => _propertyState.CachedPartitions.Buckets;
        private HashSet<Entity>[] _cacheBucketMembers => _propertyState.CachedPartitions.Members;
        private Dictionary<PropertyIdentity, PendingProperty> _pending => _propertyState.Pending;
        private readonly List<Entity> _cacheScratch = new List<Entity>();

        // Host-side priority: shortens the latency of a changed rent.
        private Dictionary<Entity, HostObservedRent> _hostObserved => _propertyState.HostObserved;
        private List<Entity>[] _hostObservedBuckets => _propertyState.HostPartitions.Buckets;
        private bool[] _hostBucketInitialized => _propertyState.HostPartitions.Initialized;
        private int[] _hostBucketCursor => _propertyState.HostPartitions.Cursor;

        private const int MaxPropertiesObservedPerUpdate = PropertySyncLimits.MaxPropertiesObservedPerUpdate;
        private Dictionary<PropertyIdentity, PropertyRentEntry> _priority => _propertyState.Priority;
        private ConcurrentQueue<PropertyIdentity> _priorityOrder => _propertyState.PriorityOrder;

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
        private long _lastSeededWorldInstallGeneration;
        private bool _clientBaselineWarned;
        private bool _syncWasReady;
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
            public PropertyIdentity Identity;
            public Entity Prefab;
            public int Rent;
            public int Bucket;
            public uint LastSeenSweep;
        }

        private sealed class PendingProperty : PendingPropertyState<PropertyRentEntry>
        { }

        private sealed class HostObservedRent
        {
            public int Rent;
            public int Bucket;
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
                if (service == null || !service.SimulationSyncReady)
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
                    _droppedPages += _propertyState.DropIncoming();
                    ScanHostChanges(bucket);
                    ReportStats(session, service.NowMs);
                    return;
                }

                // Fallback; the city-state pump normally merged these already.
                PumpIncoming();
                ApplyBucket(bucket);
                // RentAdjust also rewrites household contracts; restore channel 21's at this same boundary.
                if (_occupancy != null) _occupancy.CorrectHouseholdRentsAfterRentAdjust(bucket);
                ReportStats(session, service.NowMs);
            }
        }

        /// <summary>Resolves arrived pages. Read-only against ECS; writes stay in OnUpdate.</summary>
        internal void PumpIncoming()
        {
            MultiplayerService service = Mod.Service;
            if (service == null || !service.SimulationSyncReady) return;
            if (service.Session.Role == SessionRole.Host)
            {
                _droppedPages += _propertyState.DropIncoming();
                return;
            }

            // Seed the host rents from the world transfer before a local RentAdjust replaces them.
            long installGeneration = service.WorldInstallGeneration;
            if (installGeneration > _lastSeededWorldInstallGeneration &&
                !SeedClientBaseline(installGeneration)) return;

            long now = service.NowMs;
            bool retryDue = _propertyState.RetryDue(now);
            if (_incoming.IsEmpty && !retryDue) return;

            using (var scope = new PropertySearchScope(_objectSearch))
            {
                DrainIncoming(now, scope.Batch, scope.Candidates, MaxPumpPages);
                if (!retryDue) return;
                RetryPending(now, scope.Batch, scope.Candidates);
                _propertyState.NextPendingPumpMs = now + ResolveRetryMs;
            }
        }
    }
}
