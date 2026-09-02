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
    /// Every page the host sends is an absolute, revisioned roster for the properties it names: the
    /// households in the building, the people in them, their money, daily economy and rent. A
    /// client resolves each property by the same portable identity the rest of the mod uses
    /// (prefab name plus world anchor) and then makes its own building match.
    ///
    /// Host entity handles travel only as opaque, world-epoch-scoped identity keys. They are never
    /// resolved as local entity handles. This lets a family move between properties, or be replaced
    /// by another family at the same renter-buffer position, without mutating one local entity into
    /// a different remote person. Absolute pages remain idempotent, and monotonic revisions make
    /// delayed priority pages harmless.
    ///
    /// The client stops authoring occupancy while this runs — see Authority.cs.
    /// </summary>
    // The budgets, state and cached records the whole system works from, and its per-update cycle.
    //
    // The rest is split by side: Capture*.cs is what a host sends, Realize*.cs is what a client
    // does with it, Identity.cs is how the two agree on which property is which, and Authority.cs
    // is what a client stops doing while the host owns occupancy. The lifecycle boundary, page
    // plumbing and stats are in Cycle.cs.
    public partial class ResidentialOccupancySyncSystem : GameSystemBase
    {
        private const int UpdatePartitions = 16;

        /// <summary>
        /// Simulation frames between updates. Must be a power of two: the game gates a system's
        /// update with <c>frameIndex &amp; (interval - 1)</c>. One of sixteen partitions is examined
        /// per update, so the whole city is revisited every 1024 simulation frames.
        /// </summary>
        private const int UpdateIntervalFrames = 64;

        // Remote growables are created at the transmitted XZ exactly. A generous four-metre
        // fallback can claim the neighbouring half-lot while the intended building is still in
        // the creation pipeline, allowing alternating roster pages to overwrite one local house.
        // Half a metre still absorbs float noise without crossing a zone-cell boundary.
        private const float AnchorMatchDistance = 0.5f;
        private const float AnchorSearchRadius = 8f;
        private const float AmbiguousDistanceEpsilon = 0.01f;

        /// <summary>
        /// Soft byte budget for one page. Pages go out at the city-state cadence (~1 Hz), so this
        /// is also roughly the per-client bandwidth this feature costs.
        /// </summary>
        // One occupancy page is emitted per city-state snapshot. Four KiB could not keep up with
        // A dense property is atomic: all of its households and citizens have to travel together.
        // The former 16 KiB target therefore sent only three or four towers per second and the
        // changed-property queue grew without bound. Use most of the already validated 240 KiB
        // codec allowance; the remaining headroom is for lifecycle records and size estimation
        // conservatism, and the client still applies structural changes through separate budgets.
        private const int PageByteBudget = 224 * 1024;

        private const int MaxIncomingPages = 8;
        private const int MaxPumpPages = 2;
        private const int MaxCachedProperties = 131072;
        private const int MaxPendingIdentities = 4096;
        private const int MaxPendingMoveIns = 4096;
        private const int MaxStagedTransfers = 4096;
        // A lifecycle wave must survive long enough to rotate across the wire without evicting its
        // first records. This is deliberately city-scale rather than a normal-frame estimate.
        private const int MaxTrackedDepartures = 131072;
        private const int MaxTrackedHouseholdChecksPerUpdate = 1024;
        private const int MaxTrackedCitizenChecksPerUpdate = 2048;
        private const long DepartureRetentionMs = 900000;
        private const int MaxMoveInFinalizationsPerUpdate = 256;
        private const int MaxPendingRetriesPerPump = 128;
        private const long ResolveRetryMs = 5000;
        private const long ResolveTimeoutMs = 300000;
        private const int MaxPriorityProperties = 4096;
        private const int PriorityPropertiesPerPage = 64;

        // Properties examined by the rolling change detector in one update, and cached properties
        // reconciled by the rolling client partition in one update. Both walks used to cover a
        // whole sixteenth of the city per update, so their cost grew with the city until a
        // quarter-million residents turned each one into a visible hitch every second. A ceiling
        // keeps them flat: a very large city takes proportionally longer to complete one rotation.
        // Nothing urgent rides on that rotation - a move-in or move-out reaches the wire through
        // the RentersUpdated event in the same frame it happens, and a page that changes a
        // property reconciles it immediately through the dirty queue.
        private const int MaxPropertiesObservedPerUpdate = 256;
        private const int MaxCachedPropertiesWalkedPerUpdate = 256;
        // Retained lifecycle records rotate across pages rather than consuming the whole soft
        // page budget. Leave enough guaranteed room to drain several just-occupied properties per
        // snapshot while still advancing the baseline by at least one property.
        // In the reported dense city more than 27k household and 41k citizen tombstones were
        // retained. At 24/48 records per second a household record could expire before completing
        // one rotation. Eight KiB carries both maximum batches and closes every lifecycle edge
        // several times inside the retention window.
        private const int HostDeparturesPerPage =
            ResidentialOccupancySnapshot.MaxDeparturesPerPage;
        private const int HostCitizenDeparturesPerPage =
            ResidentialOccupancySnapshot.MaxCitizenDeparturesPerPage;
        private const int PriorityByteBudget = PageByteBudget * 3 / 4;

        // Per-update work ceilings. Structural changes are the expensive part, so they are capped
        // well below the page rate; anything left over is picked up by the next update.
        private const int MaxPropertiesAppliedPerUpdate = 96;
        private const int MaxHouseholdsCreatedPerUpdate = 12;
        private const int MaxCitizensCreatedPerUpdate = 48;
        private const int MaxVehiclesCreatedPerUpdate = 24;
        private const int MaxCitizensRetiredPerUpdate = 48;
        private const int MaxHouseholdsRetiredPerUpdate = 12;

        private const long StatsIntervalMs = 30000;

        private readonly ConcurrentQueue<ResidentialOccupancySnapshot> _incoming =
            new ConcurrentQueue<ResidentialOccupancySnapshot>();
        private readonly Dictionary<Entity, CachedProperty> _cache =
            new Dictionary<Entity, CachedProperty>();
        private readonly List<Entity>[] _cacheBuckets = CreateBuckets();
        private readonly HashSet<Entity>[] _cacheBucketMembers = CreateBucketSets();
        private readonly int[] _cacheBucketCursor = new int[UpdatePartitions];
        private readonly List<Entity> _dirty = new List<Entity>();
        private readonly HashSet<Entity> _dirtyMembers = new HashSet<Entity>();
        private readonly Dictionary<PropertyRentIdentity, PendingProperty> _pending =
            new Dictionary<PropertyRentIdentity, PendingProperty>();
        private readonly ConcurrentQueue<PropertyRentIdentity> _pendingOrder =
            new ConcurrentQueue<PropertyRentIdentity>();
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

        // Host-side change detection. The rolling baseline is always sent; these entries only
        // shorten the time from an occupancy change to the page that carries it.
        private readonly Dictionary<Entity, HostObserved> _hostObserved =
            new Dictionary<Entity, HostObserved>();
        private readonly List<Entity>[] _hostObservedBuckets = CreateBuckets();
        private readonly bool[] _hostBucketInitialized = new bool[UpdatePartitions];
        private readonly int[] _hostBucketCursor = new int[UpdatePartitions];
        private readonly Dictionary<Entity, int> _traceSentRosterHashes =
            new Dictionary<Entity, int>();
        private readonly Dictionary<PropertyRentIdentity, int> _traceReceivedRosterHashes =
            new Dictionary<PropertyRentIdentity, int>();
        private readonly Dictionary<ulong, PropertyRentIdentity> _tracePlacedHouseholds =
            new Dictionary<ulong, PropertyRentIdentity>();
        private readonly Dictionary<PropertyRentIdentity, Entity> _priority =
            new Dictionary<PropertyRentIdentity, Entity>();
        private readonly ConcurrentQueue<PropertyRentIdentity> _priorityOrder =
            new ConcurrentQueue<PropertyRentIdentity>();
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
        private uint _clientSweepId;
        private int _clientNextPage;
        private bool _clientSweepIntact;
        private bool _syncWasReady;
        private long _nextPendingPumpMs;

        private long _lastStatsMs;
        private long _sentBytes;
        private int _sentPages;
        private int _sentProperties;
        private int _priorityChanges;
        private int _priorityDrops;
        private int _captureSkips;
        private int _observedProperties;
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
        private int _feeInputCorrections;
        private int _feeInputDeferred;

        private sealed class CachedProperty
        {
            public PropertyRentIdentity Identity;
            public Entity Prefab;
            public ulong Revision;
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

        private sealed class PendingProperty
        {
            public OccupancyProperty Property;
            public uint SweepId;
            public long ExpiresMs;
            public long NextAttemptMs;
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

            /// <summary>
            /// Set when a renter event queued this property: the stored hash describes a roster
            /// that no longer exists, and the next rolling pass must re-baseline it without
            /// treating the difference as a second, independent change.
            /// </summary>
            public bool Stale;
        }

        private sealed class HostDeparture
        {
            public ulong Revision;
            public long ExpiresMs;
            public bool Unhoused;
        }

        // A struct: a city of a quarter of a million residents keeps one of these per person, and
        // a boxed object each would be a large permanently live heap graph for the GC to walk.
        private struct HostCitizenObservation
        {
            public Entity Entity;
            public ulong HouseholdId;
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
            // Households nothing can ever house again on a client. Tourists and commuters are a
            // different simulation with their own lifecycle and are never ours to retire; a
            // household still carrying CurrentBuilding is mid-arrival and has not asked for a home
            // yet.
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
            // PropertySeeker is enableable, so this query only contains households whose local
            // behaviour has actively asked to find a property. The host owns that decision.
            _clientPropertySeekers = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<global::Game.Citizens.Household,
                    global::Game.Agents.PropertySeeker>(),
                None = SyncQuery.ReadOnly<Deleted, Temp, global::Game.Citizens.TouristHousehold,
                    global::Game.Citizens.CommuterHousehold>(),
            });
            // PropertyProcessingSystem emits this exact event whenever a renter is added to or
            // removed from a property. A dedicated every-frame boundary consumes the signal so a
            // second family does not have to wait for the slow rolling property scan.
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
            if (service == null || !service.GameplaySyncReady)
            {
                // A world-sync barrier closes GameplaySyncReady before installing a replacement
                // world. Keep client authority held throughout that gap; briefly re-enabling the
                // lifecycle systems is enough for them to create or evict a family before the
                // first new roster arrives.
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

            int bucket = (int)(SimulationUtils.GetUpdateFrameWithInterval(
                _simulationSystem.frameIndex, UpdateIntervalFrames, UpdatePartitions) %
                UpdatePartitions);

            // Two sibling scopes rather than one around the method: the branches are mutually
            // exclusive, so the profiler's total stays a sum of what it lists.
            if (session.Role == SessionRole.Host)
            {
                using (Diagnostics.SyncProfiler.Measure("Occupancy.HostScan", Diagnostics.SyncZone.Residential))
                {
                    DropIncomingPages();
                    // Departures are sampled by ResidentialOccupancyDepartureCaptureSystem, which
                    // sits directly in front of the native executor at that executor's own
                    // interval. Repeating the walk here only ever re-read a query it had already
                    // drained on a more recent frame.
                    ScanTrackedHostHouseholds(service.NowMs);
                    ScanTrackedHostCitizens(service.NowMs);
                    ScanHostChanges(bucket);
                }
            }
            else
            {
                using (Diagnostics.SyncProfiler.Measure("Occupancy.Apply", Diagnostics.SyncZone.Residential))
                {
                    // Normally the city-state pump has already turned every arrived page into
                    // cache entries. Pump once more as a harmless fallback before this bucket
                    // is consumed.
                    PumpIncoming();
                    ApplyPending(bucket);
                }
            }
            ReportStats(session, service.NowMs);
        }
    }
}
