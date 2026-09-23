using System.Reflection;
using Game.Common;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Infrastructure;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    public partial class BuildSyncSystem
    {
        internal enum NativeDeriveResult : byte
        {
            /// <summary>Definitions exist and the isolated commit is armed for the next frame.</summary>
            Armed,
            /// <summary>Another transaction owns the commit slot, or a local apply has priority.</summary>
            Busy,
            /// <summary>This build of the game does not expose the generator; use the fallback path.</summary>
            Unsupported,
            /// <summary>The generator produced nothing usable; the caller must use its fallback.</summary>
            Failed,
        }

        private static MethodInfo _createDefinitionsMethod;
        private static FieldInfo _randomSeedValueField;
        private static bool _deriveReflectionResolved;
        private static readonly System.Collections.Generic.Dictionary<System.Type, FieldInfo>
            _toolSeedFields = new System.Collections.Generic.Dictionary<System.Type, FieldInfo>();

        private ObjectToolSystem _objectToolSystem;
        private UpgradeToolSystem _upgradeToolSystem;
        private ToolOutputBarrier _toolOutputBarrier;
        private EntityQuery _freshDefinitions;
        private EntityQuery _standingDefinitions;

        // An Apply advances the tool seed, so the previous frame's sample built what committed.
        private uint _lifecycleToolSeed;
        private uint _previousLifecycleToolSeed;

        /// <summary>The seed the applied action used, so the receiver draws the same variations.</summary>
        public uint AppliedLifecycleToolSeed => _previousLifecycleToolSeed;

        /// <summary>True when the game exposes the definition generator this path drives.</summary>
        public bool CanDeriveNativeTransactions =>
            ResolveDeriveReflection() && _toolOutputBarrier != null && _objectToolSystem != null;

        private void InitializeNativeDerive()
        {
            _objectToolSystem = World.GetOrCreateSystemManaged<ObjectToolSystem>();
            _upgradeToolSystem = World.GetOrCreateSystemManaged<UpgradeToolSystem>();
            _toolOutputBarrier = World.GetOrCreateSystemManaged<ToolOutputBarrier>();

            // Everything not born Deleted: exactly the local tool's definitions.
            _freshDefinitions = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<CreationDefinition>(),
                None = SyncQuery.ReadOnly<Deleted>(),
            });

            // Definitions without Updated: the graph behind the previews now committing.
            _standingDefinitions = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<CreationDefinition>(),
                None = SyncQuery.ReadOnly<Updated, Deleted>(),
            });
        }

        private void SampleLifecycleToolSeed(global::Game.Tools.ToolBaseSystem lifecycleTool)
        {
            _previousLifecycleToolSeed = _lifecycleToolSeed;
            _lifecycleToolSeed = ReadToolSeed(lifecycleTool);
        }

        private static bool ResolveDeriveReflection()
        {
            if (_deriveReflectionResolved)
                return _createDefinitionsMethod != null && _randomSeedValueField != null;
            _deriveReflectionResolved = true;

            // The game's own definition generator; a future rename degrades to the reduced paths.
            _createDefinitionsMethod = typeof(ObjectToolBaseSystem).GetMethod("CreateDefinitions",
                BindingFlags.Instance | BindingFlags.NonPublic);
            _createDefinitionsTakesOverrides = false;
            if (_createDefinitionsMethod != null)
            {
                int parameters = _createDefinitionsMethod.GetParameters().Length;
                _createDefinitionsTakesOverrides = parameters == CreateDefinitionsArgumentCount + 1;
                if (parameters != CreateDefinitionsArgumentCount && !_createDefinitionsTakesOverrides)
                    _createDefinitionsMethod = null;
            }
            _randomSeedValueField = typeof(RandomSeed).GetField("m_Seed",
                BindingFlags.Instance | BindingFlags.NonPublic);

            if (_createDefinitionsMethod == null || _randomSeedValueField == null)
                SyncLog.Warn(LogTopic.Buildings,
                    "BuildSync: the game's object definition generator is not " +
                    "reachable; upgrades and building moves fall back to reduced replication.");
            return _createDefinitionsMethod != null && _randomSeedValueField != null;
        }

        // Game 1.6.2 added a placement-overrides argument; bind to whichever signature exists.
        private const int CreateDefinitionsArgumentCount = 23;

        private static bool _createDefinitionsTakesOverrides;

        // Isolated: the JIT resolves every type a method names when compiling it.
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static object EmptyPlacementOverrides() => default(PlacementOverrides);

        /// <summary>Drops the second-to-last (overrides) slot for a build that predates it.</summary>
        private static object[] TrimOverridesArgument(object[] arguments)
        {
            var trimmed = new object[arguments.Length - 1];
            System.Array.Copy(arguments, trimmed, arguments.Length - 2);
            trimmed[trimmed.Length - 1] = arguments[arguments.Length - 1];
            return trimmed;
        }

        private static FieldInfo ToolSeedField(System.Type toolType)
        {
            if (_toolSeedFields.TryGetValue(toolType, out FieldInfo field)) return field;
            field = toolType.GetField("m_RandomSeed",
                BindingFlags.Instance | BindingFlags.NonPublic);
            _toolSeedFields[toolType] = field;
            return field;
        }

        private static uint ReadToolSeed(global::Game.Tools.ToolBaseSystem tool)
        {
            if (tool == null || !ResolveDeriveReflection()) return 0u;
            FieldInfo field = ToolSeedField(tool.GetType());
            if (field == null || field.FieldType != typeof(RandomSeed)) return 0u;
            object seed = field.GetValue(tool);
            return seed == null ? 0u : (uint)_randomSeedValueField.GetValue(seed);
        }

        private static RandomSeed MakeRandomSeed(uint value)
        {
            object boxed = default(RandomSeed);
            _randomSeedValueField.SetValue(boxed, value);
            return (RandomSeed)boxed;
        }

        /// <summary>
        /// Reproduces a remote upgrade or relocation by running the game's generator on this world with
        /// the tool's inputs (prefab, owner/original, one snapped control point, seed). Host re-commits,
        /// road attachment, covered sub-net deletes and lot snapping all derive from local geometry.
        /// </summary>
        internal NativeDeriveResult TryDeriveObjectTransaction(Entity objectPrefab, Entity owner,
            Entity original, Entity attachmentTarget, float3 position, quaternion rotation,
            float elevation, uint toolSeed, string source, System.Action onCommitLost,
            System.Action onCommitComplete, bool stamping = false)
        {
            if (!CanDeriveNativeTransactions) return NativeDeriveResult.Unsupported;
            if (_nativeNetCoordinator == null || _nativeNetCoordinator.IsCommitBusy ||
                !_nativeNetCoordinator.CanBuildDefinitions) return NativeDeriveResult.Busy;

            ObjectToolBaseSystem tool = owner != Entity.Null && _upgradeToolSystem != null
                ? (ObjectToolBaseSystem)_upgradeToolSystem
                : _objectToolSystem;

            _nativeNetCoordinator.PrepareDefinitionFrame();
            int startTick = System.Environment.TickCount;
            var controlPoints = new NativeList<ControlPoint>(1, Allocator.Temp);
            try
            {
                // Drop the tool's buffered preview first, so the only fresh definitions are the generator's.
                DiscardBufferedLocalDefinitions();

                controlPoints.Add(new ControlPoint
                {
                    m_Position = position,
                    m_HitPosition = position,
                    m_Rotation = rotation,
                    // The snapped road/node drives attachment, lane movement and road re-commits.
                    m_OriginalEntity = attachmentTarget,
                    m_Elevation = elevation,
                });

                var arguments = new object[]
                {
                    objectPrefab,                               // objectPrefab
                    Entity.Null,                                // transformPrefab
                    Entity.Null,                                // brushPrefab
                    owner,                                      // owner (the building being upgraded)
                    original,                                   // original (the object being moved)
                    Entity.Null,                                // laneEditor (editor only)
                    _cityConfig != null ? _cityConfig.defaultTheme : Entity.Null,
                    controlPoints,
                    default(NativeReference<ObjectToolBaseSystem.AttachmentData>),
                    false,                                      // editorMode
                    _cityConfig != null && _cityConfig.leftHandTraffic,
                    false,                                      // removing
                    // Stamping omits the stamp root and expands its graph, as the local tool does.
                    stamping,
                    0f, 0f, 0f,                                 // brush size/angle/strength
                    0f,                                         // distance (0 = single placement)
                    0f,                                         // deltaTime (creature spawning only)
                    MakeRandomSeed(toolSeed),
                    Snap.None,
                    AgeMask.Sapling,
                    false,                                      // decorationMode
                    // Editor staging values; a replicated placement has none.
                    _createDefinitionsTakesOverrides ? EmptyPlacementOverrides() : null,
                    default(JobHandle),
                };
                if (!_createDefinitionsTakesOverrides)
                    arguments = TrimOverridesArgument(arguments);

                object handle = _createDefinitionsMethod.Invoke(tool, arguments);
                if (handle is JobHandle) ((JobHandle)handle).Complete();

                // Nothing ran in between: every fresh definition is this transaction's.
                FlushToolOutputBarrier();
            }
            catch (System.Exception ex)
            {
                controlPoints.Dispose();
                _nativeNetCoordinator.CancelPreparedDefinitionFrame();
                // Reflection wraps whatever the generator threw; the inner one is the useful message.
                System.Exception cause = ex.InnerException ?? ex;
                SyncLog.Warn(LogTopic.Buildings,
                    "BuildSync: the game's definition generator rejected " + source +
                    "; dropping this edit: " + cause.Message);
                return NativeDeriveResult.Failed;
            }
            finally
            {
                // The rest of this frame's tool phases need the barrier.
                _toolOutputBarrier.AllowUsage();
            }
            controlPoints.Dispose();

            int derived = TagDerivedDefinitions();
            if (derived == 0)
            {
                _nativeNetCoordinator.CancelPreparedDefinitionFrame();
                SyncLog.Trace(LogTopic.Buildings, "native derive produced no definitions (" + source +
                    ")");
                return NativeDeriveResult.Failed;
            }

            // A stamp graph is rootless and must be armed as one, or validation rejects it.
            if (!_nativeNetCoordinator.ArmObjectCommit(onCommitLost, onCommitComplete,
                    "derived " + source + " defs=" + derived, stamping))
            {
                _nativeNetCoordinator.CancelPreparedDefinitionFrame();
                return NativeDeriveResult.Busy;
            }

            SyncLog.Trace(LogTopic.Buildings, "native derive " + source + " defs=" + derived +
                " seed=" + toolSeed + " deriveMS=" + (System.Environment.TickCount - startTick));
            return NativeDeriveResult.Armed;
        }

        /// <summary>
        /// Plays the tool barrier back early and re-enables it: playback disables it, and later tool systems
        /// this frame create their buffers from it.
        /// </summary>
        private void FlushToolOutputBarrier()
        {
            _toolOutputBarrier.Update();
            _toolOutputBarrier.AllowUsage();
        }

        /// <summary>
        /// Flushes the barrier and removes the local preview it played back, as the definition gate does;
        /// the tool regenerates next update.
        /// </summary>
        private void DiscardBufferedLocalDefinitions()
        {
            FlushToolOutputBarrier();
            if (_freshDefinitions.IsEmptyIgnoreFilter) return;

            NativeArray<Entity> definitions = _freshDefinitions.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < definitions.Length; i++)
                    EntityManager.DestroyEntity(definitions[i]);
                if (definitions.Length > 0) _nativeNetCoordinator.ForceActiveToolUpdate();
            }
            finally
            {
                definitions.Dispose();
            }
        }

        /// <summary>
        /// Tags the generator's definitions Deleted: consumed this frame, destroyed at Cleanup, ignored by
        /// the gate.
        /// </summary>
        private int TagDerivedDefinitions()
        {
            if (_freshDefinitions.IsEmptyIgnoreFilter) return 0;
            NativeArray<Entity> definitions = _freshDefinitions.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < definitions.Length; i++)
                    EntityManager.AddComponent<Deleted>(definitions[i]);
                return definitions.Length;
            }
            finally
            {
                definitions.Dispose();
            }
        }
    }
}
