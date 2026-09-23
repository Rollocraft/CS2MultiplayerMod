using CS2MultiplayerMod.Core.Sync;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.Infrastructure;
using Game;
using Game.Buildings;
using Game.Common;
using Game.Companies;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Makes the host the only author of commercial, industrial and office business: tenancy, panel
    /// figures and held goods. Clients hold the accounting calculator, tenant creation and property
    /// search; the host's per-building roster is realized through the native rent-action queue.
    /// Structural work is budgeted per update and gated behind a settle window. Employees resolve
    /// through <see cref="ResidentialOccupancySyncSystem"/>'s citizen map onto real local citizens.
    /// </summary>
    public partial class CompanyStatsSyncSystem : GameSystemBase,
        Channels.IPagedPropertyRuntime<CompanyStatsSnapshot>
    {
        private readonly PagedPropertySyncState<CompanyStatsSnapshot, CachedEntry, PendingEntry, int, Entity>
            _propertyState = new PagedPropertySyncState<CompanyStatsSnapshot, CachedEntry, PendingEntry, int, Entity>();
        private const int UpdatePartitions = PropertySyncLimits.UpdatePartitions;
        private readonly SimulationScanCadence _hostScanCadence = new SimulationScanCadence();
        private readonly SimulationScanCadence _stateScanCadence = new SimulationScanCadence();
        private readonly SimulationScanCadence _tenancyScanCadence = new SimulationScanCadence();

        /// <summary>
        /// Matches <c>CompanyEconomyStatisticSystem.kUpdatesPerDay</c>; interval and partition must be
        /// derived exactly as that system does.
        /// </summary>
        private const int CompanyUpdatesPerDay = 128;
        private const int CompanyUpdateInterval = 262144 / (CompanyUpdatesPerDay * UpdatePartitions);

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
        // Bounded by bytes: employee rosters make entry sizes vary by orders of magnitude.
        private const int PriorityEntriesPerPage = 224;
        private const int PageByteBudget = CompanyStatsSnapshot.MaxEncodedBytes - 512;
        private const int PriorityByteBudget = PageByteBudget * 7 / 8;

        private const int MaxPropertiesObservedPerUpdate = PropertySyncLimits.MaxPropertiesObservedPerUpdate;

        /// <summary>Slow repair walk once the dirty queue is empty; real changes arrive on pages.</summary>
        private const int MaxTenancyWalkedPerUpdate = 64;
        private const int MaxTenancyDirtyPerBoundary = 128;

        /// <summary>Re-attempts after an incomplete apply; see <see cref="RetryStateDirty"/>.</summary>
        private const int MaxStateRetries = 3;

        /// <summary>Entity handles are reused, so this table is dropped whole rather than pruned.</summary>
        private const int MaxObservedEmployeeBuffers = 16384;

        /// <summary>Last exact hash per building, so chunk false positives and our own writes do not re-arm.</summary>
        private const int MaxObservedEfficiencyBuffers = 131072;

        private const int MaxObservedExtractorCompanies = 16384;
        private const int MaxExtractorSignalsPerBoundary = 64;

        private const int MaxStateDirtyPerBoundary = 128;
        private const int MaxStateWalkedPerBoundary = 64;
        private const int MaxEfficiencyDirtyPerBoundary = 512;

        /// <summary>Structural ceilings: creating or closing a business is what a frame feels.</summary>
        private const int MaxCompaniesCreatedPerUpdate = 6;
        private const int MaxCompaniesRetiredPerUpdate = 6;

        /// <summary>
        /// Frames a building is left alone after a tenancy action, while its move-in is still queued;
        /// acting again would create a second business or undo the first.
        /// </summary>
        private const uint SettleFrames = 4 * 16;

        /// <summary>Cap on the queue of just-changed buildings; the rolling walk is the backstop.</summary>
        private const int MaxDirtyProperties = 8192;

        private const long StatsIntervalMs = PropertySyncLimits.StatsIntervalMs;

        private ConcurrentQueue<CompanyStatsSnapshot> _incoming => _propertyState.Incoming;

        /// <summary>Resolved workplace building -> what the host says about it.</summary>
        private Dictionary<Entity, CachedEntry> _cache => _propertyState.Cache;
        private Dictionary<PropertyIdentity, PendingEntry> _pending => _propertyState.Pending;
        private readonly List<Entity> _cacheScratch = new List<Entity>();

        private readonly List<Entity> _dirty = new List<Entity>();
        private readonly HashSet<Entity> _dirtyMembers = new HashSet<Entity>();
        private readonly List<Entity> _stateDirty = new List<Entity>();
        private readonly HashSet<Entity> _stateDirtyMembers = new HashSet<Entity>();
        private readonly Dictionary<Entity, int> _stateRetries = new Dictionary<Entity, int>();

        /// <summary>What we last left each employee buffer at, so our own writes do not re-dirty it.</summary>
        private readonly Dictionary<Entity, int> _clientEmployeeObserved =
            new Dictionary<Entity, int>();
        private readonly Dictionary<Entity, int> _hostEfficiencyObserved =
            new Dictionary<Entity, int>();
        private readonly Dictionary<Entity, int> _clientEfficiencyObserved =
            new Dictionary<Entity, int>();
        private readonly Dictionary<Entity, int> _hostExtractorProduce =
            new Dictionary<Entity, int>();
        private bool _hostEfficiencyObservationInitialized;
        private bool _clientEfficiencyObservationInitialized;
        private readonly List<Entity> _efficiencyDirty = new List<Entity>();
        private readonly HashSet<Entity> _efficiencyDirtyMembers = new HashSet<Entity>();
        private readonly List<Entity> _stateRetryScratch = new List<Entity>();
        private readonly HashSet<Entity> _stateAppliedThisBoundary = new HashSet<Entity>();
        private readonly List<Entity> _tenancyOrder = new List<Entity>();
        private int _tenancyCursor;
        private int _stateCursor;
        private readonly Dictionary<Entity, uint> _settling = new Dictionary<Entity, uint>();
        private readonly List<Entity> _settlingScratch = new List<Entity>();
        private readonly HashSet<Entity> _authorizedMoveAways = new HashSet<Entity>();
        private readonly List<Entity> _authorizedScratch = new List<Entity>();

        private Dictionary<Entity, int> _hostObserved => _propertyState.HostObserved;
        private readonly Dictionary<Entity, int> _hostEmployeeObserved =
            new Dictionary<Entity, int>();
        private bool[] _hostPartitionInitialized => _propertyState.HostPartitions.Initialized;
        private int[] _hostPartitionCursor => _propertyState.HostPartitions.Cursor;
        private Dictionary<PropertyIdentity, Entity> _priority => _propertyState.Priority;
        private ConcurrentQueue<PropertyIdentity> _priorityOrder => _propertyState.PriorityOrder;

        private readonly List<CompanyStatsResource> _resourceScratch =
            new List<CompanyStatsResource>();
        private readonly List<CompanyStatsTradeCost> _tradeCostScratch =
            new List<CompanyStatsTradeCost>();
        private readonly List<CompanyStatsEmployee> _employeeScratch =
            new List<CompanyStatsEmployee>();
        private readonly List<CompanyStatsEfficiency> _efficiencyScratch =
            new List<CompanyStatsEfficiency>();
        private readonly HashSet<ulong> _employeeIdScratch = new HashSet<ulong>();
        private readonly List<ResolvedEmployee> _resolvedEmployeeScratch =
            new List<ResolvedEmployee>();
        private readonly HashSet<Entity> _desiredEmployeeEntities = new HashSet<Entity>();
        private readonly List<Entity> _employeeEntityScratch = new List<Entity>();
        private readonly List<Entity> _employeeRemovalScratch = new List<Entity>();

        // The partition split by zone so each is timed separately.
        private readonly List<Entity> _commercialBucket = new List<Entity>();
        private readonly List<Entity> _industrialBucket = new List<Entity>();
        private readonly List<Entity> _officeBucket = new List<Entity>();
        private readonly int[] _zoneApplied = new int[5];
        private readonly int[] _zoneOpened = new int[5];
        private readonly int[] _zoneClosed = new int[5];

        private EntityQuery _properties;
        private EntityQuery _companies;
        private EntityQuery _departingCompanies;
        private EntityQuery _companySeekers;
        private EntityQuery _renterUpdates;
        private EntityQuery _prefabs;
        private PrefabSystem _prefabSystem;
        private PrefabIndex _prefabIndex;
        private ObjectSearch _objectSearch;
        private SimulationSystem _simulationSystem;
        private PropertyProcessingSystem _propertyProcessing;
        private ResidentialOccupancySyncSystem _occupancy;
        private global::Game.UI.NameSystem _nameSystem;

        private Entity[] _hostSweepEntities;
        private int _captureCursor;
        private uint _captureSweepId = 1;
        private int _capturePageIndex;
        private bool _syncWasReady;
        private long _lastStatsMs;
        private long _sentBytes;
        private int _sentPages, _sentEntries, _priorityChanges, _priorityDrops, _captureSkips;
        private int _receivedPages, _droppedPages, _resolved, _unresolved, _ambiguous, _expired;
        private int _appliedCompanies, _correctedFields, _correctedResources;
        private int _correctedCompanyData, _correctedTradeCosts, _correctedEmployees;
        private int _correctedEfficiencies, _hostEfficiencySignals, _clientEfficiencyRepairs;
        private int _correctedExtractorProduce, _hostExtractorSignals;
        private int _correctedPropertyPrefabs, _alignedPropertyBuildRates;
        private int _createdCompanies, _retiredCompanies, _deferredActions, _cancelledDecisions;
        private int _hostLifecycleSignals, _clientLifecycleRepairs;

        private sealed class CachedEntry
        {
            public CompanyStatsEntry Entry;
            public uint LastSeenSweep;
        }

        private sealed class PendingEntry : PendingPropertyState<CompanyStatsEntry>
        { }

        private struct ResolvedEmployee
        {
            public Entity Citizen;
            public CompanyStatsEmployee State;
        }

        public override int GetUpdateInterval(SystemUpdatePhase phase) =>
            phase == SystemUpdatePhase.GameSimulation ? CompanyUpdateInterval : 1;

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
            _occupancy = World.GetOrCreateSystemManaged<ResidentialOccupancySyncSystem>();
            _nameSystem = World.GetOrCreateSystemManaged<global::Game.UI.NameSystem>();

            // The host sweeps buildings, not companies: vacancy is what a client cannot work out alone.
            _properties = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<Building, Renter, PrefabRef,
                    global::Game.Objects.Transform, UpdateFrame>(),
                Any = SyncQuery.ReadOnly<CommercialProperty, IndustrialProperty, OfficeProperty,
                    StorageProperty, ExtractorProperty>(),
                None = SyncQuery.ReadOnly<Temp, Deleted>(),
            });

            // Same shape as CompanyEconomyStatisticSystem's query: exactly the companies it writes.
            _companies = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<CompanyData, global::Game.Economy.Resources,
                    PropertyRenter, CompanyStatisticData, UpdateFrame>(),
                None = SyncQuery.ReadOnly<Created, Deleted, Temp>(),
            });

            _departingCompanies = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<CompanyData, global::Game.Agents.MovingAway>(),
                None = SyncQuery.ReadOnly<Deleted, Temp>(),
            });

            // Enableable: only companies actively seeking a building. The host owns that decision.
            _companySeekers = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<CompanyData, global::Game.Agents.PropertySeeker>(),
                None = SyncQuery.ReadOnly<Deleted, Temp>(),
            });

            _renterUpdates = GetEntityQuery(ComponentType.ReadOnly<RentersUpdated>());

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
            using (Diagnostics.SyncProfiler.Measure("CompanyStats"))
            {
                MultiplayerService service = Mod.Service;
                if (service == null || !service.SimulationSyncReady)
                {
                    // Keep client authority across a world-sync barrier, or the spawners open businesses in the gap.
                    if (service != null && service.Session.Role == SessionRole.Client)
                        ApplyLocalAuthority(service.Session);
                    else
                        RestoreLocalAuthority();
                    if (_syncWasReady) DrainForWorldChange();
                    _syncWasReady = false;
                    return;
                }
                _syncWasReady = true;

                MultiplayerSession session = service.Session;
                ApplyLocalAuthority(session);

                // Derived exactly as CompanyEconomyStatisticSystem does: the partition it just recomputed.
                uint updateFrame = SimulationUtils.GetUpdateFrame(
                    _simulationSystem.frameIndex, CompanyUpdatesPerDay, UpdatePartitions);

                if (session.Role == SessionRole.Host)
                {
                    _droppedPages += _propertyState.DropIncoming();
                    if (_hostScanCadence.TryTakePartition(_simulationSystem.selectedSpeed,
                            UpdatePartitions, out int partition))
                        ScanHostChanges(partition);
                }
                else
                {
                    // Fallback; the city-state pump normally resolved these already.
                    PumpIncoming();
                    ApplyFigures(updateFrame);
                }
                ReportStats(session, service.NowMs);
            }
        }

        /// <summary>Also called from the pump: GameSimulation stops while paused.</summary>
        internal void MaintainAuthority()
        {
            MultiplayerService service = Mod.Service;
            if (service == null || service.Session.Role != SessionRole.Client)
            {
                RestoreLocalAuthority();
                return;
            }
            ApplyLocalAuthority(service.Session);
        }

        internal void Enqueue(CompanyStatsSnapshot snapshot)
        {
            if (snapshot != null) _droppedPages += _propertyState.Enqueue(snapshot);
        }

        // Authority is maintained from the pump too: it also runs while paused.
        bool Channels.IPagedPropertyRuntime<CompanyStatsSnapshot>.Capture(NetworkWriter writer) => Capture(writer);
        void Channels.IPagedPropertyRuntime<CompanyStatsSnapshot>.Enqueue(CompanyStatsSnapshot snapshot) => Enqueue(snapshot);
        void Channels.IPagedPropertyRuntime<CompanyStatsSnapshot>.Pump() { MaintainAuthority(); PumpIncoming(); }
        void Channels.IPagedPropertyRuntime<CompanyStatsSnapshot>.ResetPending() => ResetPending();

        internal void ResetPending()
        {
            DrainForWorldChange();
            MultiplayerService service = Mod.Service;
            if (service != null && service.Session.Role == SessionRole.Client)
                ApplyLocalAuthority(service.Session);
            else if (service == null || !service.SimulationSyncReady)
                RestoreLocalAuthority();
        }

        internal void DrainForWorldChange()
        {
            lock (_incoming) SyncInbox.Clear(_incoming);
            _cache.Clear();
            _cacheScratch.Clear();
            _propertyState.ClearPending();
            _priority.Clear();
            while (_priorityOrder.TryDequeue(out PropertyIdentity discardedPriority)) { }
            _hostObserved.Clear();
            _hostScanCadence.Reset();
            _stateScanCadence.Reset();
            _tenancyScanCadence.Reset();
            _hostEmployeeObserved.Clear();
            _hostEfficiencyObserved.Clear();
            _clientEfficiencyObserved.Clear();
            _hostExtractorProduce.Clear();
            _hostEfficiencyObservationInitialized = false;
            _clientEfficiencyObservationInitialized = false;
            Array.Clear(_hostPartitionInitialized, 0, _hostPartitionInitialized.Length);
            Array.Clear(_hostPartitionCursor, 0, _hostPartitionCursor.Length);
            _dirty.Clear();
            _dirtyMembers.Clear();
            _stateDirty.Clear();
            _stateDirtyMembers.Clear();
            _efficiencyDirty.Clear();
            _efficiencyDirtyMembers.Clear();
            _stateRetries.Clear();
            _clientEmployeeObserved.Clear();
            _stateRetryScratch.Clear();
            _stateAppliedThisBoundary.Clear();
            _tenancyOrder.Clear();
            _tenancyCursor = 0;
            _stateCursor = 0;
            _settling.Clear();
            _settlingScratch.Clear();
            _authorizedMoveAways.Clear();
            _authorizedScratch.Clear();
            _resourceScratch.Clear();
            _tradeCostScratch.Clear();
            _employeeScratch.Clear();
            _efficiencyScratch.Clear();
            _employeeIdScratch.Clear();
            _resolvedEmployeeScratch.Clear();
            _desiredEmployeeEntities.Clear();
            _employeeEntityScratch.Clear();
            _employeeRemovalScratch.Clear();
            _employeeRemovalMembers.Clear();
            _partialEmployeeStates.Clear();
            _partialEmployeeSeen.Clear();
            _commercialBucket.Clear();
            _industrialBucket.Clear();
            _officeBucket.Clear();
            Array.Clear(_zoneApplied, 0, _zoneApplied.Length);
            Array.Clear(_zoneOpened, 0, _zoneOpened.Length);
            Array.Clear(_zoneClosed, 0, _zoneClosed.Length);
            _hostSweepEntities = null;
            _captureCursor = 0;
            _capturePageIndex = 0;
            _captureSweepId = 1;
            _propertyState.ResetSweep();
            _prefabIndex = new PrefabIndex(_prefabSystem, _prefabs);
        }

        /// <summary>Rentable by a business, including warehouses and extractor properties.</summary>
        private bool IsLiveWorkplaceProperty(Entity property) =>
            property != Entity.Null && EntityManager.Exists(property) &&
            EntityManager.HasComponent<Building>(property) &&
            EntityManager.HasBuffer<Renter>(property) &&
            EntityManager.HasComponent<PrefabRef>(property) &&
            EntityManager.HasComponent<global::Game.Objects.Transform>(property) &&
            (EntityManager.HasComponent<CommercialProperty>(property) ||
             EntityManager.HasComponent<IndustrialProperty>(property) ||
             EntityManager.HasComponent<OfficeProperty>(property) ||
             EntityManager.HasComponent<StorageProperty>(property) ||
             EntityManager.HasComponent<ExtractorProperty>(property)) &&
            !EntityManager.HasComponent<Temp>(property) &&
            !EntityManager.HasComponent<Deleted>(property);

        /// <summary>The business renting a building, or null; households in a mixed building are channel 21's.</summary>
        private Entity FindTenant(Entity property)
        {
            if (!EntityManager.HasBuffer<Renter>(property)) return Entity.Null;
            DynamicBuffer<Renter> renters = EntityManager.GetBuffer<Renter>(property, true);
            for (int i = 0; i < renters.Length; i++)
            {
                Entity renter = renters[i].m_Renter;
                if (renter == Entity.Null || !EntityManager.Exists(renter)) continue;
                if (!EntityManager.HasComponent<CompanyData>(renter)) continue;
                if (EntityManager.HasComponent<Deleted>(renter) ||
                    EntityManager.HasComponent<Temp>(renter)) continue;
                return renter;
            }
            return Entity.Null;
        }

        private void ReportStats(MultiplayerSession session, long now)
        {
            if (_lastStatsMs == 0) { _lastStatsMs = now; return; }
            if (now - _lastStatsMs < StatsIntervalMs) return;
            _lastStatsMs = now;

            if (session.Role == SessionRole.Host)
                WriteToWorkplaceTopics("pages=" + _sentPages + ", entries=" + _sentEntries +
                                       ", bytes=" + _sentBytes + ", changed=" + _priorityChanges +
                                       ", efficiencySignals=" + _hostEfficiencySignals +
                                       ", extractorSignals=" + _hostExtractorSignals +
                                       ", lifecycleSignals=" + _hostLifecycleSignals +
                                       ", queued=" + _priority.Count + ", dropped=" +
                                       _priorityDrops + ", skipped=" + _captureSkips + ".");
            else
            {
                WriteToWorkplaceTopics("pages=" + _receivedPages + ", queueDropped=" +
                                       _droppedPages + ", cached=" + _cache.Count + ", pending=" +
                                       _pending.Count + ", resolved=" + _resolved +
                                       ", unresolved=" + _unresolved + ", ambiguous=" + _ambiguous +
                                       ", expired=" + _expired + ", correctedFigures=" +
                                       _correctedFields + ", correctedResources=" +
                                       _correctedResources + ", correctedCompany=" +
                                       _correctedCompanyData + ", correctedTrade=" +
                                       _correctedTradeCosts + ", correctedEmployees=" +
                                       _correctedEmployees + ", correctedEfficiency=" +
                                       _correctedEfficiencies + ", efficiencyRepairs=" +
                                       _clientEfficiencyRepairs + ", correctedExtractorProduce=" +
                                       _correctedExtractorProduce + ", prefabCorrections=" +
                                       _correctedPropertyPrefabs + ", buildRatesAligned=" +
                                       _alignedPropertyBuildRates + ", deferred=" + _deferredActions +
                                       ", lifecycleRepairs=" + _clientLifecycleRepairs +
                                       ", cancelledLocalDecisions=" + _cancelledDecisions +
                                       ", tenancyDirty=" + _dirty.Count + ", stateDirty=" +
                                       _stateDirty.Count + ", efficiencyDirty=" +
                                       _efficiencyDirty.Count + ".");
                ReportZone(SyncZone.Commercial);
                ReportZone(SyncZone.Industrial);
                ReportZone(SyncZone.Office);
            }

            _sentPages = _sentEntries = _priorityChanges = _priorityDrops = _captureSkips = 0;
            _sentBytes = 0;
            _receivedPages = _droppedPages = _resolved = _unresolved = _ambiguous = _expired = 0;
            _appliedCompanies = _correctedFields = _correctedResources = 0;
            _correctedCompanyData = _correctedTradeCosts = _correctedEmployees = 0;
            _correctedEfficiencies = _hostEfficiencySignals = _clientEfficiencyRepairs = 0;
            _correctedExtractorProduce = _hostExtractorSignals = 0;
            _correctedPropertyPrefabs = _alignedPropertyBuildRates = 0;
            _createdCompanies = _retiredCompanies = _deferredActions = _cancelledDecisions = 0;
            _hostLifecycleSignals = _clientLifecycleRepairs = 0;
            Array.Clear(_zoneApplied, 0, _zoneApplied.Length);
            Array.Clear(_zoneOpened, 0, _zoneOpened.Length);
            Array.Clear(_zoneClosed, 0, _zoneClosed.Length);
        }

        /// <summary>The shared health line goes to each enabled zone topic, built at most once.</summary>
        private void WriteToWorkplaceTopics(string body)
        {
            bool commercial = SyncLog.IsZoneEnabled(SyncZone.Commercial);
            bool industrial = SyncLog.IsZoneEnabled(SyncZone.Industrial);
            bool office = SyncLog.IsZoneEnabled(SyncZone.Office);
            if (!commercial && !industrial && !office) return;
            string line = "CompanyStats/30s: " + body;
            if (commercial) SyncLog.DetailZone(SyncZone.Commercial, line);
            if (industrial) SyncLog.DetailZone(SyncZone.Industrial, line);
            if (office) SyncLog.DetailZone(SyncZone.Office, line);
        }

        private void ReportZone(SyncZone zone)
        {
            if (!SyncLog.IsZoneEnabled(zone)) return;
            int index = (int)zone;
            SyncLog.DetailZone(zone, "corrected=" + _zoneApplied[index] + ", opened=" +
                                    _zoneOpened[index] + ", closed=" + _zoneClosed[index] + ".");
        }
    }
}
