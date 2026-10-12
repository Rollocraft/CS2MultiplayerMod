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
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Core.Sync;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.Infrastructure;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Replicates the buildings zoning grows on its own. The spawner draws building, variant and level
    /// from a clock-seeded random stream, so the host alone decides and peers are told. A spawn that
    /// would overlap something already standing is refused (see Realize.cs).
    /// </summary>
    public partial class GrowableSyncSystem : GameSystemBase, IRealizeStage
    {
        /// <summary>How often the host looks for level changes. They are rare; the query is small.</summary>
        private const long LevelScanIntervalMs = 500;

        /// <summary>Per-pass ceiling of the rolling state scan; real transitions are event-driven.</summary>
        private const int MaxStateBuildingsPerScan = 256;

        private const int MaxPendingStateCorrections = 512;
        private const long RetryIntervalMs = 500;
        private const int MaxStateRetriesPerFrame = 16;
        private int _stateRetryCursor;

        /// <summary>Covers a reconnect burst while keeping the set small.</summary>
        private const long ReplayWindowMs = 120000;

        /// <summary>Structural work per frame; condition/progress samples have their own budget.</summary>
        private const int MaxRealizePerFrame = 8;

        /// <summary>A definition becomes an entity a phase or two later; this only has to outlast that.</summary>
        private const long SelfRealizedWindowMs = 15000;

        private const int MaxSelfRealized = 256;

        private const long PlayerPlacedPruneIntervalMs = 30000;

        /// <summary>Cap on the host's level-change memory, so a long session cannot grow it without bound.</summary>
        private const int MaxTrackedLevelChanges = 4096;

        private readonly GrowableCommandInbox _incoming = new GrowableCommandInbox();

        /// <summary>Idempotence: a redelivered command must not build a second house.</summary>
        private readonly OperationReplayWindow<uint> _applied = new OperationReplayWindow<uint>();

        /// <summary>Positions this client asked for, so the building that appears there counts as the host's.</summary>
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
            public long NextAttempt;
        }

        private readonly List<PendingRealizedSpawn> _selfRealized =
            new List<PendingRealizedSpawn>();
        private readonly List<PendingStateCorrection> _pendingStateCorrections =
            new List<PendingStateCorrection>();
        private readonly HashSet<uint> _pendingStateSequences = new HashSet<uint>();

        /// <summary>
        /// Spawnable entities placed by a player's tool: specialized industry uses level-one spawnable
        /// buildings, so the prefab alone cannot tell.
        /// </summary>
        private readonly HashSet<Entity> _playerPlacedGrowables = new HashSet<Entity>();
        private readonly List<Entity> _stalePlayerPlacedGrowables = new List<Entity>();

        /// <summary>Host-side: the level-up target already announced per building.</summary>
        private readonly Dictionary<Entity, Entity> _announcedLevelChange = new Dictionary<Entity, Entity>();
        private readonly List<Entity> _staleLevelChanges = new List<Entity>();

        private struct HostConstructionObservation
        {
            public byte Progress;
            public byte Speed;
        }

        private readonly Dictionary<Entity, HostConstructionObservation> _hostConstruction =
            new Dictionary<Entity, HostConstructionObservation>();
        private readonly HashSet<Entity> _constructionSeen = new HashSet<Entity>();
        private readonly List<Entity> _constructionScratch = new List<Entity>();

        private struct HostStateObservation
        {
            public byte Flags;
            public int Condition;
        }

        private readonly Dictionary<Entity, HostStateObservation> _hostState =
            new Dictionary<Entity, HostStateObservation>();
        private int _stateScanBucket;
        private int _stateScanCursor;

        private uint _sequence;
        private long _lastLevelScanMs;
        private long _lastStatsMs;
        private long _lastPlayerPlacedPruneMs;

        private int _sentSpawn, _sentLevel, _sentRemove, _sentState;
        private int _gotSpawn, _gotLevel, _gotRemove, _gotState;
        private int _duplicates, _conflicts, _unmatched, _unknownPrefab, _rejectedLocal;
        private int _repairedPrefabs;
        private int _stateDataOnly, _stateRefreshes, _stateRetryChecks;

        private PrefabSystem _prefabSystem;
        private PrefabIndex _prefabIndex;
        private ObjectSearch _objectSearch;
        private BuildSyncSystem _buildSync;
        private DeleteSyncSystem _deleteSync;
        private GrowableObserver _observer;

        private EntityQuery _createdBuildings;
        private EntityQuery _deletedBuildings;
        private EntityQuery _levelChanging;
        private EntityQuery _stateBuildings;

        protected override void OnCreate()
        {
            base.OnCreate();

            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _prefabIndex = new PrefabIndex(_prefabSystem, GetEntityQuery(ComponentType.ReadOnly<PrefabData>()));
            _objectSearch = new ObjectSearch(
                World.GetOrCreateSystemManaged<global::Game.Objects.SearchSystem>());
            _buildSync = World.GetOrCreateSystemManaged<BuildSyncSystem>();
            _deleteSync = World.GetOrCreateSystemManaged<DeleteSyncSystem>();

            // A loaded world is not tagged Created, so a join never re-broadcasts the city.
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

            _levelChanging = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<UnderConstruction, Building, PrefabRef,
                    global::Game.Objects.Transform>(),
                None = SyncQuery.ReadOnly<Temp, Deleted>(),
            });

            // Plain component data: scan one UpdateFrame partition at a time.
            _stateBuildings = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<Building, BuildingCondition, PrefabRef,
                    global::Game.Objects.Transform, UpdateFrame>(),
                None = SyncQuery.ReadOnly<Temp, Deleted, Owner>(),
            });

            _observer = SyncObserverBinding.Bind(
                () => new GrowableObserver(_incoming, Mod.Service.Session),
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
            _incoming.Clear();
            _selfRealized.Clear();
            _pendingStateCorrections.Clear();
            _pendingStateSequences.Clear();
            _playerPlacedGrowables.Clear();
            _stalePlayerPlacedGrowables.Clear();
            _lastPlayerPlacedPruneMs = 0;
            _lastGrowableRealizeMs = 0;
            _stateRetryCursor = 0;
            _stateDataOnly = _stateRefreshes = _stateRetryChecks = 0;
            // A replaced world arrives complete; old queued decisions and sequences are stale.
            _applied.Clear();
            _announcedLevelChange.Clear();
            _hostConstruction.Clear();
            _constructionSeen.Clear();
            _constructionScratch.Clear();
            _hostState.Clear();
            _stateScanBucket = 0;
            _stateScanCursor = 0;
        }

        /// <summary>Capture only; realization runs in ToolUpdate.</summary>
        protected override void OnUpdate()
        {
            using (Diagnostics.SyncProfiler.Measure("Growable"))
            {
                MultiplayerService service = Mod.Service;
                if (service == null) return;

                if (!service.SimulationSyncReady)
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
                    // Here, not in ToolUpdate: the Created tag it reads is gone by then.
                    RejectLocallyGrownBuildings(now);
                    return;
                }

                CaptureCreated(session, now);
                CaptureRemoved(session, now);
                if (now - _lastLevelScanMs >= LevelScanIntervalMs)
                {
                    _lastLevelScanMs = now;
                    CaptureConstruction(session, now);
                    CaptureStateChanges(session, now);
                }
                ReportStats(session, now);
            }
        }

        /// <summary>The prefab half only; see <see cref="IsAutonomousGrowable"/> for origin.</summary>
        private bool IsGrowablePrefab(Entity prefab) =>
            prefab != Entity.Null && EntityManager.Exists(prefab) &&
            EntityManager.HasComponent<SpawnableBuildingData>(prefab) &&
            !EntityManager.HasComponent<SignatureBuildingData>(prefab);

        /// <summary>
        /// A simulation-grown zoned building; specialized-industry placements share the data, so origin
        /// and the committed graph decide.
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

        /// <summary>One periodic line rather than one per building.</summary>
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

            long coalesced = _incoming.TakeCoalescedCount();
            if (_gotSpawn + _gotLevel + _gotRemove + _gotState + _duplicates + _conflicts +
                _unmatched + _unknownPrefab + _rejectedLocal + _repairedPrefabs +
                _stateRetryChecks == 0 && coalesced == 0) return;
            SyncLog.Detail(LogTopic.Buildings, "GrowableSync/30s client: spawn=" + _gotSpawn +
                " level=" + _gotLevel + " remove=" + _gotRemove + " state=" + _gotState +
                " duplicate=" + _duplicates + " conflict=" + _conflicts + " unmatched=" + _unmatched +
                " unknownPrefab=" + _unknownPrefab + " rejectedLocal=" + _rejectedLocal +
                " prefabRepairs=" + _repairedPrefabs + " inbox=" + _incoming.Count +
                " coalesced=" + coalesced + " dataOnly=" + _stateDataOnly +
                " stateRefreshes=" + _stateRefreshes + " retryChecks=" + _stateRetryChecks + ".");
            _gotSpawn = _gotLevel = _gotRemove = _gotState = 0;
            _duplicates = _conflicts = _unmatched = _unknownPrefab = _rejectedLocal = 0;
            _repairedPrefabs = 0;
            _stateDataOnly = _stateRefreshes = _stateRetryChecks = 0;
        }
    }
}
