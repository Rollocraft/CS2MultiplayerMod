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
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Makes the host the only author of who lives in a residential building.
    ///
    /// Every page the host sends is an absolute roster for the properties it names: the households
    /// in the building, the people in them, their money and their rent. A client resolves each
    /// property by the same portable identity the rest of the mod uses (prefab name plus world
    /// anchor) and then makes its own building match — creating or retiring households, adding or
    /// removing residents, and overwriting the numbers.
    ///
    /// Two things make this affordable where a per-entity mirror of the whole citizen simulation
    /// would not be:
    ///
    /// * Household income is a function of each resident's age, whether they hold a job, and the
    ///   wage bracket of that job. It does not depend on which company employs them, so matching
    ///   the residents matches the income without replicating employment at all.
    /// * Occupancy identity is (property, slot), not a global household id. Nothing has to be
    ///   bootstrapped at join and nothing accumulates: a page that is late, lost or applied twice
    ///   converges to the same state.
    ///
    /// The client stops authoring occupancy while this runs — see Authority.cs.
    /// </summary>
    public partial class ResidentialOccupancySyncSystem : GameSystemBase
    {
        private const int UpdatePartitions = 16;

        /// <summary>
        /// Simulation frames between updates. Must be a power of two: the game gates a system's
        /// update with <c>frameIndex &amp; (interval - 1)</c>. One of sixteen partitions is examined
        /// per update, so the whole city is revisited every 1024 simulation frames.
        /// </summary>
        private const int UpdateIntervalFrames = 64;

        private const float AnchorMatchDistance = 4f;
        private const float AnchorSearchRadius = 8f;
        private const float AmbiguousDistanceEpsilon = 0.01f;

        /// <summary>
        /// Soft byte budget for one page. Pages go out at the city-state cadence (~1 Hz), so this
        /// is also roughly the per-client bandwidth this feature costs.
        /// </summary>
        private const int PageByteBudget = 4096;

        private const int MaxIncomingPages = 8;
        private const int MaxPumpPages = 2;
        private const int MaxCachedProperties = 131072;
        private const int MaxPendingIdentities = 4096;
        private const int MaxPendingRetriesPerPump = 128;
        private const long ResolveRetryMs = 5000;
        private const long ResolveTimeoutMs = 300000;
        private const int MaxPriorityProperties = 4096;
        private const int PriorityPropertiesPerPage = 16;

        // Per-update work ceilings. Structural changes are the expensive part, so they are capped
        // well below the page rate; anything left over is picked up by the next update.
        private const int MaxPropertiesAppliedPerUpdate = 96;
        private const int MaxHouseholdsCreatedPerUpdate = 12;
        private const int MaxCitizensCreatedPerUpdate = 48;
        private const int MaxHouseholdsRetiredPerUpdate = 12;

        private const long StatsIntervalMs = 30000;

        private readonly ConcurrentQueue<ResidentialOccupancySnapshot> _incoming =
            new ConcurrentQueue<ResidentialOccupancySnapshot>();
        private readonly Dictionary<Entity, CachedProperty> _cache =
            new Dictionary<Entity, CachedProperty>();
        private readonly List<Entity>[] _cacheBuckets = CreateBuckets();
        private readonly HashSet<Entity>[] _cacheBucketMembers = CreateBucketSets();
        private readonly List<Entity> _dirty = new List<Entity>();
        private readonly HashSet<Entity> _dirtyMembers = new HashSet<Entity>();
        private readonly Dictionary<PropertyRentIdentity, PendingProperty> _pending =
            new Dictionary<PropertyRentIdentity, PendingProperty>();
        private readonly ConcurrentQueue<PropertyRentIdentity> _pendingOrder =
            new ConcurrentQueue<PropertyRentIdentity>();

        // Host-side change detection. The rolling baseline is always sent; these entries only
        // shorten the time from an occupancy change to the page that carries it.
        private readonly Dictionary<Entity, HostObserved> _hostObserved =
            new Dictionary<Entity, HostObserved>();
        private readonly List<Entity>[] _hostObservedBuckets = CreateBuckets();
        private readonly bool[] _hostBucketInitialized = new bool[UpdatePartitions];
        private readonly Dictionary<PropertyRentIdentity, OccupancyProperty> _priority =
            new Dictionary<PropertyRentIdentity, OccupancyProperty>();
        private readonly ConcurrentQueue<PropertyRentIdentity> _priorityOrder =
            new ConcurrentQueue<PropertyRentIdentity>();

        private EntityQuery _properties;
        private EntityQuery _unreachableHouseholds;
        private EntityQuery _prefabs;
        private Entity[] _hostSweepEntities;
        private PrefabSystem _prefabSystem;
        private PrefabIndex _prefabIndex;
        private ObjectSearch _objectSearch;
        private SimulationSystem _simulationSystem;
        private PropertyProcessingSystem _propertyProcessing;

        private int _captureCursor;
        private uint _captureSweepId = 1;
        private int _capturePageIndex;
        private bool _syncWasReady;
        private long _nextPendingPumpMs;

        private long _lastStatsMs;
        private long _sentBytes;
        private int _sentPages;
        private int _sentProperties;
        private int _priorityChanges;
        private int _priorityDrops;
        private int _captureSkips;
        private int _receivedPages;
        private int _droppedPages;
        private int _resolved;
        private int _unresolved;
        private int _ambiguous;
        private int _expired;
        private int _cacheDrops;
        private int _appliedProperties;
        private int _createdHouseholds;
        private int _createdCitizens;
        private int _createdPets;
        private int _retiredHouseholds;
        private int _removedCitizens;
        private int _rewrittenCitizens;
        private int _rentActions;
        private int _refusedMoveIns;
        private int _forcedCompletions;
        private int _alignedBuildRates;
        private int _deferredForConstruction;
        private int _renamedEntities;

        private sealed class CachedProperty
        {
            public PropertyRentIdentity Identity;
            public Entity Prefab;
            public byte ConstructionSpeed;
            public OccupancyHousehold[] Households;
            public int Bucket;
        }

        private sealed class PendingProperty
        {
            public OccupancyProperty Property;
            public long ExpiresMs;
            public long NextAttemptMs;
        }

        private sealed class HostObserved
        {
            public int Hash;
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
            phase == SystemUpdatePhase.GameSimulation ? UpdateIntervalFrames : 1;

        protected override void OnCreate()
        {
            base.OnCreate();
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _prefabs = GetEntityQuery(ComponentType.ReadOnly<PrefabData>());
            _prefabIndex = new PrefabIndex(_prefabSystem, _prefabs);
            _objectSearch = new ObjectSearch(
                World.GetOrCreateSystemManaged<global::Game.Objects.SearchSystem>());
            _simulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
            _propertyProcessing = World.GetOrCreateSystemManaged<PropertyProcessingSystem>();
            _properties = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Building>(),
                    ComponentType.ReadOnly<ResidentialProperty>(),
                    ComponentType.ReadOnly<Renter>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<global::Game.Objects.Transform>(),
                    ComponentType.ReadOnly<UpdateFrame>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Owner>(),
                },
            });
            // Households nothing can ever house again on a client. Tourists and commuters are a
            // different simulation with their own lifecycle and are never ours to retire; a
            // household still carrying CurrentBuilding is mid-arrival and has not asked for a home
            // yet.
            _unreachableHouseholds = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<global::Game.Citizens.Household>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<PropertyRenter>(),
                    ComponentType.ReadOnly<global::Game.Citizens.HomelessHousehold>(),
                    ComponentType.ReadOnly<global::Game.Agents.MovingAway>(),
                    ComponentType.ReadOnly<global::Game.Citizens.CurrentBuilding>(),
                    ComponentType.ReadOnly<global::Game.Citizens.TouristHousehold>(),
                    ComponentType.ReadOnly<global::Game.Citizens.CommuterHousehold>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>(),
                },
            });
            SyncInbox.RegisterDrain(DrainForWorldChange);
            Mod.log.Info(nameof(ResidentialOccupancySyncSystem) +
                         " ready (host-authoritative residential occupancy).");
        }

        protected override void OnDestroy()
        {
            SyncInbox.UnregisterDrain(DrainForWorldChange);
            RestoreLocalAuthority();
            DrainForWorldChange();
            base.OnDestroy();
        }

        protected override void OnUpdate()
        {
            MultiplayerService service = Mod.Service;
            if (service == null || !service.GameplaySyncReady)
            {
                if (_syncWasReady)
                {
                    RestoreLocalAuthority();
                    DrainForWorldChange();
                }
                _syncWasReady = false;
                return;
            }
            _syncWasReady = true;

            MultiplayerSession session = service.Session;
            ApplyLocalAuthority(session);

            int bucket = (int)(SimulationUtils.GetUpdateFrameWithInterval(
                _simulationSystem.frameIndex, UpdateIntervalFrames, UpdatePartitions) %
                UpdatePartitions);

            if (session.Role == SessionRole.Host)
            {
                DropIncomingPages();
                ScanHostChanges(bucket);
            }
            else
            {
                // Normally the city-state pump has already turned every arrived page into cache
                // entries. Pump once more as a harmless fallback before this bucket is consumed.
                PumpIncoming();
                ApplyPending(bucket);
            }
            ReportStats(session, service.NowMs);
        }

        /// <summary>
        /// Kept engaged from the city-state pump as well as from <see cref="OnUpdate"/>. The
        /// GameSimulation phase stops ticking the moment a player pauses, so a client that leaves
        /// a session while paused would otherwise keep its household systems held forever.
        /// </summary>
        internal void MaintainAuthority()
        {
            MultiplayerService service = Mod.Service;
            if (service == null || !service.GameplaySyncReady)
            {
                RestoreLocalAuthority();
                return;
            }
            ApplyLocalAuthority(service.Session);
        }

        /// <summary>
        /// The channel's reset. Called both when a session ends and on an in-session world
        /// replacement, so authority is only handed back in the first case.
        /// </summary>
        internal void ResetPending()
        {
            DrainForWorldChange();
            MultiplayerService service = Mod.Service;
            if (service == null || !service.GameplaySyncReady) RestoreLocalAuthority();
        }

        /// <summary>Called by the state channel on the receiving side; never requests a resync.</summary>
        internal void Enqueue(ResidentialOccupancySnapshot snapshot)
        {
            if (snapshot == null) return;
            lock (_incoming)
            {
                _incoming.Enqueue(snapshot);
                while (_incoming.Count > MaxIncomingPages)
                {
                    ResidentialOccupancySnapshot dropped;
                    if (!_incoming.TryDequeue(out dropped)) break;
                    _droppedPages++;
                }
            }
        }

        internal void DrainForWorldChange()
        {
            lock (_incoming) SyncInbox.Clear(_incoming);
            _cache.Clear();
            ClearBuckets(_cacheBuckets);
            ClearBucketSets(_cacheBucketMembers);
            _dirty.Clear();
            _dirtyMembers.Clear();
            _pending.Clear();
            PropertyRentIdentity discardedPending;
            while (_pendingOrder.TryDequeue(out discardedPending)) { }
            _settling.Clear();
            _unreachableSince.Clear();
            _unreachableSeen.Clear();
            _localHouseholds.Clear();
            _memberScratch.Clear();
            _settlingScratch.Clear();
            _appliedThisUpdate.Clear();
            _reapply.Clear();
            _applyWarned = false;
            _nextPendingPumpMs = 0;
            _prefabIndex = new PrefabIndex(_prefabSystem, _prefabs);

            _hostObserved.Clear();
            ClearBuckets(_hostObservedBuckets);
            Array.Clear(_hostBucketInitialized, 0, _hostBucketInitialized.Length);
            _priority.Clear();
            PropertyRentIdentity discardedPriority;
            while (_priorityOrder.TryDequeue(out discardedPriority)) { }
            RestartHostSweep();
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

        private void DropIncomingPages()
        {
            if (_incoming.IsEmpty) return;
            lock (_incoming)
            {
                ResidentialOccupancySnapshot ignored;
                while (_incoming.TryDequeue(out ignored)) _droppedPages++;
            }
        }

        private bool IsLiveProperty(Entity property) =>
            property != Entity.Null && EntityManager.Exists(property) &&
            EntityManager.HasComponent<Building>(property) &&
            EntityManager.HasComponent<ResidentialProperty>(property) &&
            EntityManager.HasBuffer<Renter>(property) &&
            EntityManager.HasComponent<PrefabRef>(property) &&
            EntityManager.HasComponent<global::Game.Objects.Transform>(property) &&
            EntityManager.HasComponent<UpdateFrame>(property) &&
            !EntityManager.HasComponent<Temp>(property) &&
            !EntityManager.HasComponent<Deleted>(property) &&
            !EntityManager.HasComponent<Owner>(property);

        private void ReportStats(MultiplayerSession session, long now)
        {
            if (_lastStatsMs == 0) { _lastStatsMs = now; return; }
            if (now - _lastStatsMs < StatsIntervalMs) return;
            _lastStatsMs = now;

            if (session.Role == SessionRole.Host)
            {
                int clients = 0;
                foreach (Peer peer in session.Peers) if (peer.Handshaked) clients++;
                Mod.Verbose("[MP] Occupancy/30s host: pages=" + _sentPages + ", properties=" +
                            _sentProperties + ", bytes=" + _sentBytes + ", clients=" + clients +
                            ", estimatedFanoutBytes=" + _sentBytes * clients +
                            ", transportPendingBytes=" + session.PendingSendBytes +
                            ", changedPriority=" + _priorityChanges + ", priorityQueued=" +
                            _priority.Count + ", priorityDropped=" + _priorityDrops +
                            ", captureSkipped=" + _captureSkips + ".");
            }
            else
            {
                Mod.Verbose("[MP] Occupancy/30s client: pages=" + _receivedPages +
                            ", queueDropped=" + _droppedPages + ", cached=" + _cache.Count +
                            ", pending=" + _pending.Count + ", resolved=" + _resolved +
                            ", unresolved=" + _unresolved + ", ambiguous=" + _ambiguous +
                            ", expired=" + _expired + ", cacheDropped=" + _cacheDrops +
                            ", appliedProperties=" + _appliedProperties + ", households +" +
                            _createdHouseholds + "/-" + _retiredHouseholds + ", citizens +" +
                            _createdCitizens + "/-" + _removedCitizens + "/~" +
                            _rewrittenCitizens + ", pets +" + _createdPets + ", renamed=" +
                            _renamedEntities + ", rentActions=" + _rentActions +
                            ", refusedMoveIns=" + _refusedMoveIns + ", buildRatesAligned=" +
                            _alignedBuildRates + ", forcedCompletions=" + _forcedCompletions +
                            ", deferredForConstruction=" + _deferredForConstruction + ".");
            }
            _sentPages = _sentProperties = _priorityChanges = _priorityDrops = _captureSkips = 0;
            _sentBytes = 0;
            _receivedPages = _droppedPages = _resolved = _unresolved = _ambiguous = 0;
            _expired = _cacheDrops = _appliedProperties = 0;
            _createdHouseholds = _createdCitizens = _createdPets = 0;
            _retiredHouseholds = _removedCitizens = _rewrittenCitizens = 0;
            _rentActions = _refusedMoveIns = 0;
            _forcedCompletions = _alignedBuildRates = _deferredForConstruction = 0;
            _renamedEntities = 0;
        }
    }
}
