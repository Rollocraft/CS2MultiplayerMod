using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using Game;
using Game.City;
using Game.Common;
using Game.Objects;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Infrastructure;
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.Systems.Net;
namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Replicates object placements (buildings, props) bidirectionally: detect <see cref="Created"/>
    /// non-replicas, broadcast <see cref="ObjectPlacementCommand"/>; realize by spawning
    /// <see cref="CreationDefinition"/>. Guards via player id and <see cref="ReplicationGuard"/>;
    /// host relays to other clients. Known: Created query includes zoning growth.
    /// </summary>
    public partial class BuildSyncSystem : GameSystemBase
    {
        private readonly ConcurrentQueue<SimulationCommandMessage> _incoming =
            new ConcurrentQueue<SimulationCommandMessage>();
        private readonly ReplicationGuard _guard = new ReplicationGuard();

        /// <summary>A net object can outrun the road it attaches to; hold it until the node exists.</summary>
        private const long AttachRetryWindowMs = 10000;

        /// <summary>Ceiling on the wait list, so a peer can never grow it without bound.</summary>
        private const int MaxPendingAttachments = 256;

        private readonly List<(ObjectPlacementCommand command, Entity prefab, int originPlayerId, long deadline)> _attachRetry =
            new List<(ObjectPlacementCommand, Entity, int, long)>();

        /// <summary>
        /// Set by <see cref="SyncRealizeSystem"/> while remote terrain edits are backlogged: no new
        /// remote object realizes until terrain catches up (its transmitted Y assumes the sender's
        /// terrain).
        /// </summary>
        public bool DeferForTerrain;

        private readonly Dictionary<string, int> _diag = new Dictionary<string, int>();
        private long _diagStartMs = -1;
        private int _diagTotal;

        // Commands refused because their prefab belongs to simulation spawning rather than
        // player placement. Aggregate them: a bad peer can otherwise produce hundreds of
        // warnings per second while we are protecting the world from the flood.
        private readonly Dictionary<string, int> _refused = new Dictionary<string, int>();
        private int _refusedTotal;

        // Diagnostic probes: how many entities each successive filter sees, so a quiet log
        // pinpoints whether the update phase is even seeing freshly-Created entities.
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
        // The object/upgrade tool that was active until it applied and handed activeTool back to the
        // default tool. Valid for that one frame only - see ObserveLocalToolOutput. Owned-area
        // handoffs have their own explicit recreate marker and do not depend on prior-frame state.
        private global::Game.Tools.ToolBaseSystem _switchedAwayObjectTool;
        // The move tool clears its control-point list as it applies. Sample the standing snapped
        // point on preview frames so the apply capture can still recover its destination net parent.
        private ControlPoint _lastObjectToolControlPoint;
        private bool _hasLastObjectToolControlPoint;
        // Ordinary one-point building placement is regenerated remotely from this snapped point.
        // Keep it separately from relocation because both modes use the same native control-point
        // type but have different command lifecycles.
        private ControlPoint _lastPlacementControlPoint;
        private bool _hasLastPlacementControlPoint;
        // The stamp placement the standing definitions were generated from. An asset stamp travels
        // as this one point plus its prefab and tool seed, so the peer regenerates the graph rather
        // than rebuilding it from transmitted definitions.
        private ControlPoint _lastStampControlPoint;
        private bool _hasLastStampControlPoint;
        private bool _partialPlacementRecoveryRequested;
        private EntityQuery _createdObjects;
        private EntityQuery _createdAppliedObjects;
        private EntityQuery _liveNodes;
        private EntityQuery _liveEdges;
        private EntityQuery _liveStaticObjects;
        private CommandObserver _observer;

        // Used by the realize path to reproduce the game's own building placement (a building
        // emits an object definition plus owner-linked lot-area and connection-net definitions).
        // leftHandTraffic mirrors the driveway sub-nets the way the game does; the two prefab
        // lookups feed NetUtils.GetSubNet / AreaUtils.SelectAreaPrefab. See Realize.cs.
        private CityConfigurationSystem _cityConfig;
        private ComponentLookup<NetGeometryData> _netGeometryLookup;
        private ComponentLookup<SpawnableObjectData> _spawnableObjectLookup;

        // A building's connection nets are not laid at their prefab-local height: the game snaps each
        // course to the terrain, or to the host building's lot surface when it has one. Reproducing
        // that needs the height/water fields plus the five lookups CalculateLotInfo reads. See
        // RealizeSubNetCourse.
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

            // Top-level objects created this frame: prefab + transform, not a tool preview
            // (Temp), not an owned sub-object (Owner), not being deleted, not a net edge.
            _createdObjects = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<Created, PrefabRef, Transform>(),
                None = SyncQuery.ReadOnly<Temp, Owner, Deleted, global::Game.Net.Edge,
                    global::Game.Objects.Moving, global::Game.Vehicles.Vehicle,
                    global::Game.Creatures.Creature>(),
            });

            // Full object-tool transactions can commit through an owned extension rather than a
            // top-level object. Keep a narrow Applied query for correlating either kind with the
            // exact preview graph cached before the click.
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

            // Standing placed objects (buildings, props), for the duplicate-placement guard in
            // Realize.cs. Static excludes vehicles/cims; Owner excludes sub-objects.
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

            _observer = SyncObserverBinding.Bind(
                () => new CommandObserver(_incoming,
                        ObjectPlacementCommand.Id, ObjectToolOperationCommand.Id,
                        AssetStampCommand.Id)
                    {
                        MaxBodyBytes = ObjectToolOperationCommand.MaxEncodedBytes,
                    },
                DrainQueue);
        }

        protected override void OnDestroy()
        {
            SyncObserverBinding.Unbind(_observer, DrainQueue);
            base.OnDestroy();
        }

        private void DrainQueue()
        {
            SyncInbox.Clear(_incoming);
            _attachRetry.Clear();
            DrainNativeObjectOperations();
            _cachedLocalObjectOperation = null;
            ClearRecentLocalObjectOperations();
            ClearPlayerPlacedSpawnables();
            _selectedAssetStampPrefabName = null;
            // A world sync tears this down mid-handoff. Say so: the held graph is a committed local
            // building that no peer has been told about, and losing it without a trace is how a
            // specialized placement went missing on one machine with nothing in the log.
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
            _partialPlacementRecoveryRequested = false;
            DeferForTerrain = false;
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
                // These probes walk broad Created queries. They are troubleshooting-only work, so
                // keep them off the normal frame path unless the summary they feed is switched on.
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
        /// Remember the ToolUpdate decision that can produce Created objects later this frame.
        /// Some one-shot placements leave the object tool before ModificationEnd, so checking the
        /// active tool only at capture time loses the placement entirely.
        ///
        /// ToolSystem drives the whole ToolUpdate phase from inside its own update, so by the time
        /// any of our systems run the active tool has already made its decision this frame. A
        /// one-shot action can assign the default tool, or the owned-area editor for a
        /// non-overlapping lot, to <c>activeTool</c> as part of applying. Reading only
        /// <c>activeTool</c> is blind on exactly that frame. Keep the last object-lifecycle tool for
        /// one frame and accept only those two engine-owned transitions.
        /// </summary>
        public void ObserveLocalToolOutput()
        {
            global::Game.Tools.ToolBaseSystem active = _toolSystem != null ? _toolSystem.activeTool : null;
            ObjectToolSystem activeObjectTool = active as ObjectToolSystem;
            if (activeObjectTool != null)
            {
                if (activeObjectTool.actualMode == ObjectToolSystem.Mode.Move)
                    RememberObjectToolControlPoint(activeObjectTool);
                else
                    _hasLastObjectToolControlPoint = false;

                if (activeObjectTool.actualMode == ObjectToolSystem.Mode.Create)
                    RememberPlacementControlPoint(activeObjectTool);
                else
                    _hasLastPlacementControlPoint = false;

                // The apply frame's capture runs at ToolUpdate, before this sample, so it reads the
                // preview point that actually produced the definitions now committing.
                if (activeObjectTool.actualMode == ObjectToolSystem.Mode.Stamp)
                    RememberStampControlPoint(activeObjectTool);
                else
                    _hasLastStampControlPoint = false;
            }
            Entity recreatedArea = _areaToolSystem != null
                ? _areaToolSystem.recreate
                : Entity.Null;
            // A non-overlapping owned lot is one lifecycle action split across two tools. The
            // object tool leaves an applying graph standing, assigns that lot to AreaTool.recreate,
            // and switches activeTool before later observers run. ToolSystem.applyMode still belongs
            // to the tool that ran this phase, so this conjunction identifies exactly the transition
            // frame rather than every frame spent drawing the area.
            bool objectToOwnedAreaHandoff =
                active is AreaToolSystem &&
                recreatedArea != Entity.Null &&
                _toolSystem != null &&
                _toolSystem.applyMode == ApplyMode.Apply &&
                _objectToolSystem != null &&
                _objectToolSystem.applyMode == ApplyMode.Apply;

            // Conversely, closing or cancelling that polygon switches activeTool back before the
            // area tool's output is consumed. The object tool is current at that point but did not
            // run, and its old ApplyMode must not be interpreted as a second object placement.
            bool returningFromOwnedArea =
                active is ObjectToolSystem &&
                recreatedArea != Entity.Null;
            bool activeLifecycleToolRan =
                IsObjectLifecycleTool(active) && !returningFromOwnedArea;
            _localNetToolRanThisFrame = active is global::Game.Tools.NetToolSystem;
            // Default hand-backs and recreate-area handoffs are engine-owned transitions. Any
            // other different build tool is a user action and must not inherit the prior tool's
            // Apply state.
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

            if (objectToOwnedAreaHandoff && applying)
                SyncLog.Trace(LogTopic.Buildings,
                    "object lifecycle apply retained across owned-area handoff");
        }

        // Sampled at ToolUpdate and kept for the rest of the frame, unlike
        // _localObjectApplyThisFrame which capture paths consume and clear.
        private bool _localLifecycleApplyThisFrame;

        /// <summary>
        /// True for the whole frame in which a local object-lifecycle tool applied. A placement,
        /// upgrade or relocation removes whatever its footprint covers - lot sub-nets, sub-areas,
        /// props - and the receiver reproduces those removals from the same action, so they must not
        /// also be captured as bulldozes.
        /// </summary>
        public bool LocalObjectLifecycleAppliedThisFrame => _localLifecycleApplyThisFrame;

        /// <summary>
        /// Called by <see cref="SyncRealizeSystem"/> during ToolUpdate. Definitions realize
        /// when created before Modification1 (see frame order in <see cref="SyncRealizeSystem"/>).
        /// Capture stays at ModificationEnd where one-frame <see cref="Created"/> tags live.
        /// </summary>
        public void RealizePending()
        {
            MultiplayerService service = Mod.Service;
            if (service == null) return;

            MultiplayerSession session = service.Session;
            if (service.GameplaySyncReady)
                RealizeIncoming(session, service.NowMs);
        }

        // Periodic summary of what the detector captured — reveals over-capture severity
        // and the exact prefab names being synced, without flooding the log per object.
        private void RecordDiagnostic(string prefabName)
        {
            _diagTotal++;
            int count;
            _diag.TryGetValue(prefabName, out count);
            _diag[prefabName] = count + 1;
        }

        private void RecordRefused(string prefabName)
        {
            _refusedTotal++;
            int count;
            _refused.TryGetValue(prefabName, out count);
            _refused[prefabName] = count + 1;
        }

        private void FlushDiagnostics(long now, bool connected)
        {
            if (_diagStartMs < 0) { _diagStartMs = now; return; }
            if (now - _diagStartMs < 5000) return;

            // Only log when something is happening, to avoid spamming an idle main menu.
            if (connected || _hbAnyCreated > 0 || _diagTotal > 0)
            {
                var sb = new StringBuilder();
                sb.Append("BuildSync/5s: updates=").Append(_hbUpdates)
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
                  .Append(" simulation-only placement(s) in the last 5s [");
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
            // Specialized-industry placement is not committed until its area-tool polygon closes.
            // Its initial building root must never publish the incomplete object half here.
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

                // A committed root is a stronger signal than the transient tool Apply pulse. Its
                // prefab, transform, and random seed select the exact recent preview graph.
                if (TryPublishMatchingRecentLocalObjectOperation(localCreated, now)) return;

                // Record every failed graph correlation, including the original failure mode where
                // the one-frame Apply pulse was not sampled. Apply still gates the reduced fallback.
                NoteCommittedObjectGraphMiss(localCreated);

                // Only the reduced compatibility fallback still depends on the tool Apply sample.
                if (!_localObjectApplyThisFrame) return;

                for (int i = 0; i < localCreated.Count; i++)
                {
                    Entity entity = localCreated[i];
                    Entity prefab = EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab;
                    string name = _prefabSystem.GetPrefabName(prefab);
                    if (string.IsNullOrEmpty(name)) continue;

                    // The final-entity path is only a compatibility fallback. Simulation
                    // movers and zone-grown buildings can be Created on the same frame as a
                    // real tool apply, but they are not part of that player action.
                    if (IsSimulationOnlyPlacementPrefab(prefab)) continue;

                    if (RequiresCompleteObjectLifecycle(prefab))
                    {
                        // Buildings and prefabs with owned elements must never enter the reduced
                        // placement channel: it cannot preserve their complete subobject, network,
                        // area, terrain, and attachment transaction.
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

                    // A net object (roundabout island, turn-restriction sign) is inert without its
                    // parent: the ring and the restriction are derived from the parent's sub-objects,
                    // never from the object's transform. AttachSystem resolved the parent by now.
                    var attachKind = ObjectAttachKind.None;
                    bool isNode;
                    Unity.Mathematics.float3 attachPos;
                    if (NetAttachment.TryGetAttachment(EntityManager, entity, out isNode, out attachPos))
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

        /// <summary>
        /// True for prefabs whose live instances must be created by simulation ownership
        /// machinery, never by a standalone multiplayer placement definition.
        /// </summary>
        private bool IsSimulationOnlyPlacementPrefab(Entity prefab)
        {
            if (prefab == Entity.Null || !EntityManager.Exists(prefab)) return true;
            if (EntityManager.HasComponent<MovingObjectData>(prefab)) return true;
            return EntityManager.HasComponent<SpawnableBuildingData>(prefab) &&
                   !EntityManager.HasComponent<SignatureBuildingData>(prefab);
        }

        /// <summary>
        /// True when a final root transform is not a complete representation of the placement.
        /// These prefabs must travel through the native atomic object-lifecycle command.
        /// </summary>
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
        /// A net object (roundabout island, turn-restriction sign) is described completely by prefab,
        /// transform and attach anchor: the receiver regenerates its owned elements from the prefab
        /// and tags the parent itself. Decorated variants carry sub-objects, which would otherwise
        /// pin them to the native path where a capture miss escalates to a whole-world reload.
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
