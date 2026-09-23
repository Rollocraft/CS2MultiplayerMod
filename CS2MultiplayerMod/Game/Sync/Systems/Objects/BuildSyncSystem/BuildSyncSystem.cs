using System.Collections.Generic;
using System.Text;
using Game.City;
using Game.Common;
using Game.Objects;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Infrastructure;
using CS2MultiplayerMod.Game.Sync.Commands;
namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Replicates object placements (buildings, props) both ways. Captures Created non-replicas and
    /// realizes them through <see cref="CreationDefinition"/>s; echoes are guarded by player id and
    /// <see cref="ReplicationGuard"/>.
    /// </summary>
    public partial class BuildSyncSystem : CommandSyncSystem, IRealizeStage
    {
        private readonly ReplicationGuard _guard = new ReplicationGuard();

        /// <summary>A net object can outrun the road it attaches to; hold it until the node exists.</summary>
        private const long AttachRetryWindowMs = 10000;

        /// <summary>Ceiling on the wait list, so a peer can never grow it without bound.</summary>
        private const int MaxPendingAttachments = 256;

        private readonly List<(ObjectPlacementCommand command, Entity prefab, int originPlayerId, long deadline)> _attachRetry =
            new List<(ObjectPlacementCommand, Entity, int, long)>();

        private readonly Dictionary<string, int> _diag = new Dictionary<string, int>();
        private long _diagStartMs = -1;

        /// <summary>Summary interval, matching the mod's other 30 s heartbeats.</summary>
        private const long DiagIntervalMs = 30000;
        private int _diagTotal;

        // Refused simulation-spawn prefabs, aggregated so a bad peer cannot flood the log.
        private readonly Dictionary<string, int> _refused = new Dictionary<string, int>();
        private int _refusedTotal;

        // How many entities each successive capture filter sees.
        private int _hbUpdates, _hbAnyCreated, _hbCreatedPrefab, _hbCreatedTransform, _hbFiltered;
        private EntityQuery _diagAnyCreated, _diagCreatedPrefab, _diagCreatedTransform;

        private PrefabSystem _prefabSystem;
        private PrefabIndex _prefabIndex;
        private CityStateSyncSystem _cityStateSync;
        private ToolSystem _toolSystem;
        private AreaToolSystem _areaToolSystem;
        private bool _localObjectToolRanThisFrame;
        private bool _localNetToolRanThisFrame;
        private bool _localObjectApplyThisFrame;
        // The lifecycle tool that handed activeTool back while applying; valid for that one frame.
        private global::Game.Tools.ToolBaseSystem _switchedAwayObjectTool;
        // The move tool clears its control points as it applies; sampled on preview frames.
        private ControlPoint _lastObjectToolControlPoint;
        private bool _hasLastObjectToolControlPoint;
        // One-point placement input, kept apart from relocation's (different command lifecycles).
        private ControlPoint _lastPlacementControlPoint;
        private bool _hasLastPlacementControlPoint;
        // A stamp travels as this point plus prefab and seed; the peer regenerates the graph.
        private ControlPoint _lastStampControlPoint;
        private bool _hasLastStampControlPoint;
        private bool _partialPlacementRecoveryRequested;
        private EntityQuery _createdObjects;
        private EntityQuery _createdAppliedObjects;
        private EntityQuery _liveNodes;
        private EntityQuery _liveEdges;
        private EntityQuery _liveStaticObjects;

        // For reproducing the game's building placement (object, lot areas and connection nets):
        // traffic side for driveways, and the lookups GetSubNet / SelectAreaPrefab read.
        private CityConfigurationSystem _cityConfig;
        private ComponentLookup<NetGeometryData> _netGeometryLookup;
        private ComponentLookup<SpawnableObjectData> _spawnableObjectLookup;

        // Connection nets snap to terrain or the host lot surface, which needs the fields and lookups
        // CalculateLotInfo reads (see RealizeSubNetCourse).
        private global::Game.Simulation.TerrainSystem _terrainSystem;
        private global::Game.Simulation.WaterSystem _waterSystem;
        private ComponentLookup<global::Game.Objects.Transform> _transformLookup;
        private ComponentLookup<PrefabRef> _prefabRefLookup;
        private ComponentLookup<ObjectGeometryData> _objectGeometryLookup;
        private ComponentLookup<BuildingTerraformData> _buildingTerraformLookup;
        private ComponentLookup<BuildingExtensionData> _buildingExtensionLookup;

        protected override void OnCreate()
        {
            base.OnCreate();

            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _cityStateSync = World.GetOrCreateSystemManaged<CityStateSyncSystem>();
            _toolSystem = World.GetOrCreateSystemManaged<ToolSystem>();
            _areaToolSystem = World.GetOrCreateSystemManaged<AreaToolSystem>();
            _prefabIndex = new PrefabIndex(_prefabSystem, GetEntityQuery(ComponentType.ReadOnly<PrefabData>()));

            _cityConfig = World.GetOrCreateSystemManaged<CityConfigurationSystem>();
            _netGeometryLookup = GetComponentLookup<NetGeometryData>(isReadOnly: true);
            _spawnableObjectLookup = GetComponentLookup<SpawnableObjectData>(isReadOnly: true);
            _terrainSystem = World.GetOrCreateSystemManaged<global::Game.Simulation.TerrainSystem>();
            _waterSystem = World.GetOrCreateSystemManaged<global::Game.Simulation.WaterSystem>();
            _transformLookup = GetComponentLookup<global::Game.Objects.Transform>(isReadOnly: true);
            _prefabRefLookup = GetComponentLookup<PrefabRef>(isReadOnly: true);
            _objectGeometryLookup = GetComponentLookup<ObjectGeometryData>(isReadOnly: true);
            _buildingTerraformLookup = GetComponentLookup<BuildingTerraformData>(isReadOnly: true);
            _buildingExtensionLookup = GetComponentLookup<BuildingExtensionData>(isReadOnly: true);

            // Top-level objects created this frame; no previews, sub-objects, deletions or edges.
            _createdObjects = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<Created, PrefabRef, Transform>(),
                None = SyncQuery.ReadOnly<Temp, Owner, Deleted, global::Game.Net.Edge,
                    global::Game.Objects.Moving, global::Game.Vehicles.Vehicle,
                    global::Game.Creatures.Creature>(),
            });

            // Applied roots, including commits through an owned extension, for correlating with the preview.
            _createdAppliedObjects = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<Created, Applied, PrefabRef, Transform, PseudoRandomSeed,
                    global::Game.Objects.Object>(),
                None = SyncQuery.ReadOnly<Temp, Deleted>(),
            });

            // Attach targets for incoming net objects, matched by position.
            _liveNodes = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<global::Game.Net.Node>(),
                None = SyncQuery.ReadOnly<Temp, Deleted>(),
            });
            _liveEdges = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<global::Game.Net.Edge, global::Game.Net.Curve>(),
                None = SyncQuery.ReadOnly<Temp, Deleted>(),
            });

            // Standing placed objects for the duplicate guard; Static excludes movers, Owner sub-objects.
            _liveStaticObjects = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<PrefabRef, Transform, global::Game.Objects.Static>(),
                None = SyncQuery.ReadOnly<Temp, Owner, Deleted>(),
            });

            _diagAnyCreated = GetEntityQuery(ComponentType.ReadOnly<Created>());
            _diagCreatedPrefab = GetEntityQuery(ComponentType.ReadOnly<Created>(), ComponentType.ReadOnly<PrefabRef>());
            _diagCreatedTransform = GetEntityQuery(
                ComponentType.ReadOnly<Created>(), ComponentType.ReadOnly<PrefabRef>(), ComponentType.ReadOnly<Transform>());

            InitializeNativeObjectOperations();
            InitializeNativeDerive();

            ListenFor(new[] { ObjectPlacementCommand.Id, ObjectPlacementBatchCommand.Id,
                ObjectToolOperationCommand.Id, AssetStampCommand.Id },
                ObjectToolOperationCommand.MaxEncodedBytes);
        }

        protected override void DrainQueue()
        {
            SyncInbox.Clear(_incoming);
            _attachRetry.Clear();
            DrainNativeObjectOperations();
            ClearBuildingIntegrations();
            _cachedLocalObjectOperation = null;
            ClearRecentLocalObjectOperations();
            ClearPlayerPlacedSpawnables();
            _selectedAssetStampPrefabName = null;
            // A held specialized placement is a committed local building no peer knows about; log its loss.
            if (_pendingSpecializedObjectOperation != null)
            {
                SyncLog.Warn(LogTopic.Buildings,
                    "BuildSync: discarding a held specialized placement (" +
                    _pendingSpecializedObjectOperation.Definitions.Length +
                    " definitions) that was still waiting for its polygon.");
            }
            ClearSpecializedAreaCapture();
            _nativeLifecycleCapturedThisFrame = false;
            _localObjectToolRanThisFrame = false;
            _localNetToolRanThisFrame = false;
            _localObjectApplyThisFrame = false;
            _switchedAwayObjectTool = null;
            _hasLastObjectToolControlPoint = false;
            _hasLastPlacementControlPoint = false;
            _hasLastStampControlPoint = false;
            _localLifecycleApplyThisFrame = false;
            LocalObjectBrushAppliedThisFrame = false;
            _partialPlacementRecoveryRequested = false;
            _refused.Clear();
            _refusedTotal = 0;
        }

        protected override void OnUpdate()
        {
            using (Diagnostics.SyncProfiler.Measure("BuildSync"))
            {
                MultiplayerService service = Mod.Service;
                if (service == null) return;

                bool ready = service.GameplaySyncReady;
                _hbUpdates++;
                // Troubleshooting probes over broad queries; only when their summary is on.
                if (ready && SyncLog.IsEnabled(LogTopic.Buildings))
                {
                    _hbAnyCreated = System.Math.Max(_hbAnyCreated, _diagAnyCreated.CalculateEntityCount());
                    _hbCreatedPrefab = System.Math.Max(_hbCreatedPrefab, _diagCreatedPrefab.CalculateEntityCount());
                    _hbCreatedTransform = System.Math.Max(_hbCreatedTransform, _diagCreatedTransform.CalculateEntityCount());
                    _hbFiltered = System.Math.Max(_hbFiltered, _createdObjects.CalculateEntityCount());
                }

                long now = service.NowMs;
                MultiplayerSession session = service.Session;
                if (ready)
                {
                    CaptureCompletedSpecializedArea();
                    PrioritizeCreatedTrees(session);
                    _guard.Prune(now);
                    TryPublishCommittedObjectGraph(now);
                    CaptureNewObjects(session, now);
                    ObserveBuildingIntegrations(now);
                }
                else DrainQueue();
                _localObjectApplyThisFrame = false;
                FlushDiagnostics(now, ready);
            }
        }

        private void PrioritizeCreatedTrees(MultiplayerSession session)
        {
            if (session.Role != SessionRole.Host || _createdObjects.IsEmptyIgnoreFilter) return;
            NativeArray<Entity> entities = _createdObjects.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                    if (EntityManager.HasComponent<Tree>(entities[i]))
                        _cityStateSync.PrioritizeTree(entities[i]);
            }
            finally
            {
                entities.Dispose();
            }
        }

        /// <summary>
        /// Remembers this frame's ToolUpdate decision. A one-shot apply can hand activeTool to the default
        /// tool or the owned-area editor before capture runs, so the last lifecycle tool is kept for one
        /// frame and only those two engine-owned transitions are accepted.
        /// </summary>
        public void ObserveLocalToolOutput()
        {
            global::Game.Tools.ToolBaseSystem active = _toolSystem != null ? _toolSystem.activeTool : null;
            if (active is ObjectToolSystem activeObjectTool)
            {
                if (activeObjectTool.actualMode == ObjectToolSystem.Mode.Move)
                    RememberObjectToolControlPoint(activeObjectTool);
                else
                    _hasLastObjectToolControlPoint = false;

                if (activeObjectTool.actualMode == ObjectToolSystem.Mode.Create)
                    RememberPlacementControlPoint(activeObjectTool);
                else
                    _hasLastPlacementControlPoint = false;

                // Apply-frame capture ran earlier and already read the point that produced these definitions.
                if (activeObjectTool.actualMode == ObjectToolSystem.Mode.Stamp)
                    RememberStampControlPoint(activeObjectTool);
                else
                    _hasLastStampControlPoint = false;
            }
            Entity recreatedArea = _areaToolSystem != null
                ? _areaToolSystem.recreate
                : Entity.Null;
            // An owned lot is one action across two tools: the object tool assigns it to AreaTool.recreate and
            // switches tools; applyMode still belongs to the object tool on exactly that frame.
            bool objectToOwnedAreaHandoff =
                active is AreaToolSystem &&
                recreatedArea != Entity.Null &&
                _toolSystem != null &&
                _toolSystem.applyMode == ApplyMode.Apply &&
                _objectToolSystem != null &&
                _objectToolSystem.applyMode == ApplyMode.Apply;

            // Returning from the polygon: the object tool is current but did not run.
            bool returningFromOwnedArea =
                active is ObjectToolSystem &&
                recreatedArea != Entity.Null;
            bool activeLifecycleToolRan =
                IsObjectLifecycleTool(active) && !returningFromOwnedArea;
            _localNetToolRanThisFrame = active is global::Game.Tools.NetToolSystem;
            // Any other tool switch is the user's; it does not inherit the prior tool's Apply.
            global::Game.Tools.ToolBaseSystem lifecycleTool = null;
            if (activeLifecycleToolRan)
                lifecycleTool = active;
            else if (objectToOwnedAreaHandoff)
                lifecycleTool = _objectToolSystem;
            else if (active is global::Game.Tools.DefaultToolSystem)
                lifecycleTool = _switchedAwayObjectTool;
            // Consumed after one frame: only the switch-away frame still belongs to that tool.
            _switchedAwayObjectTool = activeLifecycleToolRan ? active : null;

            bool applying = lifecycleTool != null &&
                            lifecycleTool.applyMode == ApplyMode.Apply;
            RememberSelectedAssetStampPrefab(lifecycleTool);
            SampleLifecycleToolSeed(lifecycleTool);
            _localObjectToolRanThisFrame = activeLifecycleToolRan || applying;
            _localObjectApplyThisFrame = applying;
            _localLifecycleApplyThisFrame = _localObjectApplyThisFrame;
            LocalObjectBrushAppliedThisFrame = applying &&
                lifecycleTool is ObjectToolSystem objectTool &&
                objectTool.actualMode == ObjectToolSystem.Mode.Brush;

            if (objectToOwnedAreaHandoff && applying)
                SyncLog.Trace(LogTopic.Buildings,
                    "object lifecycle apply retained across owned-area handoff");
        }

        // Kept for the whole frame, unlike _localObjectApplyThisFrame which capture consumes.
        private bool _localLifecycleApplyThisFrame;

        /// <summary>
        /// A local lifecycle tool applied this frame; the removals its footprint caused are reproduced by the
        /// receiver and must not be captured as bulldozes.
        /// </summary>
        public bool LocalObjectLifecycleAppliedThisFrame => _localLifecycleApplyThisFrame;

        /// <summary>Brush removals are explicit edits; they keep their delete fallback unless published natively.</summary>
        internal bool LocalObjectBrushAppliedThisFrame { get; private set; }

        /// <summary>ToolUpdate: definitions realize only if created before Modification1.</summary>
        public void RealizePending()
        {
            MultiplayerService service = Mod.Service;
            if (service == null) return;

            MultiplayerSession session = service.Session;
            if (service.GameplaySyncReady)
            {
                ApplyBuildingIntegrationRefreshes(service.NowMs);
                RealizeIncoming(session, service.NowMs);
            }
        }

        private void RecordDiagnostic(string prefabName)
        {
            _diagTotal++;
            _diag.TryGetValue(prefabName, out int count);
            _diag[prefabName] = count + 1;
        }

        private void RecordRefused(string prefabName)
        {
            _refusedTotal++;
            _refused.TryGetValue(prefabName, out int count);
            _refused[prefabName] = count + 1;
        }

        private void FlushDiagnostics(long now, bool connected)
        {
            if (_diagStartMs < 0) { _diagStartMs = now; return; }
            if (now - _diagStartMs < DiagIntervalMs) return;

            // Only log when something is happening, to avoid spamming an idle main menu.
            if (connected || _hbAnyCreated > 0 || _diagTotal > 0)
            {
                var sb = new StringBuilder();
                sb.Append("BuildSync/30s: updates=").Append(_hbUpdates)
                  .Append(" created[any/+prefab/+transform/filtered]=")
                  .Append(_hbAnyCreated).Append('/').Append(_hbCreatedPrefab).Append('/')
                  .Append(_hbCreatedTransform).Append('/').Append(_hbFiltered)
                  .Append(" emitted=").Append(_diagTotal);
                if (_diagTotal > 0)
                {
                    sb.Append(" [");
                    int n = 0;
                    foreach (var pair in _diag)
                    {
                        if (n > 0) sb.Append(", ");
                        sb.Append(pair.Key).Append(" x").Append(pair.Value);
                        if (++n >= 10) { sb.Append(", ..."); break; }
                    }
                    sb.Append(']');
                }
                SyncLog.Detail(LogTopic.Buildings, sb.ToString());
            }

            if (_refusedTotal > 0)
            {
                var sb = new StringBuilder();
                sb.Append("BuildSync realize: refused ").Append(_refusedTotal)
                  .Append(" simulation-only placement(s) in the last 30s [");
                int n = 0;
                foreach (KeyValuePair<string, int> pair in _refused)
                {
                    if (n > 0) sb.Append(", ");
                    sb.Append(pair.Key).Append(" x").Append(pair.Value);
                    if (++n >= 10) { sb.Append(", ..."); break; }
                }
                sb.Append(']');
                SyncLog.Warn(LogTopic.Buildings, sb.ToString());
                _refused.Clear();
                _refusedTotal = 0;
            }

            _diag.Clear();
            _diagTotal = 0;
            _diagStartMs = now;
            _hbUpdates = _hbAnyCreated = _hbCreatedPrefab = _hbCreatedTransform = _hbFiltered = 0;
        }

        private void CaptureNewObjects(MultiplayerSession session, long now)
        {
            if (_createdObjects.IsEmptyIgnoreFilter || _nativeLifecycleCapturedThisFrame ||
                (_nativeNetCoordinator != null && _nativeNetCoordinator.DidCommitObjectGraphThisFrame)) return;
            // A specialized placement publishes only once its polygon closes.
            if (_pendingSpecializedObjectOperation != null ||
                (_areaToolSystem != null && _areaToolSystem.recreate != Entity.Null)) return;

            NativeArray<Entity> entities = _createdObjects.ToEntityArray(Allocator.Temp);
            try
            {
                var localCreated = new List<Entity>(entities.Length);
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity entity = entities[i];
                    Entity prefab = EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab;
                    string name = _prefabSystem.GetPrefabName(prefab);
                    if (string.IsNullOrEmpty(name) || IsSimulationOnlyPlacementPrefab(prefab))
                        continue;
                    Transform transform = EntityManager.GetComponentData<Transform>(entity);
                    if (_guard.Consume(ReplicationGuard.Key(name, transform.m_Position), now))
                        continue;
                    localCreated.Add(entity);
                }

                if (localCreated.Count == 0) return;

                // A committed root's prefab, transform and seed select the exact recent preview graph.
                if (TryPublishMatchingRecentLocalObjectOperation(localCreated, now)) return;

                // Record every failed correlation; Apply still gates the reduced fallback.
                NoteCommittedObjectGraphMiss(localCreated);

                // Only the reduced compatibility fallback still depends on the tool Apply sample.
                if (!_localObjectApplyThisFrame) return;
                if (TryCaptureObjectBrushPlacements(session, localCreated)) return;

                for (int i = 0; i < localCreated.Count; i++)
                {
                    Entity entity = localCreated[i];
                    Entity prefab = EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab;
                    string name = _prefabSystem.GetPrefabName(prefab);
                    if (string.IsNullOrEmpty(name)) continue;

                    // Simulation spawns can appear on a tool-apply frame but are not part of it.
                    if (IsSimulationOnlyPlacementPrefab(prefab)) continue;

                    if (RequiresCompleteObjectLifecycle(prefab))
                    {
                        // Buildings and prefabs with owned elements need the full native transaction.
                        if (!_partialPlacementRecoveryRequested)
                        {
                            _partialPlacementRecoveryRequested = true;
                            SyncLog.Error(LogTopic.Buildings,
                                "BuildSync: complete lifecycle capture was missed for '" + name +
                                "'; requesting (debounced) world recovery instead of " +
                                "sending a partial object graph. " +
                                (_lastObjectGraphMissDetail ?? "no correlation detail"));
                            Mod.Service.RequestAutomaticWorldRecovery("building placement capture missed");
                        }
                        continue;
                    }

                    Transform transform = EntityManager.GetComponentData<Transform>(entity);
                    int randomSeed = EntityManager.HasComponent<PseudoRandomSeed>(entity)
                        ? EntityManager.GetComponentData<PseudoRandomSeed>(entity).m_Seed
                        : (int)(math.hash(transform.m_Position) & 0xffffu);
                    float age = EntityManager.HasComponent<Tree>(entity)
                        ? TreeAge(EntityManager.GetComponentData<Tree>(entity))
                        : 0f;

                    // A net object is inert without its parent; AttachSystem has resolved it by now.
                    var attachKind = ObjectAttachKind.None;
                    if (NetAttachment.TryGetAttachment(EntityManager, entity, out bool isNode, out float3 attachPos))
                        attachKind = isNode ? ObjectAttachKind.NetNode : ObjectAttachKind.NetEdge;

                    var command = new ObjectPlacementCommand
                    {
                        PrefabName = name,
                        PosX = transform.m_Position.x,
                        PosY = transform.m_Position.y,
                        PosZ = transform.m_Position.z,
                        RotX = transform.m_Rotation.value.x,
                        RotY = transform.m_Rotation.value.y,
                        RotZ = transform.m_Rotation.value.z,
                        RotW = transform.m_Rotation.value.w,
                        RandomSeed = randomSeed,
                        Age = age,
                        AttachKind = attachKind,
                        AttachX = attachPos.x,
                        AttachY = attachPos.y,
                        AttachZ = attachPos.z,
                    };
                    session.SendCommand(0, ObjectPlacementCommand.Id, command.Encode());
                    RecordDiagnostic(name);
                }
            }
            finally
            {
                entities.Dispose();
            }
        }

        private static float TreeAge(Tree tree)
        {
            float growth = tree.m_Growth + 0.5f;
            TreeState stage = tree.m_State &
                              (TreeState.Teen | TreeState.Adult | TreeState.Elderly |
                               TreeState.Dead | TreeState.Stump);
            float age;
            switch (stage)
            {
                case TreeState.Teen: age = 0.1f + growth / 1706.6666f; break;
                case TreeState.Adult: age = 0.25f + growth / 731.4286f; break;
                case TreeState.Elderly: age = 0.6f + growth / 731.4286f; break;
                case TreeState.Dead:
                case TreeState.Stump: age = 0.95f + growth / 5120f; break;
                default: age = growth / 2560f; break;
            }
            return math.clamp(age, 0f, 1f);
        }

        /// <summary>Instances only simulation ownership may create.</summary>
        private bool IsSimulationOnlyPlacementPrefab(Entity prefab)
        {
            if (prefab == Entity.Null || !EntityManager.Exists(prefab)) return true;
            if (EntityManager.HasComponent<MovingObjectData>(prefab)) return true;
            return EntityManager.HasComponent<SpawnableBuildingData>(prefab) &&
                   !EntityManager.HasComponent<SignatureBuildingData>(prefab);
        }

        /// <summary>A root transform does not describe the placement; the native lifecycle command must.</summary>
        private bool RequiresCompleteObjectLifecycle(Entity prefab)
        {
            if (IsNetObjectPlacement(prefab)) return false;

            return EntityManager.HasComponent<BuildingData>(prefab) ||
                   EntityManager.HasComponent<TransportStopData>(prefab) ||
                   EntityManager.HasBuffer<global::Game.Prefabs.SubObject>(prefab) ||
                   EntityManager.HasBuffer<global::Game.Prefabs.SubNet>(prefab) ||
                   EntityManager.HasBuffer<global::Game.Prefabs.SubArea>(prefab);
        }

        /// <summary>
        /// A net object is fully described by prefab, transform and attach anchor; the receiver regenerates
        /// its owned elements, including decorated variants' sub-objects.
        /// </summary>
        private bool IsNetObjectPlacement(Entity prefab)
        {
            return EntityManager.HasComponent<global::Game.Prefabs.NetObjectData>(prefab) &&
                   !EntityManager.HasComponent<BuildingData>(prefab) &&
                   !EntityManager.HasComponent<TransportStopData>(prefab);
        }

        /// <summary>Routes received object-placement commands (sim thread) into the queue.</summary>
    }
}
