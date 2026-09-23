using CS2MultiplayerMod.Core.Sync;
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
    /// Makes the host the only author of who lives in residential buildings. Pages are absolute,
    /// revisioned rosters resolved by prefab name plus anchor. Host entity handles travel only as
    /// opaque session-scoped keys, never as local handles. Authority.cs lists what a client stops doing.
    /// </summary>
    public partial class ResidentialOccupancySyncSystem : GameSystemBase,
        Channels.IPagedPropertyRuntime<ResidentialOccupancySnapshot>
    {
        private readonly PagedPropertySyncState<ResidentialOccupancySnapshot, CachedProperty, PendingProperty, HostObserved, Entity>
            _propertyState = new PagedPropertySyncState<ResidentialOccupancySnapshot, CachedProperty, PendingProperty, HostObserved, Entity>();
        private const int UpdatePartitions = PropertySyncLimits.UpdatePartitions;

        /// <summary>
        /// Must be a power of two: the game gates updates with <c>frameIndex &amp; (interval - 1)</c>.
        /// </summary>
        private const int UpdateIntervalFrames = 64;
        private readonly SimulationScanCadence _hostScanCadence = new SimulationScanCadence();
        private readonly SimulationScanCadence _repairScanCadence = new SimulationScanCadence();

        // Remote growables are placed at the exact XZ; a wider radius can claim the neighbouring half-lot.
        private const float AnchorMatchDistance = 0.5f;
        private const float AnchorSearchRadius = 8f;
        private const float AmbiguousDistanceEpsilon = 0.01f;

        /// <summary>
        /// Soft byte budget per page (~1 Hz). A dense property travels whole, so this uses most of the
        /// codec's 240 KiB allowance.
        /// </summary>
        private const int PageByteBudget = 224 * 1024;

        private const int MaxIncomingPages = PropertySyncLimits.MaxIncomingPages;
        private const int MaxPumpPages = PropertySyncLimits.MaxPumpPages;
        private const int MaxCachedProperties = PropertySyncLimits.MaxCachedProperties;
        private const int MaxPendingMoveIns = 4096;
        private const int MaxStagedTransfers = 4096;
        // City-scale, so a lifecycle wave rotates across the wire before its first records expire.
        private const int MaxTrackedDepartures = 131072;
        private const int MaxTrackedHouseholdChecksPerUpdate = 1024;
        private const int MaxTrackedCitizenChecksPerUpdate = 2048;
        private const long DepartureRetentionMs = 900000;
        private const int MaxMoveInFinalizationsPerUpdate = 256;
        private const int MaxPendingRetriesPerPump = 128;
        private const long ResolveRetryMs = PropertySyncLimits.ResolveRetryMs;
        private const long ResolveTimeoutMs = 300000;
        private const int MaxPriorityProperties = 4096;
        private const int PriorityPropertiesPerPage = 64;

        // Rolling walk ceilings (host detector and client repair), so cost stays flat with city size.
        // Urgent changes do not wait on them: renter events and the dirty queue act at once.
        private const int MaxPropertiesObservedPerUpdate = 128;
        private const int MaxCachedPropertiesWalkedPerUpdate = 256;
        // Departure records rotate within a reserved slice of each page, leaving room for the baseline.
        private const int HostDeparturesPerPage =
            ResidentialOccupancySnapshot.MaxDeparturesPerPage;
        private const int HostCitizenDeparturesPerPage =
            ResidentialOccupancySnapshot.MaxCitizenDeparturesPerPage;
        private const int PriorityByteBudget = PageByteBudget * 3 / 4;

        // Structural work is capped well below the page rate.
        private const int MaxPropertiesAppliedPerUpdate = 96;
        private const int MaxHouseholdsCreatedPerUpdate = 12;
        private const int MaxCitizensCreatedPerUpdate = 48;
        private const int MaxVehiclesCreatedPerUpdate = 24;
        private const int MaxCitizensRetiredPerUpdate = 48;
        private const int MaxHouseholdsRetiredPerUpdate = 12;

        private const long StatsIntervalMs = PropertySyncLimits.StatsIntervalMs;

        private ConcurrentQueue<ResidentialOccupancySnapshot> _incoming => _propertyState.Incoming;
        private Dictionary<Entity, CachedProperty> _cache => _propertyState.Cache;
        private List<Entity>[] _cacheBuckets => _propertyState.CachedPartitions.Buckets;
        private HashSet<Entity>[] _cacheBucketMembers => _propertyState.CachedPartitions.Members;
        private int[] _cacheBucketCursor => _propertyState.CachedPartitions.Cursor;
        private readonly List<Entity> _dirty = new List<Entity>();
        private readonly HashSet<Entity> _dirtyMembers = new HashSet<Entity>();
        private Dictionary<PropertyIdentity, PendingProperty> _pending => _propertyState.Pending;
        private readonly Dictionary<ulong, PendingMoveIn> _pendingMoveIns =
            new Dictionary<ulong, PendingMoveIn>();
        private readonly ConcurrentQueue<ulong> _pendingMoveInOrder = new ConcurrentQueue<ulong>();
        private readonly Dictionary<ulong, StagedTransfer> _stagedTransfers =
            new Dictionary<ulong, StagedTransfer>();
        private readonly Dictionary<ulong, uint> _stagedTransferCooldownUntil =
            new Dictionary<ulong, uint>();
        private readonly List<ulong> _stagedTransferScratch = new List<ulong>();
        private readonly HashSet<ulong> _pendingCitizenRetirementIds = new HashSet<ulong>();
        private readonly ConcurrentQueue<ulong> _pendingCitizenRetirements =
            new ConcurrentQueue<ulong>();
        private readonly List<Entity> _cacheScratch = new List<Entity>();
        private readonly HashSet<Entity> _authorizedMoveAways = new HashSet<Entity>();
        private readonly List<Entity> _authorizedMoveAwayScratch = new List<Entity>();
        private readonly HashSet<Entity> _lifecyclePropertyScratch = new HashSet<Entity>();

        // Host-side priority: shortens the latency of a change.
        private Dictionary<Entity, HostObserved> _hostObserved => _propertyState.HostObserved;
        private List<Entity>[] _hostObservedBuckets => _propertyState.HostPartitions.Buckets;
        private bool[] _hostBucketInitialized => _propertyState.HostPartitions.Initialized;
        private int[] _hostBucketCursor => _propertyState.HostPartitions.Cursor;
        private readonly Dictionary<Entity, int> _traceSentRosterHashes =
            new Dictionary<Entity, int>();
        private readonly Dictionary<PropertyIdentity, int> _traceReceivedRosterHashes =
            new Dictionary<PropertyIdentity, int>();
        private readonly Dictionary<ulong, PropertyIdentity> _tracePlacedHouseholds =
            new Dictionary<ulong, PropertyIdentity>();
        private Dictionary<PropertyIdentity, Entity> _priority => _propertyState.Priority;
        private ConcurrentQueue<PropertyIdentity> _priorityOrder => _propertyState.PriorityOrder;
        private readonly Dictionary<ulong, HostDeparture> _hostDepartures =
            new Dictionary<ulong, HostDeparture>();
        private readonly ConcurrentQueue<ulong> _hostDepartureOrder = new ConcurrentQueue<ulong>();
        private readonly HashSet<ulong> _hostDepartureOrderMembers = new HashSet<ulong>();
        private readonly Dictionary<ulong, HostDeparture> _hostCitizenDepartures =
            new Dictionary<ulong, HostDeparture>();
        private readonly ConcurrentQueue<ulong> _hostCitizenDepartureOrder =
            new ConcurrentQueue<ulong>();
        private readonly HashSet<ulong> _hostCitizenDepartureOrderMembers = new HashSet<ulong>();
        private readonly Dictionary<ulong, HostCitizenObservation> _hostCitizens =
            new Dictionary<ulong, HostCitizenObservation>();
        private readonly ConcurrentQueue<ulong> _hostCitizenOrder = new ConcurrentQueue<ulong>();
        private readonly HashSet<ulong> _hostCitizenOrderMembers = new HashSet<ulong>();
        private readonly Dictionary<ulong, Entity> _hostHouseholds =
            new Dictionary<ulong, Entity>();
        private readonly ConcurrentQueue<ulong> _hostHouseholdOrder = new ConcurrentQueue<ulong>();
        private readonly HashSet<ulong> _hostHouseholdOrderMembers = new HashSet<ulong>();
        private readonly Dictionary<ulong, ulong[]> _hostHouseholdCitizens =
            new Dictionary<ulong, ulong[]>();

        private EntityQuery _properties;
        private EntityQuery _bootstrapHouseholds;
        private EntityQuery _bootstrapCitizens;
        private EntityQuery _unreachableHouseholds;
        private EntityQuery _departingHouseholds;
        private EntityQuery _clientPropertySeekers;
        private EntityQuery _renterUpdates;
        private EntityQuery _prefabs;
        private EntityQuery _citizenCreationPrefabs;
        private EntityQuery _arrivalOutsideConnections;
        private Entity _citizenCreationPrefab;
        private Entity[] _hostSweepEntities;
        private PrefabSystem _prefabSystem;
        private PrefabIndex _prefabIndex;
        private ObjectSearch _objectSearch;
        private SimulationSystem _simulationSystem;
        private PropertyProcessingSystem _propertyProcessing;

        private int _captureCursor;
        private uint _captureSweepId = 1;
        private int _capturePageIndex;
        private ulong _hostCaptureRevision = 1;
        private bool _captureSweepHadSkips;
        private bool _captureBaselineNeedsEmptyPage;
        private bool _syncWasReady;
        private long _lastStatsMs;
        private long _sentBytes;
        private int _sentPages;
        private int _sentProperties;
        private int _priorityChanges;
        private int _priorityDrops;
        private int _captureSkips;
        private int _observedProperties;
        private int _probeSkipped;
        private int _reconcileSkipped;
        private int _unchangedProperties;
        private int _receivedPages;
        private int _droppedPages;
        private int _resolved;
        private int _unresolved;
        private int _ambiguous;
        private int _expired;
        private int _cacheDrops;
        private int _stalePages;
        private int _pruned;
        private int _appliedProperties;
        private int _createdHouseholds;
        private int _createdCitizens;
        private int _createdPets;
        private int _createdVehicles;
        private int _retiredHouseholds;
        private int _removedCitizens;
        private int _rewrittenCitizens;
        private int _healthProblemCorrections;
        private int _hostDeathTransitions;
        private int _lifecyclePrioritySignals;
        private int _lifecycleRepairSignals;
        private int _clientRenterRepairSignals;
        private int _rentActions;
        private int _refusedMoveIns;
        private int _forcedCompletions;
        private int _forcedPrefabCorrections;
        private int _alignedBuildRates;
        private int _deferredForConstruction;
        private int _renamedEntities;
        private int _economyCorrections;
        private int _economyDeferred;
        private int _incomeCorrections;
        private int _incomeDeferred;
        private int _feeInputCorrections;
        private int _feeInputDeferred;

        private sealed class CachedProperty
        {
            public PropertyIdentity Identity;
            public Entity Prefab;
            public ulong Revision;
            public OccupancyProperty LastReceived;
            public byte ConstructionSpeed;
            public bool HasElectricityConsumer;
            public int ElectricityFulfilledConsumption;
            public bool HasWaterConsumer;
            public int WaterFulfilledFresh;
            public int WaterFulfilledSewage;
            public OccupancyHousehold[] Households;
            public int Bucket;
            public uint LastSeenSweep;
            public bool RemoveAfterApply;
        }

        private sealed class PendingProperty : PendingPropertyState<OccupancyProperty>
        {
            public OccupancyProperty Property { get => Entry; set => Entry = value; }
 }

        private sealed class PendingMoveIn
        {
            public ulong HouseholdId;
            public Entity Household;
            public Entity Property;
            public int Rent;
            public ulong Revision;
            public bool CreatedLocally;
        }

        private sealed class StagedTransfer
        {
            public Entity Household;
            public Entity Source;
            public Entity Destination;
            public uint StartedFrame;
        }

        private sealed class HostObserved
        {
            public int Hash;
            public int Bucket;

            /// <summary>A renter event queued this property; re-baseline without counting a second change.</summary>
            public bool Stale;
        }

        private sealed class HostDeparture
        {
            public ulong Revision;
            public long ExpiresMs;
            public bool Unhoused;
        }

        // A struct: one per resident, so no boxed heap graph for the GC.
        private struct HostCitizenObservation
        {
            public Entity Entity;
            public ulong HouseholdId;
        }

        public override int GetUpdateInterval(SystemUpdatePhase phase) =>
            phase == SystemUpdatePhase.GameSimulation ? UpdateIntervalFrames : 1;

        protected override void OnCreate()
        {
            base.OnCreate();
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _prefabs = GetEntityQuery(ComponentType.ReadOnly<PrefabData>());
            _citizenCreationPrefabs = GetEntityQuery(
                ComponentType.ReadOnly<global::Game.Prefabs.CitizenData>(),
                ComponentType.ReadOnly<ArchetypeData>());
            _arrivalOutsideConnections = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<global::Game.Objects.OutsideConnection, PrefabRef,
                    global::Game.Objects.Transform>(),
                None = SyncQuery.ReadOnly<global::Game.Objects.ElectricityOutsideConnection,
                    global::Game.Objects.WaterPipeOutsideConnection, Deleted, Temp>(),
            });
            _prefabIndex = new PrefabIndex(_prefabSystem, _prefabs);
            _objectSearch = new ObjectSearch(
                World.GetOrCreateSystemManaged<global::Game.Objects.SearchSystem>());
            _simulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
            _propertyProcessing = World.GetOrCreateSystemManaged<PropertyProcessingSystem>();
            _properties = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<Building, ResidentialProperty, Renter, PrefabRef,
                    global::Game.Objects.Transform, UpdateFrame>(),
                None = SyncQuery.ReadOnly<Temp, Deleted, Owner>(),
            });
            _bootstrapHouseholds = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<global::Game.Citizens.Household,
                    global::Game.Citizens.HouseholdCitizen, PrefabRef>(),
                None = SyncQuery.ReadOnly<Deleted, Temp, global::Game.Citizens.TouristHousehold,
                    global::Game.Citizens.CommuterHousehold>(),
            });
            _bootstrapCitizens = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<global::Game.Citizens.Citizen,
                    global::Game.Citizens.HouseholdMember, PrefabRef>(),
                None = SyncQuery.ReadOnly<Deleted, Temp>(),
            });
            // Homeless households on a client. Tourists and commuters are not ours; CurrentBuilding means
            // still arriving.
            _unreachableHouseholds = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<global::Game.Citizens.Household, PrefabRef>(),
                None = SyncQuery.ReadOnly<PropertyRenter, global::Game.Citizens.HomelessHousehold,
                    global::Game.Agents.MovingAway, global::Game.Citizens.CurrentBuilding,
                    global::Game.Citizens.TouristHousehold, global::Game.Citizens.CommuterHousehold,
                    Deleted, Temp>(),
            });
            _departingHouseholds = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<global::Game.Citizens.Household,
                    global::Game.Agents.MovingAway>(),
                None = SyncQuery.ReadOnly<Deleted, Temp, global::Game.Citizens.TouristHousehold,
                    global::Game.Citizens.CommuterHousehold>(),
            });
            // Enableable: only households actively seeking a home. The host owns that decision.
            _clientPropertySeekers = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<global::Game.Citizens.Household,
                    global::Game.Agents.PropertySeeker>(),
                None = SyncQuery.ReadOnly<Deleted, Temp, global::Game.Citizens.TouristHousehold,
                    global::Game.Citizens.CommuterHousehold>(),
            });
            // Raised on every renter add/remove; consumed by a dedicated every-frame boundary.
            _renterUpdates = GetEntityQuery(
                ComponentType.ReadOnly<global::Game.Common.Event>(),
                ComponentType.ReadOnly<RentersUpdated>());
            SyncInbox.RegisterDrain(DrainForWorldChange);
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
            if (service == null || !service.SimulationSyncReady)
            {
                // Keep client authority across a world-sync barrier, or lifecycle systems act in the gap.
                if (service != null && service.Session.Role == SessionRole.Client)
                    ApplyLocalAuthority(service.Session);
                else
                    RestoreLocalAuthority();
                if (_syncWasReady)
                {
                    DrainForWorldChange();
                }
                _syncWasReady = false;
                return;
            }
            _syncWasReady = true;

            MultiplayerSession session = service.Session;
            ApplyLocalAuthority(session);

            if (session.Role == SessionRole.Host)
            {
                _droppedPages += _propertyState.DropIncoming();
                if (_hostScanCadence.TryTakePartition(_simulationSystem.selectedSpeed,
                        UpdatePartitions, out int bucket))
                {
                    using (Diagnostics.SyncProfiler.Measure("Occupancy.HostHouseholds", Diagnostics.SyncZone.Residential))
                        ScanTrackedHostHouseholds(service.NowMs);
                    using (Diagnostics.SyncProfiler.Measure("Occupancy.HostCitizens", Diagnostics.SyncZone.Residential))
                        ScanTrackedHostCitizens(service.NowMs);
                    using (Diagnostics.SyncProfiler.Measure("Occupancy.HostScan", Diagnostics.SyncZone.Residential))
                        ScanHostChanges(bucket);
                }
            }
            else
            {
                using (Diagnostics.SyncProfiler.Measure("Occupancy.Pump", Diagnostics.SyncZone.Residential))
                    PumpIncoming();
                ApplyPending();
            }
            ReportStats(session, service.NowMs);
        }
    }
}
