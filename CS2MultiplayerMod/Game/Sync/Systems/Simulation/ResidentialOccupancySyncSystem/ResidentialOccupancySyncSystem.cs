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
        // normal residential growth once the city reached a few hundred homes, leaving urgent
        // move-ins queued for minutes. Sixteen KiB remains far below the 240 KiB hard codec cap
        // while allowing the rolling baseline and the priority queue to make city-scale progress.
        private const int PageByteBudget = 16 * 1024;

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
        private const int PriorityPropertiesPerPage = 16;

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
        private const int HostDeparturesPerPage = 24;
        private const int HostCitizenDeparturesPerPage = 48;
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
        private int _rentActions;
        private int _refusedMoveIns;
        private int _forcedCompletions;
        private int _alignedBuildRates;
        private int _deferredForConstruction;
        private int _renamedEntities;
        private int _economyCorrections;
        private int _economyDeferred;

        private sealed class CachedProperty
        {
            public PropertyRentIdentity Identity;
            public Entity Prefab;
            public ulong Revision;
            public byte ConstructionSpeed;
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
                    ScanHostDepartures(service.NowMs);
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

        /// <summary>
        /// Kept engaged from the city-state pump as well as from <see cref="OnUpdate"/>. The
        /// GameSimulation phase stops ticking the moment a player pauses, so a client that leaves
        /// a session while paused would otherwise keep its household systems held forever.
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

        /// <summary>
        /// Called every simulation frame immediately before the native move-away consumer. The
        /// main occupancy system runs at a wider interval and can otherwise miss a short-lived
        /// MovingAway entity entirely.
        /// </summary>
        internal void ProcessHouseholdLifecycleBoundary()
        {
            MultiplayerService service = Mod.Service;
            if (service == null || !service.GameplaySyncReady) return;
            if (service.Session.Role == SessionRole.Host)
                ScanHostDepartures(service.NowMs);
            else
                CancelClientLifecycleDecisions();
        }

        /// <summary>
        /// HouseholdBehaviorSystem must run locally because it produces shopping needs and car
        /// demand. It also proposes moves. Remove only those proposals at the last safe boundary;
        /// retirements explicitly requested by the received host roster are whitelisted.
        /// </summary>
        private void CancelClientLifecycleDecisions()
        {
            // This runs every simulation frame. A large city proposes hundreds of these decisions
            // per frame, and cancelling them one entity at a time makes each removal its own
            // structural change; both cancellations are therefore issued in bulk.
            if (!_departingHouseholds.IsEmptyIgnoreFilter)
            {
                if (_authorizedMoveAways.Count == 0)
                {
                    EntityManager.RemoveComponent<global::Game.Agents.MovingAway>(
                        _departingHouseholds);
                }
                else
                {
                    NativeArray<Entity> departures =
                        _departingHouseholds.ToEntityArray(Allocator.Temp);
                    NativeList<Entity> cancelled =
                        new NativeList<Entity>(departures.Length, Allocator.Temp);
                    try
                    {
                        for (int i = 0; i < departures.Length; i++)
                        {
                            Entity household = departures[i];
                            if (_authorizedMoveAways.Contains(household)) continue;
                            cancelled.Add(household);
                        }
                        if (cancelled.Length > 0)
                            EntityManager.RemoveComponent<global::Game.Agents.MovingAway>(
                                cancelled.AsArray());
                    }
                    finally
                    {
                        cancelled.Dispose();
                        departures.Dispose();
                    }
                }
            }

            // PropertySeeker is enableable, so this query holds exactly the households whose flag
            // is set - including any that were departing above. Clearing the bits a chunk at a
            // time replaces one main-thread call per family.
            if (!_clientPropertySeekers.IsEmptyIgnoreFilter)
                EntityManager.SetComponentEnabled<global::Game.Agents.PropertySeeker>(
                    _clientPropertySeekers, false);

            if (_authorizedMoveAways.Count <= 4096) return;
            _authorizedMoveAwayScratch.Clear();
            foreach (Entity household in _authorizedMoveAways)
                if (!EntityManager.Exists(household) || EntityManager.HasComponent<Deleted>(household))
                    _authorizedMoveAwayScratch.Add(household);
            for (int i = 0; i < _authorizedMoveAwayScratch.Count; i++)
                _authorizedMoveAways.Remove(_authorizedMoveAwayScratch[i]);
            _authorizedMoveAwayScratch.Clear();
        }

        /// <summary>
        /// The channel's reset. Called both when a session ends and on an in-session world
        /// replacement, so authority is only handed back in the first case.
        /// </summary>
        internal void ResetPending()
        {
            DrainForWorldChange();
            MultiplayerService service = Mod.Service;
            if (service != null && service.Session.Role == SessionRole.Client)
                ApplyLocalAuthority(service.Session);
            else if (service == null || !service.GameplaySyncReady)
                RestoreLocalAuthority();
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
            RestoreAllStagedTransferLinks();
            _cache.Clear();
            _cacheScratch.Clear();
            _authorizedMoveAways.Clear();
            _authorizedMoveAwayScratch.Clear();
            ClearBuckets(_cacheBuckets);
            ClearBucketSets(_cacheBucketMembers);
            Array.Clear(_cacheBucketCursor, 0, _cacheBucketCursor.Length);
            _dirty.Clear();
            _dirtyMembers.Clear();
            _pending.Clear();
            PropertyRentIdentity discardedPending;
            while (_pendingOrder.TryDequeue(out discardedPending)) { }
            _pendingMoveIns.Clear();
            ulong discardedMoveIn;
            while (_pendingMoveInOrder.TryDequeue(out discardedMoveIn)) { }
            _stagedTransfers.Clear();
            _stagedTransferCooldownUntil.Clear();
            _stagedTransferScratch.Clear();
            _pendingCitizenRetirementIds.Clear();
            ulong discardedCitizenRetirement;
            while (_pendingCitizenRetirements.TryDequeue(out discardedCitizenRetirement)) { }
            _settling.Clear();
            _unreachableSince.Clear();
            _unboundHouseholdSince.Clear();
            _unboundCitizenSince.Clear();
            _bootstrapHouseholdIndex.Clear();
            _bootstrapCitizenIndex.Clear();
            _bootstrapIdentityIndexBuilt = false;
            _unreachableSeen.Clear();
            _localHouseholds.Clear();
            _memberScratch.Clear();
            _claimedHouseholds.Clear();
            _claimedCitizens.Clear();
            _claimedPets.Clear();
            _wantedHouseholdIds.Clear();
            _wantedCitizenIds.Clear();
            _missingPetPrefabs.Clear();
            _localVehiclePrefabCounts.Clear();
            _matchedVehiclePrefabCounts.Clear();
            _vehicleSpawnWarnings.Clear();
            _arrivalSources.Clear();
            _settlingScratch.Clear();
            _appliedThisUpdate.Clear();
            _reapply.Clear();
            ClearIdentityState();
            ClearRentAuthorityState();
            _economyCursor = 0;
            _applyWarned = false;
            _arrivalSourceWarned = false;
            _nextPendingPumpMs = 0;
            _prefabIndex = new PrefabIndex(_prefabSystem, _prefabs);
            _citizenCreationPrefab = Entity.Null;

            _hostObserved.Clear();
            ClearBuckets(_hostObservedBuckets);
            Array.Clear(_hostBucketInitialized, 0, _hostBucketInitialized.Length);
            Array.Clear(_hostBucketCursor, 0, _hostBucketCursor.Length);
            _traceSentRosterHashes.Clear();
            _traceReceivedRosterHashes.Clear();
            _tracePlacedHouseholds.Clear();
            _priority.Clear();
            PropertyRentIdentity discardedPriority;
            while (_priorityOrder.TryDequeue(out discardedPriority)) { }
            _hostDepartures.Clear();
            _hostDepartureOrderMembers.Clear();
            ulong discardedDeparture;
            while (_hostDepartureOrder.TryDequeue(out discardedDeparture)) { }
            _hostCitizenDepartures.Clear();
            _hostCitizenDepartureOrderMembers.Clear();
            ulong discardedCitizenDeparture;
            while (_hostCitizenDepartureOrder.TryDequeue(out discardedCitizenDeparture)) { }
            _hostCitizens.Clear();
            _hostCitizenOrderMembers.Clear();
            ulong discardedTrackedCitizen;
            while (_hostCitizenOrder.TryDequeue(out discardedTrackedCitizen)) { }
            _hostHouseholds.Clear();
            _hostHouseholdOrderMembers.Clear();
            ulong discardedTrackedHousehold;
            while (_hostHouseholdOrder.TryDequeue(out discardedTrackedHousehold)) { }
            _hostHouseholdCitizens.Clear();
            _clientSweepId = 0;
            _clientNextPage = 0;
            _clientSweepIntact = false;
            _hostCaptureRevision = 1;
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
            _captureSweepHadSkips = false;
            _captureBaselineNeedsEmptyPage = false;
        }

        private ulong NextHostRevision()
        {
            ulong revision = _hostCaptureRevision++;
            if (revision != 0) return revision;
            revision = _hostCaptureRevision++;
            return revision == 0 ? 1UL : revision;
        }

        private ulong LastHostRevision()
        {
            ulong revision = _hostCaptureRevision - 1;
            return revision == 0 ? 1UL : revision;
        }

        private void AdvanceHostSweep()
        {
            _capturePageIndex = 0;
            _captureSweepId = unchecked(_captureSweepId + 1);
            if (_captureSweepId == 0) _captureSweepId = 1;
            _captureSweepHadSkips = false;
            _captureBaselineNeedsEmptyPage = false;
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
                Diagnostics.SyncLog.Write(Diagnostics.LogTopic.Residential, "Occupancy/30s host: pages=" + _sentPages + ", properties=" +
                            _sentProperties + ", bytes=" + _sentBytes + ", clients=" + clients +
                            ", estimatedFanoutBytes=" + _sentBytes * clients +
                            ", transportPendingBytes=" + session.PendingSendBytes +
                            ", changedPriority=" + _priorityChanges + ", priorityQueued=" +
                            _priority.Count + ", priorityDropped=" + _priorityDrops +
                            ", departuresTracked=" + _hostDepartures.Count +
                            ", citizenDeparturesTracked=" + _hostCitizenDepartures.Count +
                            ", captureSkipped=" + _captureSkips + ", observed=" +
                            _observedProperties + ".");
            }
            else
            {
                Diagnostics.SyncLog.Write(Diagnostics.LogTopic.Residential, "Occupancy/30s client: pages=" + _receivedPages +
                            ", queueDropped=" + _droppedPages + ", cached=" + _cache.Count +
                            ", pending=" + _pending.Count + ", resolved=" + _resolved +
                            ", unresolved=" + _unresolved + ", ambiguous=" + _ambiguous +
                            ", expired=" + _expired + ", stale=" + _stalePages +
                            ", pruned=" + _pruned + ", cacheDropped=" + _cacheDrops +
                            ", appliedProperties=" + _appliedProperties + ", households +" +
                            _createdHouseholds + "/-" + _retiredHouseholds + ", citizens +" +
                            _createdCitizens + "/-" + _removedCitizens + "/~" +
                            _rewrittenCitizens + ", pets +" + _createdPets + ", renamed=" +
                            _renamedEntities + ", vehicles +" + _createdVehicles +
                            ", rentActions=" + _rentActions +
                            ", refusedMoveIns=" + _refusedMoveIns + ", buildRatesAligned=" +
                            _alignedBuildRates + ", forcedCompletions=" + _forcedCompletions +
                            ", deferredForConstruction=" + _deferredForConstruction +
                            ", economyCorrections=" + _economyCorrections +
                            "/deferred " + _economyDeferred +
                            ", pendingMoveIns=" + _pendingMoveIns.Count + ", dirty=" +
                            _dirty.Count + ".");
            }
            _sentPages = _sentProperties = _priorityChanges = _priorityDrops = _captureSkips = 0;
            _observedProperties = 0;
            _sentBytes = 0;
            _receivedPages = _droppedPages = _resolved = _unresolved = _ambiguous = 0;
            _expired = _stalePages = _pruned = _cacheDrops = _appliedProperties = 0;
            _createdHouseholds = _createdCitizens = _createdPets = _createdVehicles = 0;
            _retiredHouseholds = _removedCitizens = _rewrittenCitizens = 0;
            _rentActions = _refusedMoveIns = 0;
            _forcedCompletions = _alignedBuildRates = _deferredForConstruction = 0;
            _renamedEntities = _economyCorrections = _economyDeferred = 0;
        }
    }
}
