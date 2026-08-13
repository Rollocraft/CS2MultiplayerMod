using System.Reflection;
using Game.Common;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

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

        // The tool seed that produced the definitions currently standing. A one-shot Apply advances
        // the tool's own seed as part of applying, so the value sampled on the apply frame is already
        // the next one; the previous frame's sample is the one that built what just committed.
        private uint _lifecycleToolSeed;
        private uint _previousLifecycleToolSeed;

        /// <summary>
        /// Seed of the tool action that applied on this frame. Travels with upgrade/relocation
        /// commands so the receiver's generator draws the same variations and object seeds.
        /// </summary>
        public uint AppliedLifecycleToolSeed => _previousLifecycleToolSeed;

        /// <summary>True when the game exposes the definition generator this path drives.</summary>
        public bool CanDeriveNativeTransactions =>
            ResolveDeriveReflection() && _toolOutputBarrier != null && _objectToolSystem != null;

        private void InitializeNativeDerive()
        {
            _objectToolSystem = World.GetOrCreateSystemManaged<ObjectToolSystem>();
            _upgradeToolSystem = World.GetOrCreateSystemManaged<UpgradeToolSystem>();
            _toolOutputBarrier = World.GetOrCreateSystemManaged<ToolOutputBarrier>();

            // Every definition that is not already one of ours. Sync-created definitions carry
            // Deleted from birth - "consume me this frame, then go away" - which is also what keeps
            // DefinitionGateSystem off them, so the complement is exactly the local tool's.
            _freshDefinitions = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<CreationDefinition>() },
                None = new[] { ComponentType.ReadOnly<Deleted>() },
            });

            // A tool's definitions lose their Updated tag at the Cleanup after they were consumed and
            // are destroyed on its next update, so "definition without Updated" is the graph standing
            // behind the previews now committing. Our own definitions are born Deleted.
            _standingDefinitions = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<CreationDefinition>() },
                None = new[]
                {
                    ComponentType.ReadOnly<Updated>(),
                    ComponentType.ReadOnly<Deleted>(),
                },
            });
        }

        /// <summary>
        /// Sample the active object-lifecycle tool's seed once per frame; see
        /// <see cref="AppliedLifecycleToolSeed"/> for why the previous sample is the useful one.
        /// </summary>
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

            // Runtime access to the loaded game assembly's own definition generator. Every argument
            // is a public type; a rename in a future patch degrades to the reduced fallback paths.
            _createDefinitionsMethod = typeof(ObjectToolBaseSystem).GetMethod("CreateDefinitions",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (_createDefinitionsMethod != null &&
                _createDefinitionsMethod.GetParameters().Length != CreateDefinitionsArgumentCount)
                _createDefinitionsMethod = null;
            _randomSeedValueField = typeof(RandomSeed).GetField("m_Seed",
                BindingFlags.Instance | BindingFlags.NonPublic);

            if (_createDefinitionsMethod == null || _randomSeedValueField == null)
                Mod.log.Warn("[MP] BuildSync: the game's object definition generator is not " +
                             "reachable; upgrades and building moves fall back to reduced replication.");
            return _createDefinitionsMethod != null && _randomSeedValueField != null;
        }

        private const int CreateDefinitionsArgumentCount = 23;

        private static FieldInfo ToolSeedField(System.Type toolType)
        {
            FieldInfo field;
            if (_toolSeedFields.TryGetValue(toolType, out field)) return field;
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
        /// Reproduce a remote upgrade or relocation by running the game's own definition generator
        /// against this machine's world, with the inputs the tool had: the prefab, owner/original
        /// object, one control point (including its snapped target), and the tool's random seed.
        ///
        /// Everything else the transaction contains - the host building's re-commit, the road it
        /// attaches to, re-commits of every existing sub-net with its end nodes preserved, the
        /// <see cref="global::Game.Prefabs.CreationFlags.Delete"/> of host sub-nets the new footprint
        /// covers, and the lot-surface snapping of the new connection paths - is derived here from
        /// local geometry. Shipping the sender's finished definitions instead required resolving 230+
        /// of their entity references by geometry, which fails outright whenever the two machines have
        /// a road subdivided differently.
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
                // The local tool may already have buffered this frame's preview definitions. Play the
                // barrier back and drop them now, so that after the generator runs the only fresh
                // definitions standing are the ones it just produced.
                DiscardBufferedLocalDefinitions();

                controlPoints.Add(new ControlPoint
                {
                    m_Position = position,
                    m_HitPosition = position,
                    m_Rotation = rotation,
                    // The snapped road/node is semantic input, not just preview state. It drives
                    // attachment changes, route-lane movement, and old/new road re-commits.
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
                    // Stamping makes the generator omit the asset-stamp root object and expand the
                    // prefab's subnet/subobject/area graph directly, exactly as the local tool does.
                    stamping,
                    0f, 0f, 0f,                                 // brush size/angle/strength
                    0f,                                         // distance (0 = single placement)
                    0f,                                         // deltaTime (creature spawning only)
                    MakeRandomSeed(toolSeed),
                    // Snap only reaches the brush and curve branches, neither of which a single
                    // placement takes.
                    Snap.None,
                    AgeMask.Sapling,
                    false,                                      // decorationMode
                    default(JobHandle),
                };

                object handle = _createDefinitionsMethod.Invoke(tool, arguments);
                if (handle is JobHandle) ((JobHandle)handle).Complete();

                // Materialise what the generator buffered. Nothing else ran in between, so every
                // fresh definition standing now belongs to this transaction.
                FlushToolOutputBarrier();
            }
            catch (System.Exception ex)
            {
                controlPoints.Dispose();
                _nativeNetCoordinator.CancelPreparedDefinitionFrame();
                // Reflection wraps whatever the generator threw; the inner one is the useful message.
                System.Exception cause = ex.InnerException ?? ex;
                Mod.log.Warn("[MP] BuildSync: the game's definition generator rejected " + source +
                             "; dropping this edit: " + cause.Message);
                Diagnostics.FlightRecorder.Note("native derive rejected=" + cause.GetType().Name);
                return NativeDeriveResult.Failed;
            }
            finally
            {
                // Whatever happened above, the tool barrier must be left usable: the rest of this
                // frame's tool phases create their command buffers from it.
                _toolOutputBarrier.AllowUsage();
            }
            controlPoints.Dispose();

            int derived = TagDerivedDefinitions();
            if (derived == 0)
            {
                _nativeNetCoordinator.CancelPreparedDefinitionFrame();
                Diagnostics.FlightRecorder.Note("native derive produced no definitions (" + source + ")");
                return NativeDeriveResult.Failed;
            }

            // A stamp graph is rootless by design - the generator omits the stamp's own object -
            // so it must be armed as one, or transaction validation rejects it for having no
            // top-level object and the placement is replayed until it is dropped.
            if (!_nativeNetCoordinator.ArmObjectCommit(onCommitLost, onCommitComplete,
                    "derived " + source + " defs=" + derived, stamping))
            {
                _nativeNetCoordinator.CancelPreparedDefinitionFrame();
                return NativeDeriveResult.Busy;
            }

            Diagnostics.FlightRecorder.Note("native derive " + source + " defs=" + derived +
                " seed=" + toolSeed + " deriveMS=" + (System.Environment.TickCount - startTick));
            return NativeDeriveResult.Armed;
        }

        /// <summary>
        /// Play the tool output barrier back early, then re-enable it.
        ///
        /// Playing it back is how definitions buffered into it become entities we can see. But the
        /// barrier disables itself as it plays back, and every tool system that runs later in the
        /// frame - the clear pass, the apply pass, the tools' own definition destruction - creates its
        /// command buffer from it. Leaving it disabled therefore breaks the whole local tool pipeline
        /// for the rest of the frame, which is what <c>AllowUsage</c> exists for.
        /// </summary>
        private void FlushToolOutputBarrier()
        {
            _toolOutputBarrier.Update();
            _toolOutputBarrier.AllowUsage();
        }

        /// <summary>
        /// Flush the tool output barrier and remove the local preview definitions it just played
        /// back. This is the same rule <see cref="DefinitionGateSystem"/> applies on an armed frame,
        /// brought forward so the generator's output can be identified without ambiguity. The tool is
        /// asked to regenerate, so the visible preview returns on its next update.
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
        /// Mark the generator's fresh definitions as ours. Deleted on a definition means "consume me
        /// this frame, then go away": the Generate* systems still read it, Cleanup destroys it, and
        /// the definition gate leaves it alone.
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
