using System.Collections.Concurrent;
using System.Collections.Generic;
using Game;
using Game.Buildings;
using Game.Common;
using Game.Objects;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Core.Sync;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.Infrastructure;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Replicates the buildings the zoning simulation grows on its own - houses, shops, factories,
    /// offices - rather than the ones a player places.
    ///
    /// These cannot be kept in step by running the same simulation on both machines. The spawner
    /// draws its building, its variant and its level-up target from a random stream seeded from the
    /// machine's own clock, so two cities with identical roads, zoning and demand still grow
    /// different buildings. Replication is therefore one-way: the host's simulation is the only one
    /// allowed to decide, and the peers are told what it decided.
    ///
    /// That also removes the whole class of simultaneous-creation conflicts. Two players cannot both
    /// grow a building on one lot, because only one machine grows anything. What remains is a lot
    /// whose state moved on before the host's decision arrived - handled in Realize.cs by refusing a
    /// spawn that would overlap something already standing.
    /// </summary>
    public partial class GrowableSyncSystem : GameSystemBase
    {
        /// <summary>How often the host looks for level changes. They are rare; the query is small.</summary>
        private const long LevelScanIntervalMs = 500;

        /// <summary>
        /// Buildings the rolling abandonment/condemnation scan examines per pass. It exists to
        /// notice a state the host never announced, so a very large city may take longer to come
        /// all the way round without anything being missed; every real transition still reaches
        /// the wire through its own event-driven capture.
        /// </summary>
        private const int MaxStateBuildingsPerScan = 256;

        /// <summary>Maximum time for a generated building to acquire its native road link.</summary>
        private const long RealizationValidationWindowMs = 15000;

        private const int MaxPendingStateCorrections = 512;
        private const int MaxRealizationValidations = 512;

        /// <summary>
        /// How long a completed sequence number is remembered. Long enough to cover a reconnect
        /// burst, short enough that the set stays small on a city that grows for hours.
        /// </summary>
        private const long ReplayWindowMs = 120000;

        /// <summary>
        /// Realizes per frame. The game itself never grows more than three buildings per spawner
        /// update, so a backlog this size only ever appears after a stall - and draining it flat out
        /// would spike the frame it drains on.
        /// </summary>
        private const int MaxRealizePerFrame = 8;

        /// <summary>
        /// How long a building this client asked for stays recognisable as ours. A definition
        /// becomes an entity a phase or two later, so the window only has to outlast that.
        /// </summary>
        private const long SelfRealizedWindowMs = 15000;

        private const int MaxSelfRealized = 256;

        private const long PlayerPlacedPruneIntervalMs = 30000;

        /// <summary>Cap on the host's level-change memory, so a long session cannot grow it without bound.</summary>
        private const int MaxTrackedLevelChanges = 4096;

        private readonly ConcurrentQueue<SimulationCommandMessage> _incoming =
            new ConcurrentQueue<SimulationCommandMessage>();

        /// <summary>Idempotence: a redelivered command must not build a second house.</summary>
        private readonly OperationReplayWindow<uint> _applied = new OperationReplayWindow<uint>();

        /// <summary>
        /// Positions this client has asked the build pipeline for, so the building that appears
        /// there is recognised as the host's rather than as one this machine grew.
        /// </summary>
        /// <summary>
        /// Set by <see cref="SyncRealizeSystem"/> while the net pipeline cannot deliver roads. A
        /// growable waiting to join its road graph is waiting on exactly that pipeline.
        /// </summary>
        public bool NetworkDependenciesHeld;

        private sealed class PendingRealizedSpawn
        {
            public Entity Prefab;
            public float3 Position;
            public long Expiry;
            public GrowableLifecycleCommand Command;
        }

        private sealed class PendingStateCorrection
        {
            public GrowableLifecycleCommand Command;
            public long Expiry;
        }

        private sealed class RealizationValidation
        {
            public Entity Building;
            public Entity Prefab;
            public float3 Position;
            public long Expiry;
        }

        private readonly List<PendingRealizedSpawn> _selfRealized =
            new List<PendingRealizedSpawn>();
        private readonly List<PendingStateCorrection> _pendingStateCorrections =
            new List<PendingStateCorrection>();
        private readonly HashSet<uint> _pendingStateSequences = new HashSet<uint>();
        private readonly List<RealizationValidation> _realizationValidations =
            new List<RealizationValidation>();

        /// <summary>
        /// Spawnable-prefab entities proven to have come from a player's object tool. The prefab
        /// alone cannot make that distinction: specialized-industry facilities deliberately use a
        /// level-one SpawnableBuildingData building inside their placed object/area graph.
        /// </summary>
        private readonly HashSet<Entity> _playerPlacedGrowables = new HashSet<Entity>();
        private readonly List<Entity> _stalePlayerPlacedGrowables = new List<Entity>();

        /// <summary>Host-side: the level-up target already announced per building.</summary>
        private readonly Dictionary<Entity, Entity> _announcedLevelChange = new Dictionary<Entity, Entity>();
        private readonly List<Entity> _staleLevelChanges = new List<Entity>();

        private sealed class HostConstructionObservation
        {
            public byte Progress;
            public byte Speed;
        }

        private readonly Dictionary<Entity, HostConstructionObservation> _hostConstruction =
            new Dictionary<Entity, HostConstructionObservation>();
        private readonly HashSet<Entity> _constructionSeen = new HashSet<Entity>();
        private readonly List<Entity> _constructionScratch = new List<Entity>();
        private readonly Dictionary<Entity, byte> _hostStateFlags = new Dictionary<Entity, byte>();
        private int _stateScanBucket;
        private int _stateScanCursor;

        private uint _sequence;
        private long _lastLevelScanMs;
        private long _lastStatsMs;
        private long _lastPlayerPlacedPruneMs;

        // Counters behind the periodic summary. Individual events are logged at verbose level; the
        // summary is what a normal log carries, so a desync report always shows the shape of the
        // traffic even when verbose logging was off.
        private int _sentSpawn, _sentLevel, _sentRemove, _sentState;
        private int _gotSpawn, _gotLevel, _gotRemove, _gotState;
        private int _duplicates, _conflicts, _unmatched, _unknownPrefab, _rejectedLocal;

        private PrefabSystem _prefabSystem;
        private PrefabIndex _prefabIndex;
        private ObjectSearch _objectSearch;
        private BuildSyncSystem _buildSync;
        private DeleteSyncSystem _deleteSync;
        private CommandObserver _observer;

        private EntityQuery _createdBuildings;
        private EntityQuery _deletedBuildings;
        private EntityQuery _levelChanging;
        private EntityQuery _stateBuildings;

        /// <summary>Set by <see cref="SyncRealizeSystem"/> while remote terrain edits are backlogged.</summary>
        public bool DeferForTerrain;

        protected override void OnCreate()
        {
            base.OnCreate();

            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _prefabIndex = new PrefabIndex(_prefabSystem, GetEntityQuery(ComponentType.ReadOnly<PrefabData>()));
            _objectSearch = new ObjectSearch(
                World.GetOrCreateSystemManaged<global::Game.Objects.SearchSystem>());
            _buildSync = World.GetOrCreateSystemManaged<BuildSyncSystem>();
            _deleteSync = World.GetOrCreateSystemManaged<DeleteSyncSystem>();

            // Loading a world does not tag its entities Created, so a join never re-broadcasts the
            // city the client just downloaded. Owner excludes lot content owned by a building.
            _createdBuildings = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<Created, Building, PrefabRef,
                    global::Game.Objects.Transform>(),
                None = SyncQuery.ReadOnly<Temp, Deleted, Owner>(),
            });

            _deletedBuildings = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<Deleted, Building, PrefabRef,
                    global::Game.Objects.Transform>(),
                None = SyncQuery.ReadOnly<Temp, Owner>(),
            });

            // A building only carries UnderConstruction while it is being built or re-levelled, so
            // this query holds a handful of entities even in a large city.
            _levelChanging = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<UnderConstruction, Building, PrefabRef,
                    global::Game.Objects.Transform>(),
                None = SyncQuery.ReadOnly<Temp, Deleted>(),
            });

            // Building state is ordinary component data, not a Created/Deleted lifecycle edge.
            // Scan one native UpdateFrame partition at a time and announce absolute transitions.
            _stateBuildings = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<Building, BuildingCondition, PrefabRef,
                    global::Game.Objects.Transform, UpdateFrame>(),
                None = SyncQuery.ReadOnly<Temp, Deleted, Owner>(),
            });

            _observer = SyncObserverBinding.Bind(
                () => new CommandObserver(_incoming, GrowableLifecycleCommand.Id)
                    {
                        MaxBodyBytes = GrowableLifecycleCommand.MaxEncodedBytes,
                    },
                DrainQueue);
        }

        protected override void OnDestroy()
        {
            SyncObserverBinding.Unbind(_observer, DrainQueue);
            RestoreLocalAuthority();
            base.OnDestroy();
        }

        private void DrainQueue()
        {
            if (!_incoming.IsEmpty) SyncInbox.Clear(_incoming);
            _selfRealized.Clear();
            _pendingStateCorrections.Clear();
            _pendingStateSequences.Clear();
            _realizationValidations.Clear();
            _playerPlacedGrowables.Clear();
            _stalePlayerPlacedGrowables.Clear();
            _lastPlayerPlacedPruneMs = 0;
            _lastGrowableRealizeMs = 0;
            _lastValidationTickMs = 0;
            NetworkDependenciesHeld = false;
            // A replaced world arrives complete. Anything still queued for the old one refers to
            // buildings that no longer exist, and every sequence number belongs to a city that is
            // gone: keeping either would apply a stale decision to a fresh world.
            _applied.Clear();
            _announcedLevelChange.Clear();
            _hostConstruction.Clear();
            _constructionSeen.Clear();
            _constructionScratch.Clear();
            _hostStateFlags.Clear();
            _stateScanBucket = 0;
            _stateScanCursor = 0;
        }

        /// <summary>
        /// Capture only. Realization runs from <see cref="SyncRealizeSystem"/> in ToolUpdate, the
        /// one phase where a creation definition becomes a building.
        /// </summary>
        protected override void OnUpdate()
        {
            using (Diagnostics.SyncProfiler.Measure("Growable"))
            {
                MultiplayerService service = Mod.Service;
                if (service == null) return;

                if (!service.GameplaySyncReady)
                {
                    DrainQueue();
                    RestoreLocalAuthority();
                    return;
                }

                MultiplayerSession session = service.Session;
                long now = service.NowMs;
                PrunePlayerPlacedGrowables(now);
                ApplyLocalAuthority(session);

                if (session.Role != SessionRole.Host)
                {
                    // ModificationEnd, not the ToolUpdate realize pass: the Created tag this reads is
                    // written by the object pipeline during the Modification phases and is gone again
                    // by the next frame's ToolUpdate.
                    RejectLocallyGrownBuildings(now);
                    ValidateRealizedBuildings(now);
                    return;
                }

                CaptureCreated(session, now);
                CaptureRemoved(session, now);
                if (now - _lastLevelScanMs >= LevelScanIntervalMs)
                {
                    _lastLevelScanMs = now;
                    CaptureLevelChanges(session, now);
                    CaptureConstructionChanges(session, now);
                    CaptureStateChanges(session, now);
                }
                ReportStats(session, now);
            }
        }

        /// <summary>
        /// True for a prefab eligible for simulation growth. This is only the prefab half of the
        /// decision: specialized-industry placements can use the same data, so their live entity
        /// origin is resolved by <see cref="IsAutonomousGrowable"/>.
        /// </summary>
        private bool IsGrowablePrefab(Entity prefab) =>
            prefab != Entity.Null && EntityManager.Exists(prefab) &&
            EntityManager.HasComponent<SpawnableBuildingData>(prefab) &&
            !EntityManager.HasComponent<SignatureBuildingData>(prefab);

        /// <summary>
        /// True only for a simulation-authored zoned building. A specialized-industry placement
        /// can use the same SpawnableBuildingData as a growable, so origin and the committed
        /// attachment/area graph decide before either capture or client-side rejection runs.
        /// </summary>
        private bool IsAutonomousGrowable(Entity entity, long now)
        {
            if (entity == Entity.Null || !EntityManager.Exists(entity) ||
                !EntityManager.HasComponent<PrefabRef>(entity)) return false;
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab;
            if (!IsGrowablePrefab(prefab)) return false;
            if (_playerPlacedGrowables.Contains(entity)) return false;

            if (_buildSync != null && _buildSync.ConsumePlayerPlacedSpawnable(entity, now))
            {
                _playerPlacedGrowables.Add(entity);
                SyncLog.Detail(LogTopic.Buildings,
                    "GrowableSync: excluded player-placed spawnable '" + PrefabIndexSafeName(prefab) +
                    "' from autonomous lifecycle sync.");
                return false;
            }
            return true;
        }

        private void PrunePlayerPlacedGrowables(long now)
        {
            if (_playerPlacedGrowables.Count == 0 ||
                (_lastPlayerPlacedPruneMs != 0 &&
                 now - _lastPlayerPlacedPruneMs < PlayerPlacedPruneIntervalMs)) return;
            _lastPlayerPlacedPruneMs = now;

            _stalePlayerPlacedGrowables.Clear();
            foreach (Entity entity in _playerPlacedGrowables)
                if (!EntityManager.Exists(entity)) _stalePlayerPlacedGrowables.Add(entity);
            for (int i = 0; i < _stalePlayerPlacedGrowables.Count; i++)
                _playerPlacedGrowables.Remove(_stalePlayerPlacedGrowables[i]);
            _stalePlayerPlacedGrowables.Clear();
        }

        private byte CaptureStateFlags(Entity entity)
        {
            byte flags = 0;
            if (EntityManager.HasComponent<Abandoned>(entity)) flags |= GrowableLifecycleCommand.StateAbandoned;
            if (EntityManager.HasComponent<Condemned>(entity)) flags |= GrowableLifecycleCommand.StateCondemned;
            if (EntityManager.HasComponent<Destroyed>(entity)) flags |= GrowableLifecycleCommand.StateDestroyed;
            return flags;
        }

        private int CaptureCondition(Entity entity) =>
            EntityManager.HasComponent<BuildingCondition>(entity)
                ? EntityManager.GetComponentData<BuildingCondition>(entity).m_Condition
                : 0;

        private void Send(MultiplayerSession session, GrowableLifecycleCommand command)
        {
            command.Sequence = unchecked(++_sequence);
            session.SendCommand(0, GrowableLifecycleCommand.Id, command.Encode());
        }

        /// <summary>
        /// A periodic one-liner rather than a line per building: at full speed the simulation can
        /// grow eleven buildings a second, and logging each of those individually is what turns a
        /// desync report into an unreadable file.
        /// </summary>
        private void ReportStats(MultiplayerSession session, long now)
        {
            if (_lastStatsMs == 0) { _lastStatsMs = now; return; }
            if (now - _lastStatsMs < 30000) return;
            _lastStatsMs = now;

            if (_sentSpawn + _sentLevel + _sentRemove + _sentState == 0) return;
            SyncLog.Detail(LogTopic.Buildings, "GrowableSync/30s host: spawn=" + _sentSpawn +
                " level=" + _sentLevel + " remove=" + _sentRemove + " state=" + _sentState + ".");
            _sentSpawn = _sentLevel = _sentRemove = _sentState = 0;
        }

        private void ReportClientStats(long now)
        {
            if (_lastStatsMs == 0) { _lastStatsMs = now; return; }
            if (now - _lastStatsMs < 30000) return;
            _lastStatsMs = now;

            if (_gotSpawn + _gotLevel + _gotRemove + _gotState + _duplicates + _conflicts +
                _unmatched + _unknownPrefab + _rejectedLocal == 0) return;
            SyncLog.Detail(LogTopic.Buildings, "GrowableSync/30s client: spawn=" + _gotSpawn +
                " level=" + _gotLevel + " remove=" + _gotRemove + " state=" + _gotState +
                " duplicate=" + _duplicates + " conflict=" + _conflicts + " unmatched=" + _unmatched +
                " unknownPrefab=" + _unknownPrefab + " rejectedLocal=" + _rejectedLocal + ".");
            _gotSpawn = _gotLevel = _gotRemove = _gotState = 0;
            _duplicates = _conflicts = _unmatched = _unknownPrefab = _rejectedLocal = 0;
        }
    }
}
