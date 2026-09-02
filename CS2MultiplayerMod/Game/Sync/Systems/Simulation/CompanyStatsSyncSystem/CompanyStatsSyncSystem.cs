using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using CS2MultiplayerMod.Core.Diagnostics;
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
using Unity.Collections;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Makes the host the only author of commercial, industrial and office business: which
    /// building each business occupies, the money-facing figures behind its panel, and the goods
    /// it is holding.
    ///
    /// <para><b>Why the first attempt at the figures failed.</b> It corrected companies on a
    /// 1024-frame rotation while <c>CompanyEconomyStatisticSystem</c> rewrites the same fields
    /// every <b>128</b> frames, over a partition that system picks from its own frame index. Every
    /// correction was overwritten roughly eight times before the next one arrived, usually on a
    /// different set of companies, so the panels never settled. This system therefore borrows that
    /// writer's schedule exactly - same interval, same partition, ordered directly after it - and
    /// each company is corrected in the very frame its local value was recomputed.</para>
    ///
    /// <para><b>Tenancy is authority, not correction.</b> Figures can be corrected because the
    /// local writer is a pure function of local state. Occupancy cannot: a client that keeps
    /// choosing its own tenants would spawn and house businesses the host never had, and no
    /// amount of correcting after the fact removes them. So the client stops deciding - see
    /// Authority.cs - and the host's absolute per-building roster is realized through the game's
    /// own rent-action queue, exactly as residential occupancy does for households.</para>
    ///
    /// <para><b>What this costs.</b> In the steady state a matching building costs one dictionary
    /// lookup and a field comparison, and nothing structural happens at all. Structural work -
    /// creating or closing a business, which is also what makes a building change how it looks -
    /// is what a frame actually feels, so it is budgeted per update and gated behind a settle
    /// window that keeps a page arriving mid-move-in from causing churn.</para>
    ///
    /// Employment is resolved through <see cref="ResidentialOccupancySyncSystem"/>'s citizen
    /// identity map. A host worker is attached to the corresponding real local citizen, so the
    /// native Worker component continues to produce commutes and pedestrians; no display-only
    /// employee entities are fabricated.
    /// </summary>
    public partial class CompanyStatsSyncSystem : GameSystemBase
    {
        private const int UpdatePartitions = 16;

        /// <summary>
        /// Matches <c>CompanyEconomyStatisticSystem.kUpdatesPerDay</c>. Both the interval and the
        /// partition index are derived exactly as that system derives them; changing one without
        /// the other reintroduces the drift this feature exists to remove.
        /// </summary>
        private const int CompanyUpdatesPerDay = 128;
        private const int CompanyUpdateInterval = 262144 / (CompanyUpdatesPerDay * UpdatePartitions);

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
        // A busy dense district changes far more than 32 company records per second. Bytes, not
        // the old low-density entry count, are the meaningful bound because employee rosters make
        // entry sizes vary by orders of magnitude.
        private const int PriorityEntriesPerPage = 224;
        private const int PageByteBudget = CompanyStatsSnapshot.MaxEncodedBytes - 512;
        private const int PriorityByteBudget = PageByteBudget * 7 / 8;

        /// <summary>
        /// Buildings the rolling change detector examines per update on the host. The baseline
        /// sweep sends every workplace regardless; this ceiling only stops the detector's cost
        /// from growing with the city, as on the occupancy and rent observers.
        /// </summary>
        private const int MaxPropertiesObservedPerUpdate = 256;

        /// <summary>
        /// Cached buildings the tenancy pass re-examines per update once its dirty queue is empty.
        /// This is the slow repair path for drift the host never reported; a real change arrives
        /// on a page and is handled immediately through the dirty queue instead.
        /// </summary>
        private const int MaxTenancyWalkedPerUpdate = 64;

        /// <summary>
        /// Arrived pages are applied on a 16-frame boundary rather than waiting for the target
        /// company's 2,048-frame statistics rotation. Dirty entries are immediate; the rolling
        /// walk is a defence against a native writer changing state without a new host page.
        /// </summary>
        private const int MaxStateDirtyPerBoundary = 128;
        private const int MaxStateWalkedPerBoundary = 64;

        /// <summary>
        /// Structural ceilings. Creating or closing a business moves entities between chunks and
        /// changes how its building draws, so these are the numbers that decide whether a busy
        /// economy is felt as a hitch. Anything over the budget waits for the next update.
        /// </summary>
        private const int MaxCompaniesCreatedPerUpdate = 6;
        private const int MaxCompaniesRetiredPerUpdate = 6;

        /// <summary>
        /// Frames a building is left alone after a tenancy action. The move-in it asked for is
        /// still sitting in the native rent-action queue, and acting again before that drains
        /// would create a second business or undo the first.
        /// </summary>
        private const uint SettleFrames = 4 * 16;

        /// <summary>Cap on the queue of just-changed buildings; the rolling walk is the backstop.</summary>
        private const int MaxDirtyProperties = 8192;

        private const long StatsIntervalMs = 30000;

        private readonly ConcurrentQueue<CompanyStatsSnapshot> _incoming =
            new ConcurrentQueue<CompanyStatsSnapshot>();

        /// <summary>Resolved workplace building -> what the host says about it.</summary>
        private readonly Dictionary<Entity, CachedEntry> _cache = new Dictionary<Entity, CachedEntry>();
        private readonly Dictionary<PropertyRentIdentity, PendingEntry> _pending =
            new Dictionary<PropertyRentIdentity, PendingEntry>();
        private readonly ConcurrentQueue<PropertyRentIdentity> _pendingOrder =
            new ConcurrentQueue<PropertyRentIdentity>();
        private readonly List<Entity> _cacheScratch = new List<Entity>();

        private readonly List<Entity> _dirty = new List<Entity>();
        private readonly HashSet<Entity> _dirtyMembers = new HashSet<Entity>();
        private readonly List<Entity> _stateDirty = new List<Entity>();
        private readonly HashSet<Entity> _stateDirtyMembers = new HashSet<Entity>();
        private readonly List<Entity> _stateRetryScratch = new List<Entity>();
        private readonly List<Entity> _tenancyOrder = new List<Entity>();
        private int _tenancyCursor;
        private int _stateCursor;
        private readonly Dictionary<Entity, uint> _settling = new Dictionary<Entity, uint>();
        private readonly List<Entity> _settlingScratch = new List<Entity>();
        private readonly HashSet<Entity> _authorizedMoveAways = new HashSet<Entity>();
        private readonly List<Entity> _authorizedScratch = new List<Entity>();

        private readonly Dictionary<Entity, int> _hostObserved = new Dictionary<Entity, int>();
        private readonly Dictionary<Entity, int> _hostEmployeeObserved =
            new Dictionary<Entity, int>();
        private readonly bool[] _hostPartitionInitialized = new bool[UpdatePartitions];
        private readonly int[] _hostPartitionCursor = new int[UpdatePartitions];
        private readonly Dictionary<PropertyRentIdentity, Entity> _priority =
            new Dictionary<PropertyRentIdentity, Entity>();
        private readonly ConcurrentQueue<PropertyRentIdentity> _priorityOrder =
            new ConcurrentQueue<PropertyRentIdentity>();

        private readonly List<CompanyStatsResource> _resourceScratch =
            new List<CompanyStatsResource>();
        private readonly List<CompanyStatsTradeCost> _tradeCostScratch =
            new List<CompanyStatsTradeCost>();
        private readonly List<CompanyStatsEmployee> _employeeScratch =
            new List<CompanyStatsEmployee>();
        private readonly HashSet<ulong> _employeeIdScratch = new HashSet<ulong>();
        private readonly List<ResolvedEmployee> _resolvedEmployeeScratch =
            new List<ResolvedEmployee>();
        private readonly HashSet<Entity> _desiredEmployeeEntities = new HashSet<Entity>();
        private readonly List<Entity> _employeeEntityScratch = new List<Entity>();
        private readonly List<Entity> _employeeRemovalScratch = new List<Entity>();

        // Reused every update: the partition sorted into its three zones so each can be timed and
        // counted on its own.
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
        private uint _clientSweepId;
        private int _clientNextPage;
        private bool _clientSweepIntact;
        private bool _syncWasReady;
        private long _nextPendingPumpMs;

        private long _lastStatsMs;
        private long _sentBytes;
        private int _sentPages, _sentEntries, _priorityChanges, _priorityDrops, _captureSkips;
        private int _receivedPages, _droppedPages, _resolved, _unresolved, _ambiguous, _expired;
        private int _appliedCompanies, _correctedFields, _correctedResources;
        private int _correctedCompanyData, _correctedTradeCosts, _correctedEmployees;
        private int _correctedPropertyPrefabs, _alignedPropertyBuildRates;
        private int _createdCompanies, _retiredCompanies, _deferredActions, _cancelledDecisions;
        private int _hostLifecycleSignals, _clientLifecycleRepairs;

        private sealed class CachedEntry
        {
            public CompanyStatsEntry Entry;
            public uint LastSeenSweep;
        }

        private sealed class PendingEntry
        {
            public CompanyStatsEntry Entry;
            public uint SweepId;
            public long ExpiresMs;
            public long NextAttemptMs;
        }

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

            // Buildings a business can rent. The host sweeps these rather than its companies,
            // because "nobody rents this one" is the statement a client cannot work out alone.
            _properties = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<Building, Renter, PrefabRef,
                    global::Game.Objects.Transform, UpdateFrame>(),
                Any = SyncQuery.ReadOnly<CommercialProperty, IndustrialProperty, OfficeProperty,
                    StorageProperty, ExtractorProperty>(),
                None = SyncQuery.ReadOnly<Temp, Deleted>(),
            });

            // Deliberately the same shape as CompanyEconomyStatisticSystem's own query, so the
            // correction pass sees exactly the companies that system writes - no more, no fewer.
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

            // PropertySeeker is enableable, so this only contains companies whose local behaviour
            // has actively asked to find a building. The host owns that decision.
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
                if (service == null || !service.GameplaySyncReady)
                {
                    // A world-sync barrier closes GameplaySyncReady before installing a
                    // replacement world. Keep client authority held across that gap: briefly
                    // re-enabling the spawners is enough for them to open businesses this peer's
                    // own way before the first new page arrives.
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

                // Derived exactly as CompanyEconomyStatisticSystem derives it, one line above its
                // own job schedule. This is the partition whose figures were just recomputed.
                uint updateFrame = SimulationUtils.GetUpdateFrame(
                    _simulationSystem.frameIndex, CompanyUpdatesPerDay, UpdatePartitions);
                int partition = (int)(updateFrame % UpdatePartitions);

                if (session.Role == SessionRole.Host)
                {
                    DropIncomingPages();
                    ScanHostChanges(partition);
                }
                else
                {
                    // Normally CityState's every-frame pump has already resolved arrived pages.
                    // Pump once more as a harmless fallback before this partition is corrected.
                    PumpIncoming();
                    ApplyFigures(updateFrame);
                }
                ReportStats(session, service.NowMs);
            }
        }

        /// <summary>
        /// Kept engaged from the city-state pump as well as from <see cref="OnUpdate"/>. The
        /// GameSimulation phase stops ticking the moment a player pauses, so a client that left a
        /// session while paused would otherwise keep the spawners held forever.
        /// </summary>
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
            if (snapshot == null) return;
            lock (_incoming)
            {
                _incoming.Enqueue(snapshot);
                while (_incoming.Count > MaxIncomingPages)
                {
                    CompanyStatsSnapshot dropped;
                    if (!_incoming.TryDequeue(out dropped)) break;
                    _droppedPages++;
                }
            }
        }

        internal void ResetPending()
        {
            DrainForWorldChange();
            MultiplayerService service = Mod.Service;
            if (service != null && service.Session.Role == SessionRole.Client)
                ApplyLocalAuthority(service.Session);
            else if (service == null || !service.GameplaySyncReady)
                RestoreLocalAuthority();
        }

        internal void DrainForWorldChange()
        {
            lock (_incoming) SyncInbox.Clear(_incoming);
            _cache.Clear();
            _cacheScratch.Clear();
            _pending.Clear();
            PropertyRentIdentity discardedPending;
            while (_pendingOrder.TryDequeue(out discardedPending)) { }
            _priority.Clear();
            PropertyRentIdentity discardedPriority;
            while (_priorityOrder.TryDequeue(out discardedPriority)) { }
            _hostObserved.Clear();
            _hostEmployeeObserved.Clear();
            Array.Clear(_hostPartitionInitialized, 0, _hostPartitionInitialized.Length);
            Array.Clear(_hostPartitionCursor, 0, _hostPartitionCursor.Length);
            _dirty.Clear();
            _dirtyMembers.Clear();
            _stateDirty.Clear();
            _stateDirtyMembers.Clear();
            _stateRetryScratch.Clear();
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
            _employeeIdScratch.Clear();
            _resolvedEmployeeScratch.Clear();
            _desiredEmployeeEntities.Clear();
            _employeeEntityScratch.Clear();
            _employeeRemovalScratch.Clear();
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
            _clientSweepId = 0;
            _clientNextPage = 0;
            _clientSweepIntact = false;
            _nextPendingPumpMs = 0;
            _prefabIndex = new PrefabIndex(_prefabSystem, _prefabs);
        }

        private void DropIncomingPages()
        {
            if (_incoming.IsEmpty) return;
            lock (_incoming)
            {
                CompanyStatsSnapshot ignored;
                while (_incoming.TryDequeue(out ignored)) _droppedPages++;
            }
        }

        /// <summary>
        /// A building a business can rent. Warehouses and extractor properties are part of the
        /// native industrial property search and must not disappear from this channel merely
        /// because they carry StorageProperty or Owner in addition to their workplace marker.
        /// </summary>
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

        /// <summary>
        /// The business renting a building, or null. Households share the renter buffer in a mixed
        /// building and are channel 21's business, never this one's.
        /// </summary>
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

            // The shared lines go to whichever of the three zone topics is on, so a player who
            // only turned on "industrial" still sees the channel's own health.
            if (session.Role == SessionRole.Host)
                WriteToWorkplaceTopics("pages=" + _sentPages + ", entries=" + _sentEntries +
                                       ", bytes=" + _sentBytes + ", changed=" + _priorityChanges +
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
                                       _correctedEmployees + ", prefabCorrections=" +
                                       _correctedPropertyPrefabs + ", buildRatesAligned=" +
                                       _alignedPropertyBuildRates + ", deferred=" + _deferredActions +
                                       ", lifecycleRepairs=" + _clientLifecycleRepairs +
                                       ", cancelledLocalDecisions=" + _cancelledDecisions +
                                       ", tenancyDirty=" + _dirty.Count + ", stateDirty=" +
                                       _stateDirty.Count + ".");
                ReportZone(SyncZone.Commercial);
                ReportZone(SyncZone.Industrial);
                ReportZone(SyncZone.Office);
            }

            _sentPages = _sentEntries = _priorityChanges = _priorityDrops = _captureSkips = 0;
            _sentBytes = 0;
            _receivedPages = _droppedPages = _resolved = _unresolved = _ambiguous = _expired = 0;
            _appliedCompanies = _correctedFields = _correctedResources = 0;
            _correctedCompanyData = _correctedTradeCosts = _correctedEmployees = 0;
            _correctedPropertyPrefabs = _alignedPropertyBuildRates = 0;
            _createdCompanies = _retiredCompanies = _deferredActions = _cancelledDecisions = 0;
            _hostLifecycleSignals = _clientLifecycleRepairs = 0;
            Array.Clear(_zoneApplied, 0, _zoneApplied.Length);
            Array.Clear(_zoneOpened, 0, _zoneOpened.Length);
            Array.Clear(_zoneClosed, 0, _zoneClosed.Length);
        }

        /// <summary>One channel serves three zones, so its shared health line goes to each of the
        /// zone topics the player has actually asked for - and is built only once, and only if at
        /// least one of them is on.</summary>
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
