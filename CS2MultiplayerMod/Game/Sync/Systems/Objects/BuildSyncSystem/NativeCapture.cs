using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.Infrastructure;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    public partial class BuildSyncSystem
    {
        private sealed class RecentLocalObjectOperation
        {
            public ObjectToolOperationCommand Operation;
            public long ObservedAtMs;
        }

        private struct PlayerPlacedSpawnableCreation
        {
            public Entity Prefab;
            public float3 Position;
            public float4 Rotation;
            public ushort RandomSeed;
            public long ExpiryMs;
        }

        private const int MaxRecentLocalObjectOperations = 32;
        private const long RecentLocalObjectOperationLifetimeMs = 5000;
        private const float StrictCommittedRootMatchDistanceSq = 0.0001f;
        private const float AttachedCommittedRootMatchRadiusSq = 64f;
        private const float AttachedCommittedRootMatchHeight = 20f;
        private const float StrictCommittedRootRotationDot = 0.99999f;
        private const int MaxPlayerPlacedSpawnableCreations = 128;
        private const long PlayerPlacedSpawnableLifetimeMs = 15000;
        private const float PlayerPlacedSpawnableMatchDistanceSq = 0.01f;
        private const float PlayerPlacedSpawnableMatchRotationDot = 0.9999f;
        private const float AttachedPlayerPlacedSpawnableMatchRadiusSq = 64f;
        private const float AttachedPlayerPlacedSpawnableMatchHeight = 20f;

        private ObjectToolOperationCommand _cachedLocalObjectOperation;
        // Why the last committed root failed to bind to a preview graph; read by the escalation path.
        private string _lastObjectGraphMissDetail;
        private readonly List<RecentLocalObjectOperation> _recentLocalObjectOperations =
            new List<RecentLocalObjectOperation>(MaxRecentLocalObjectOperations);
        private readonly List<PlayerPlacedSpawnableCreation> _playerPlacedSpawnableCreations =
            new List<PlayerPlacedSpawnableCreation>(8);
        // Sampled before ToolOutputSystem runs. A one-shot stamp can switch active tools while its
        // rootless definition graph is being emitted, so the graph itself cannot tell us which
        // AssetStampPrefab owns the construction cost/contract.
        private string _selectedAssetStampPrefabName;
        private long _nextLocalObjectOperationId = 1;
        private bool _nativeLifecycleCapturedThisFrame;
        private ObjectToolOperationCommand _pendingSpecializedObjectOperation;
        private ObjectToolDefinitionIntent _pendingSpecializedAreaDefinition;
        private Entity _pendingSpecializedArea;
        private bool _completeSpecializedAreaThisFrame;

        /// <summary>
        /// True through ModificationEnd when this frame's object-tool Apply was already published
        /// from native definitions. Legacy final-entity capture systems use it to avoid sending a
        /// second, reduced representation of the same placement, extension, or relocation.
        /// </summary>
        public bool NativeLifecycleCapturedThisFrame => _nativeLifecycleCapturedThisFrame;

        /// <summary>
        /// True while the object half of a specialized-industry placement is held for its area tool to
        /// finish the polygon. The compact upgrade command cannot describe that polygon, so it must
        /// not publish a stand-in while the complete transaction is still being assembled.
        /// </summary>
        internal bool HasPendingSpecializedAreaCapture =>
            _pendingSpecializedObjectOperation != null ||
            (_areaToolSystem != null && _areaToolSystem.recreate != Entity.Null);

        /// <summary>
        /// Observe the object/area tool hand-off after the output barrier. Ordinary object graphs
        /// are captured once from the standing definitions on Apply. Network-owned object graphs
        /// are the exception: retain their small, exact preview batch in the recent-root set so a
        /// one-shot network prefab remains recoverable even if it switches tools while applying.
        /// </summary>
        public void ObserveLocalObjectToolOutput(NativeArray<Entity> definitions)
        {
            ObserveLocalObjectToolStateAfterOutput();

            global::Game.Tools.ToolBaseSystem active =
                _toolSystem != null ? _toolSystem.activeTool : null;
            if (!(active is global::Game.Tools.NetToolSystem) ||
                !NativeObjectGraph.HasNewTopLevelObjectRoot(EntityManager, definitions)) return;

            // Owner-linked courses are not independent net placements. Capture the heterogeneous
            // object/net/area graph together, exactly like an intersection asset transaction. The
            // operation is retained by RememberRecentLocalObjectOperation; do not leave it as the
            // current object-tool cache where a later, unrelated lifecycle tool could claim it.
            CaptureObjectToolOperation(definitions);
            _cachedLocalObjectOperation = null;
        }

        /// <summary>
        /// Advance the specialized-industry object/area hand-off after tool output. The expensive
        /// definition encoding deliberately does not happen here: regenerated hover previews pass
        /// this point many times per second.
        /// </summary>
        private void ObserveLocalObjectToolStateAfterOutput()
        {
            global::Game.Tools.ToolBaseSystem active = _toolSystem != null ? _toolSystem.activeTool : null;
            Entity recreate = _areaToolSystem != null ? _areaToolSystem.recreate : Entity.Null;

            // Specialized-industry placement is one native action split across two tools. The
            // object tool first commits the main building and hands its owned lot to the area tool;
            // only after the polygon closes does the area tool return to the object tool. Preserve
            // the standing object definition through that handoff, then publish it with the final
            // extractor/storage polygon as one atomic operation.
            bool areaHandoff = recreate != Entity.Null &&
                               (active is AreaToolSystem || active is ObjectToolSystem);
            if (areaHandoff)
            {
                if (_pendingSpecializedObjectOperation == null &&
                    _cachedLocalObjectOperation != null &&
                    TryBeginSpecializedAreaCapture(recreate))
                {
                    Diagnostics.FlightRecorder.Note("specialized object/area handoff tracked");
                }

                if (_pendingSpecializedObjectOperation != null)
                {
                    if (_pendingSpecializedArea != recreate ||
                        !SpecializedAreaOwnerStillMatches(recreate,
                            _pendingSpecializedObjectOperation))
                    {
                        FinishSpecializedAreaCaptureWithoutPolygon();
                    }
                    else
                    {
                        // On the completion frame AreaToolSystem switches activeTool back to the
                        // object tool, while ToolSystem.applyMode still belongs to the area tool
                        // that produced this output batch. The committed live area is captured at
                        // ModificationEnd; the final click does not emit a new definition batch.
                        if (active is ObjectToolSystem &&
                            _toolSystem.applyMode == ApplyMode.Apply)
                            _completeSpecializedAreaThisFrame = true;
                        return;
                    }
                }

                if (active is AreaToolSystem)
                {
                    _cachedLocalObjectOperation = null;
                    return;
                }
            }

            // The area tool is gone without a completed polygon: the placement stands with the
            // lot it was born with, so the held object graph is the whole local change.
            if (_pendingSpecializedObjectOperation != null)
                FinishSpecializedAreaCaptureWithoutPolygon();
        }

        private static bool IsObjectLifecycleTool(global::Game.Tools.ToolBaseSystem tool) =>
            tool is ObjectToolSystem || tool is UpgradeToolSystem;

        /// <summary>
        /// Cheap flag-only scan: true when this output batch relocates an existing object (the move
        /// tool). Reads one component per definition and returns on the first Relocate, so it stays
        /// far below the cost of the full owner-path capture it lets us skip.
        /// </summary>
        private bool BatchIsRelocate(NativeArray<Entity> definitions)
        {
            for (int i = 0; i < definitions.Length; i++)
            {
                Entity entity = definitions[i];
                if (!EntityManager.Exists(entity) ||
                    !EntityManager.HasComponent<CreationDefinition>(entity)) continue;
                CreationDefinition creation = EntityManager.GetComponentData<CreationDefinition>(entity);
                if ((creation.m_Flags & CreationFlags.Relocate) != 0) return true;
            }
            return false;
        }

        /// <summary>
        /// Cheap component-only scan: true when this batch adds a service upgrade / extension to a
        /// pre-existing (live) building — the deliberate "add a tower" action UpgradeSyncSystem
        /// replicates atomically.
        ///
        /// Two shapes must both be present, and together they separate this action from every
        /// neighbouring one without touching the world:
        /// <list type="bullet">
        /// <item>a new upgrade/extension object: its prefab carries <see cref="ServiceUpgradeData"/>
        /// or <see cref="BuildingExtensionData"/>, it has an <see cref="OwnerDefinition"/> (the way
        /// the tools name the host building — <c>CreationDefinition.m_Owner</c> stays null for the
        /// object being placed, so testing that field never matched), and it has no original;</item>
        /// <item>the host building's own modify definition: no prefab, <see cref="CreationFlags.Upgrade"/>,
        /// and an original that is already a live object. A brand-new building emits no such
        /// definition (there is nothing live to modify), so a placement is never diverted.</item>
        /// </list>
        /// Upgrades whose lot is drawn by the player (extractor/storage areas) are excluded: only the
        /// native two-tool transaction can carry that polygon.
        /// </summary>
        private bool BatchIsServiceUpgradeOnLiveBuilding(NativeArray<Entity> definitions)
        {
            bool hasNewUpgradeObject = false;
            bool hasLiveHostModification = false;
            for (int i = 0; i < definitions.Length; i++)
            {
                Entity entity = definitions[i];
                if (!EntityManager.Exists(entity) ||
                    !EntityManager.HasComponent<CreationDefinition>(entity)) continue;
                CreationDefinition creation = EntityManager.GetComponentData<CreationDefinition>(entity);

                if (!hasLiveHostModification && creation.m_Prefab == Entity.Null &&
                    (creation.m_Flags & CreationFlags.Upgrade) != 0 &&
                    IsLiveUpgradeHost(creation.m_Original))
                    hasLiveHostModification = true;

                if (!hasNewUpgradeObject && creation.m_Prefab != Entity.Null &&
                    creation.m_Original == Entity.Null &&
                    EntityManager.HasComponent<ObjectDefinition>(entity) &&
                    EntityManager.HasComponent<OwnerDefinition>(entity) &&
                    (EntityManager.HasComponent<ServiceUpgradeData>(creation.m_Prefab) ||
                     EntityManager.HasComponent<BuildingExtensionData>(creation.m_Prefab)) &&
                    UpgradeOwnedGraphIsPrefabDeterministic(creation.m_Prefab))
                    hasNewUpgradeObject = true;

                if (hasNewUpgradeObject && hasLiveHostModification) return true;
            }
            return false;
        }

        /// <summary>An already-committed object the tools can modify (not a preview, not this frame's).</summary>
        private bool IsLiveUpgradeHost(Entity entity)
        {
            return entity != Entity.Null && EntityManager.Exists(entity) &&
                   EntityManager.HasComponent<global::Game.Objects.Object>(entity) &&
                   EntityManager.HasComponent<global::Game.Objects.Transform>(entity) &&
                   !EntityManager.HasComponent<Temp>(entity) &&
                   !EntityManager.HasComponent<Deleted>(entity) &&
                   !EntityManager.HasComponent<Created>(entity);
        }

        /// <summary>
        /// True when an upgrade's owned elements can be rebuilt on the peer from its prefab alone.
        /// An extractor/storage sub-area is drawn by the player through a second tool, so its polygon
        /// exists nowhere in the prefab and only the native transaction can carry it.
        /// </summary>
        internal bool UpgradeOwnedGraphIsPrefabDeterministic(Entity prefab)
        {
            if (!EntityManager.HasBuffer<SubArea>(prefab)) return true;
            DynamicBuffer<SubArea> subAreas =
                EntityManager.GetBuffer<SubArea>(prefab, isReadOnly: true);
            for (int i = 0; i < subAreas.Length; i++)
            {
                Entity declared = subAreas[i].m_Prefab;
                if (declared == Entity.Null || !EntityManager.Exists(declared)) continue;
                if (IsSpecializedAreaPrefab(declared)) return false;
                if (!EntityManager.HasBuffer<PlaceholderObjectElement>(declared)) continue;
                DynamicBuffer<PlaceholderObjectElement> candidates =
                    EntityManager.GetBuffer<PlaceholderObjectElement>(declared, isReadOnly: true);
                for (int j = 0; j < candidates.Length; j++)
                    if (IsSpecializedAreaPrefab(candidates[j].m_Object)) return false;
            }
            return true;
        }

        private void CaptureObjectToolOperation(NativeArray<Entity> definitions)
        {
            // A relocation or an upgrade of a live building is not shipped as definitions at all.
            // Capturing one walks that building's full owned-element buffers once for every one of
            // its (often 100+) sub-elements and scores every object definition against the whole
            // city's object set - every frame the preview stands, which is the FPS collapse while
            // moving and while positioning an extension. Replaying one then required resolving all
            // those references on the receiver, which cannot succeed when the two machines have a
            // road subdivided differently.
            //
            // Both instead travel as the compact inputs their tool had (MoveSyncSystem,
            // UpgradeSyncSystem) and the receiver re-runs the game's own generator over them.
            // Upgrades whose lot the player draws are the exception: no compact form can carry that
            // polygon, so BatchIsServiceUpgradeOnLiveBuilding deliberately does not claim them.
            if (BatchIsRelocate(definitions) || BatchIsServiceUpgradeOnLiveBuilding(definitions))
            {
                _cachedLocalObjectOperation = null;
                return;
            }

            var captured = new List<ObjectToolDefinitionIntent>();
            int root = -1;
            int rootScore = -1;
            bool hasStampingNet = false;
            bool hasFixedElementCut = false;
            // Root scoring asks whether a definition's owner names a live building, which searches the
            // object domain. Capture is read-only, so one snapshot serves the whole batch: without it
            // a 116-definition building preview walked the city's ~270k objects once per definition,
            // every frame the preview stood.
            BeginPortableResolve();
            try
            {
                for (int i = 0; i < definitions.Length; i++)
                {
                    Entity entity = definitions[i];
                    if (!EntityManager.Exists(entity) ||
                        !EntityManager.HasComponent<CreationDefinition>(entity)) continue;

                    ObjectToolDefinitionIntent definition;
                    if (!TryCaptureObjectToolDefinition(entity, out definition))
                    {
                        // Never publish a partial native action. The final-entity legacy path remains
                        // available for unsupported tool output, but this cache is all-or-nothing.
                        _cachedLocalObjectOperation = null;
                        return;
                    }

                    // Owned subobjects carry OwnerDefinition, while the top-level object does not.
                    // Prefer that structural distinction first, then a newly-created object over an
                    // update definition for an existing owner (the usual attached-upgrade ordering).
                    // Scored once per definition, never re-scoring the incumbent.
                    if (definition.Kind == ObjectToolDefinitionKind.Object)
                    {
                        int score = ObjectOperationRootScore(definition);
                        if (score > rootScore)
                        {
                            root = captured.Count;
                            rootScore = score;
                        }
                    }
                    if (definition.Kind == ObjectToolDefinitionKind.NetCourse &&
                        (((CreationFlags)definition.CreationFlags & CreationFlags.Stamping) != 0))
                        hasStampingNet = true;
                    if (CourseCarriesFixedElementCut(definition)) hasFixedElementCut = true;
                    captured.Add(definition);
                    if (captured.Count > ObjectToolOperationCommand.MaxDefinitions)
                    {
                        _cachedLocalObjectOperation = null;
                        return;
                    }
                }
            }
            finally
            {
                EndPortableResolve();
            }

            if (captured.Count == 0)
            {
                // ObjectToolSystem emits no definitions while an unchanged preview is standing and
                // reports ApplyMode.None. ToolOutputSystem leaves the existing Temp graph intact in
                // that case, so an empty barrier batch means "unchanged", not "no preview". Erasing
                // the cache here made stamp capture depend on clicking in the same frame as cursor
                // movement. Clear only when the tool is actively clearing/applying its output.
                if (_toolSystem == null || _toolSystem.applyMode != ApplyMode.None)
                    _cachedLocalObjectOperation = null;
                return;
            }

            if (!hasStampingNet && root < 0)
            {
                _cachedLocalObjectOperation = null;
                return;
            }

            string stampPrefabName = null;
            if (hasStampingNet)
            {
                stampPrefabName = GetSelectedAssetStampPrefabName(
                    _toolSystem != null ? _toolSystem.activeTool : null) ??
                    _selectedAssetStampPrefabName;
                if (string.IsNullOrEmpty(stampPrefabName))
                {
                    _cachedLocalObjectOperation = null;
                    Diagnostics.FlightRecorder.Note("asset stamp definitions lacked selected prefab");
                    return;
                }
                // Any ObjectDefinitions in this output are independently placed stamp subobjects,
                // not a persistent owner for the subnet graph.
                root = ObjectToolOperationCommand.AssetStampRootIndex;
            }

            var operation = new ObjectToolOperationCommand
            {
                RootIndex = (short)root,
                AssetStampPrefabName = stampPrefabName,
                Definitions = captured.ToArray(),
            };
            AttachPlacementInput(operation);

            // A net built from repeating fixed elements - a dam - reaches the standing graph already
            // divided into those elements, one course each. Publishing that division makes the peer
            // divide every piece a second time, so each module becomes a whole miniature dam.
            // Prefer the undivided graph the output barrier saw a frame earlier: the receiver then
            // divides it exactly once, as a local apply does.
            ObjectToolOperationCommand undivided;
            if (hasFixedElementCut && TryFindUndividedFixedNetOperation(operation, out undivided))
            {
                AttachPlacementInput(undivided);
                _cachedLocalObjectOperation = undivided;
                RememberRecentLocalObjectOperation(undivided);
                Diagnostics.FlightRecorder.Note("fixed-element net kept undivided defs=" +
                    undivided.Definitions.Length + " divided=" + captured.Count);
                return;
            }
            if (hasFixedElementCut)
                Diagnostics.FlightRecorder.Note(
                    "fixed-element net has no undivided graph; publishing divided defs=" +
                    captured.Count);

            _cachedLocalObjectOperation = operation;
            RememberRecentLocalObjectOperation(_cachedLocalObjectOperation);
            Diagnostics.FlightRecorder.Note(hasStampingNet
                ? "asset stamp native definitions captured=" + captured.Count +
                  " prefab=" + stampPrefabName
                : "object native definitions captured=" + captured.Count +
                  " root=" + captured[root].PrefabName +
                  " seed=" + unchecked((ushort)captured[root].RandomSeed));
        }

        /// <summary>
        /// True when this course is one element of a fixed-element net that the receiver would
        /// divide again. The network tool always emits <c>-1</c> for a course it draws; a
        /// non-negative element index means the division has already happened. A course that names
        /// an original is exempt on both machines, so it is not treated as divided.
        /// </summary>
        private static bool CourseCarriesFixedElementCut(ObjectToolDefinitionIntent definition)
        {
            return definition.Kind == ObjectToolDefinitionKind.NetCourse &&
                   !definition.PrefabIsNull &&
                   definition.NetCourse.FixedIndex >= 0 &&
                   definition.Original.Kind == PortableEntityKind.None;
        }

        private static bool ContainsFixedElementCut(ObjectToolDefinitionIntent[] definitions)
        {
            if (definitions == null) return false;
            for (int i = 0; i < definitions.Length; i++)
                if (CourseCarriesFixedElementCut(definitions[i])) return true;
            return false;
        }

        /// <summary>
        /// Find the undivided graph already held for the same root. Preview frames are observed
        /// before the division runs, so the newest held graph for a root is its own ancestor.
        /// Returns false when there is none, in which case the caller keeps the divided graph
        /// rather than dropping the placement.
        /// </summary>
        private bool TryFindUndividedFixedNetOperation(ObjectToolOperationCommand divided,
            out ObjectToolOperationCommand result)
        {
            result = null;
            ObjectToolDefinitionIntent root;
            if (!TryGetNewCommittedObjectRoot(divided, out root)) return false;

            for (int i = _recentLocalObjectOperations.Count - 1; i >= 0; i--)
            {
                ObjectToolOperationCommand candidate = _recentLocalObjectOperations[i].Operation;
                ObjectToolDefinitionIntent candidateRoot;
                if (!TryGetNewCommittedObjectRoot(candidate, out candidateRoot) ||
                    !SameRootSignature(root, candidateRoot) ||
                    ContainsFixedElementCut(candidate.Definitions)) continue;
                result = candidate;
                return true;
            }
            return false;
        }

        private int ObjectOperationRootScore(ObjectToolDefinitionIntent definition)
        {
            int score = 0;
            // An upgrade preview also contains an update definition for the existing building.
            // That definition has no prefab and used to outrank the newly-created extension,
            // leaving the complete preview graph without a committed entity it could bind to.
            if (IsNewServiceUpgradeRoot(definition)) score |= 16;
            if (!definition.HasOwnerDefinition) score |= 4;
            if (definition.Original.Kind == PortableEntityKind.None) score |= 2;
            if (definition.Owner.Kind == PortableEntityKind.None) score |= 1;
            return score;
        }

        private bool IsNewServiceUpgradeRoot(ObjectToolDefinitionIntent definition)
        {
            if (definition == null || definition.Kind != ObjectToolDefinitionKind.Object ||
                definition.PrefabIsNull || string.IsNullOrEmpty(definition.PrefabName) ||
                definition.Original.Kind != PortableEntityKind.None ||
                definition.Owner.Kind != PortableEntityKind.None ||
                !definition.HasOwnerDefinition) return false;

            CreationFlags flags = (CreationFlags)definition.CreationFlags;
            if ((flags & (CreationFlags.Delete | CreationFlags.Relocate |
                          CreationFlags.Recreate | CreationFlags.Permanent)) != 0) return false;

            Entity prefab;
            if (!_prefabIndex.TryResolve(definition.PrefabName, out prefab) ||
                (!EntityManager.HasComponent<ServiceUpgradeData>(prefab) &&
                 !EntityManager.HasComponent<BuildingExtensionData>(prefab)))
                return false;

            // Service-upgrade definitions identify their existing building through
            // OwnerDefinition. They do not necessarily carry CreationFlags.Upgrade. Requiring the
            // owner to be live distinguishes this action from integral owned objects emitted while
            // a brand-new building is still only a preview.
            Entity ownerPrefab;
            if (!_prefabIndex.TryResolve(definition.OwnerDefinitionPrefabName, out ownerPrefab))
                return false;
            return FindPortableObject(ownerPrefab,
                       new float3(definition.OwnerDefinitionX, definition.OwnerDefinitionY,
                           definition.OwnerDefinitionZ),
                       default(PortableEntityRef)) != Entity.Null;
        }

        private void RememberSelectedAssetStampPrefab(global::Game.Tools.ToolBaseSystem active)
        {
            _selectedAssetStampPrefabName = GetSelectedAssetStampPrefabName(active);
        }

        private string GetSelectedAssetStampPrefabName(global::Game.Tools.ToolBaseSystem active)
        {
            PrefabBase selected = active != null ? active.GetPrefab() : null;
            if (!(selected is AssetStampPrefab)) return null;
            Entity prefab;
            if (!_prefabSystem.TryGetEntity(selected, out prefab) || prefab == Entity.Null ||
                !EntityManager.Exists(prefab) || !EntityManager.HasComponent<AssetStampData>(prefab))
                return null;
            return _prefabSystem.GetPrefabName(prefab);
        }

        private void RememberRecentLocalObjectOperation(ObjectToolOperationCommand operation)
        {
            ObjectToolDefinitionIntent root;
            if (!TryGetNewCommittedObjectRoot(operation, out root)) return;

            long now = Mod.Service != null ? Mod.Service.NowMs : 0;
            PruneRecentLocalObjectOperations(now);
            for (int i = _recentLocalObjectOperations.Count - 1; i >= 0; i--)
            {
                RecentLocalObjectOperation recent = _recentLocalObjectOperations[i];
                ObjectToolDefinitionIntent recentRoot;
                if (!TryGetNewCommittedObjectRoot(recent.Operation, out recentRoot) ||
                    !SameRootSignature(root, recentRoot)) continue;

                recent.Operation = operation;
                recent.ObservedAtMs = now;
                _recentLocalObjectOperations.RemoveAt(i);
                _recentLocalObjectOperations.Add(recent);
                return;
            }

            _recentLocalObjectOperations.Add(new RecentLocalObjectOperation
            {
                Operation = operation,
                ObservedAtMs = now,
            });
            if (_recentLocalObjectOperations.Count > MaxRecentLocalObjectOperations)
                _recentLocalObjectOperations.RemoveAt(0);
        }

        private bool TryGetNewCommittedObjectRoot(ObjectToolOperationCommand operation,
            out ObjectToolDefinitionIntent root)
        {
            root = null;
            if (operation == null || operation.IsAssetStamp || operation.Definitions == null ||
                operation.RootIndex < 0 || operation.RootIndex >= operation.Definitions.Length)
                return false;

            root = operation.Definitions[operation.RootIndex];
            if (root == null || root.Kind != ObjectToolDefinitionKind.Object ||
                root.PrefabIsNull || string.IsNullOrEmpty(root.PrefabName) ||
                root.Original.Kind != PortableEntityKind.None)
                return false;

            CreationFlags flags = (CreationFlags)root.CreationFlags;
            if ((flags & (CreationFlags.Delete | CreationFlags.Relocate |
                          CreationFlags.Recreate | CreationFlags.Permanent)) != 0) return false;

            if (IsNewServiceUpgradeRoot(root)) return true;
            return root.Owner.Kind == PortableEntityKind.None && !root.HasOwnerDefinition &&
                   (flags & CreationFlags.Upgrade) == 0;
        }

        private static bool SameRootSignature(ObjectToolDefinitionIntent left,
            ObjectToolDefinitionIntent right)
        {
            if (!string.Equals(left.PrefabName, right.PrefabName,
                    System.StringComparison.Ordinal) ||
                left.RandomSeed != right.RandomSeed ||
                left.CreationFlags != right.CreationFlags) return false;

            float3 leftPosition = new float3(left.Object.PosX, left.Object.PosY,
                left.Object.PosZ);
            float3 rightPosition = new float3(right.Object.PosX, right.Object.PosY,
                right.Object.PosZ);
            if (math.distancesq(leftPosition, rightPosition) > 0.0001f) return false;

            float4 leftRotation = new float4(left.Object.RotX, left.Object.RotY,
                left.Object.RotZ, left.Object.RotW);
            float4 rightRotation = new float4(right.Object.RotX, right.Object.RotY,
                right.Object.RotZ, right.Object.RotW);
            return math.abs(math.dot(leftRotation, rightRotation)) >= 0.99999f;
        }

        private void PruneRecentLocalObjectOperations(long now)
        {
            if (now <= 0) return;
            for (int i = _recentLocalObjectOperations.Count - 1; i >= 0; i--)
            {
                long observedAt = _recentLocalObjectOperations[i].ObservedAtMs;
                if (observedAt > 0 && now >= observedAt &&
                    now - observedAt > RecentLocalObjectOperationLifetimeMs)
                    _recentLocalObjectOperations.RemoveAt(i);
            }
        }

        private void ForgetRecentLocalObjectOperation(ObjectToolOperationCommand operation)
        {
            if (operation == null) return;
            for (int i = _recentLocalObjectOperations.Count - 1; i >= 0; i--)
                if (object.ReferenceEquals(_recentLocalObjectOperations[i].Operation, operation))
                    _recentLocalObjectOperations.RemoveAt(i);
        }

        private void ClearRecentLocalObjectOperations()
        {
            _recentLocalObjectOperations.Clear();
        }

        /// <summary>
        /// Correlate any newly-applied object, including an owned service extension, with the exact
        /// object-tool graph that produced it. This runs before the reduced top-level and upgrade
        /// capture paths, so one successful match owns the whole native transaction.
        /// </summary>
        private bool TryPublishCommittedObjectGraph(long now)
        {
            if (_recentLocalObjectOperations.Count == 0 ||
                _nativeLifecycleCapturedThisFrame ||
                (_nativeNetCoordinator != null &&
                 _nativeNetCoordinator.DidCommitObjectGraphThisFrame) ||
                _createdAppliedObjects.IsEmptyIgnoreFilter) return false;

            NativeArray<Entity> entities = _createdAppliedObjects.ToEntityArray(Allocator.Temp);
            try
            {
                var created = new List<Entity>(entities.Length);
                for (int i = 0; i < entities.Length; i++) created.Add(entities[i]);
                return TryPublishMatchingRecentLocalObjectOperation(created, now);
            }
            finally
            {
                entities.Dispose();
            }
        }

        /// <summary>
        /// Bind a full preview graph to the root entity that demonstrably committed. Generated
        /// objects preserve the definition's prefab, transform, and pseudo-random seed, providing
        /// a stable identity after the transient tool Apply pulse has disappeared.
        /// </summary>
        private bool TryPublishMatchingRecentLocalObjectOperation(List<Entity> created, long now)
        {
            PruneRecentLocalObjectOperations(now);
            if (_recentLocalObjectOperations.Count == 0) return false;

            for (int entityIndex = 0; entityIndex < created.Count; entityIndex++)
            {
                Entity entity = created[entityIndex];
                if (!EntityManager.Exists(entity) ||
                    !EntityManager.HasComponent<Applied>(entity) ||
                    !EntityManager.HasComponent<PrefabRef>(entity) ||
                    !EntityManager.HasComponent<global::Game.Objects.Transform>(entity) ||
                    !EntityManager.HasComponent<PseudoRandomSeed>(entity)) continue;

                Entity prefab = EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab;
                string prefabName = _prefabSystem.GetPrefabName(prefab);
                global::Game.Objects.Transform transform =
                    EntityManager.GetComponentData<global::Game.Objects.Transform>(entity);
                ushort randomSeed = unchecked((ushort)
                    EntityManager.GetComponentData<PseudoRandomSeed>(entity).m_Seed);

                for (int i = _recentLocalObjectOperations.Count - 1; i >= 0; i--)
                {
                    ObjectToolOperationCommand operation =
                        _recentLocalObjectOperations[i].Operation;
                    ObjectToolDefinitionIntent root;
                    if (!TryGetNewCommittedObjectRoot(operation, out root) ||
                        !CommittedRootMatches(root, prefabName, transform, randomSeed)) continue;

                    int definitionCount = operation.Definitions.Length;
                    try
                    {
                        if (!TryPublishLocalObjectOperation(operation)) return false;
                        if (object.ReferenceEquals(_cachedLocalObjectOperation, operation))
                            _cachedLocalObjectOperation = null;
                        Diagnostics.FlightRecorder.Note("object graph matched committed root op=" +
                            operation.OperationId + " defs=" + definitionCount +
                            " prefab=" + prefabName + " seed=" + randomSeed);
                        return true;
                    }
                    catch (System.Exception ex)
                    {
                        ForgetRecentLocalObjectOperation(operation);
                        if (object.ReferenceEquals(_cachedLocalObjectOperation, operation))
                            _cachedLocalObjectOperation = null;
                        Mod.log.Warn("[MP] BuildSync: committed object graph was not sent: " +
                                     ex.Message);
                        Diagnostics.FlightRecorder.Note("committed object graph rejected=" +
                                                          ex.GetType().Name);
                        if (Mod.Service != null)
                            Mod.Service.RequestAutomaticWorldRecovery(
                                "committed building graph could not be sent");
                        return false;
                    }
                }
            }
            return false;
        }

        private static bool CommittedRootMatches(ObjectToolDefinitionIntent root,
            string prefabName, global::Game.Objects.Transform transform, ushort randomSeed)
        {
            if (!string.Equals(root.PrefabName, prefabName, System.StringComparison.Ordinal) ||
                unchecked((ushort)root.RandomSeed) != randomSeed) return false;

            float3 expectedPosition = new float3(root.Object.PosX, root.Object.PosY,
                root.Object.PosZ);

            // Ordinary objects preserve the definition transform verbatim. Road-attached objects
            // are different: the attachment pass snaps and rotates the committed root after the
            // definition was sampled. Prefab + random seed still provide the operation identity;
            // bounded horizontal/vertical checks prevent an unrelated attachment from claiming it
            // after a seed reuse while still allowing terrain and elevated-road height correction.
            if (HasAttachedCommitIntent(root))
                return math.distancesq(expectedPosition.xz, transform.m_Position.xz) <=
                           AttachedCommittedRootMatchRadiusSq &&
                       math.abs(expectedPosition.y - transform.m_Position.y) <=
                           AttachedCommittedRootMatchHeight;

            if (math.distancesq(expectedPosition, transform.m_Position) >
                StrictCommittedRootMatchDistanceSq) return false;

            float4 expectedRotation = new float4(root.Object.RotX, root.Object.RotY,
                root.Object.RotZ, root.Object.RotW);
            return math.abs(math.dot(expectedRotation, transform.m_Rotation.value)) >=
                   StrictCommittedRootRotationDot;
        }

        private static bool HasAttachedCommitIntent(ObjectToolDefinitionIntent root) =>
            root.Attached.Kind != PortableEntityKind.None ||
            !string.IsNullOrEmpty(root.AttachedPrefabName) ||
            ((CreationFlags)root.CreationFlags & CreationFlags.Attach) != 0;

        private void NoteCommittedObjectGraphMiss(List<Entity> created)
        {
            if (created.Count == 0) return;
            Entity entity = created[0];
            for (int i = 0; i < created.Count; i++)
            {
                Entity candidate = created[i];
                if (EntityManager.Exists(candidate) &&
                    EntityManager.HasComponent<Applied>(candidate) &&
                    EntityManager.HasComponent<PseudoRandomSeed>(candidate))
                {
                    entity = candidate;
                    break;
                }
            }
            string prefabName = "unknown";
            string seed = "none";
            if (EntityManager.Exists(entity) && EntityManager.HasComponent<PrefabRef>(entity))
            {
                Entity prefab = EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab;
                prefabName = _prefabSystem.GetPrefabName(prefab) ?? "unknown";
            }
            if (EntityManager.Exists(entity) && EntityManager.HasComponent<PseudoRandomSeed>(entity))
                seed = EntityManager.GetComponentData<PseudoRandomSeed>(entity).m_Seed.ToString();

            string newest = "none";
            string matchingIdentity = string.Empty;
            if (_recentLocalObjectOperations.Count > 0)
            {
                ObjectToolDefinitionIntent root;
                if (TryGetNewCommittedObjectRoot(
                        _recentLocalObjectOperations[_recentLocalObjectOperations.Count - 1].Operation,
                        out root))
                    newest = root.PrefabName + "/" + unchecked((ushort)root.RandomSeed);

                if (EntityManager.Exists(entity) &&
                    EntityManager.HasComponent<global::Game.Objects.Transform>(entity) &&
                    EntityManager.HasComponent<PseudoRandomSeed>(entity))
                {
                    ushort committedSeed = unchecked((ushort)
                        EntityManager.GetComponentData<PseudoRandomSeed>(entity).m_Seed);
                    global::Game.Objects.Transform committedTransform =
                        EntityManager.GetComponentData<global::Game.Objects.Transform>(entity);
                    for (int i = _recentLocalObjectOperations.Count - 1; i >= 0; i--)
                    {
                        if (!TryGetNewCommittedObjectRoot(
                                _recentLocalObjectOperations[i].Operation, out root) ||
                            !string.Equals(root.PrefabName, prefabName,
                                System.StringComparison.Ordinal) ||
                            unchecked((ushort)root.RandomSeed) != committedSeed)
                            continue;

                        float3 expected = new float3(root.Object.PosX, root.Object.PosY,
                            root.Object.PosZ);
                        float4 expectedRotation = new float4(root.Object.RotX,
                            root.Object.RotY, root.Object.RotZ, root.Object.RotW);
                        matchingIdentity = " identityCandidate[attached=" +
                            HasAttachedCommitIntent(root) + " horizontalDelta=" +
                            math.distance(expected.xz, committedTransform.m_Position.xz)
                                .ToString("0.000") + "m heightDelta=" +
                            math.abs(expected.y - committedTransform.m_Position.y)
                                .ToString("0.000") + "m rotationDot=" +
                            math.abs(math.dot(expectedRotation,
                                committedTransform.m_Rotation.value)).ToString("0.00000") + "]";
                        break;
                    }
                }
            }
            _lastObjectGraphMissDetail = "prefab=" + prefabName + " seed=" + seed +
                " recent=" + _recentLocalObjectOperations.Count + " newest=" + newest +
                matchingIdentity;
            Diagnostics.FlightRecorder.Note("object graph match missed " + _lastObjectGraphMissDetail);
        }

        private bool TryBeginSpecializedAreaCapture(Entity recreate)
        {
            if (!SpecializedAreaOwnerStillMatches(recreate, _cachedLocalObjectOperation))
                return false;
            RememberPlayerPlacedSpawnables(_cachedLocalObjectOperation,
                Mod.Service != null ? Mod.Service.NowMs : 0);
            ForgetRecentLocalObjectOperation(_cachedLocalObjectOperation);
            _pendingSpecializedObjectOperation = _cachedLocalObjectOperation;
            _pendingSpecializedArea = recreate;
            _pendingSpecializedAreaDefinition = null;
            _cachedLocalObjectOperation = null;
            return true;
        }

        private bool SpecializedAreaOwnerStillMatches(Entity area,
            ObjectToolOperationCommand operation)
        {
            if (area == Entity.Null || !EntityManager.Exists(area)) return false;

            Entity topOwner;
            return TryFindTopOwner(area, out topOwner) &&
                   SpecializedObjectMatchesRoot(topOwner, operation);
        }

        private bool SpecializedObjectMatchesRoot(Entity topOwner,
            ObjectToolOperationCommand operation)
        {
            if (operation == null || operation.Definitions == null ||
                operation.RootIndex < 0 || operation.RootIndex >= operation.Definitions.Length ||
                topOwner == Entity.Null || !EntityManager.Exists(topOwner) ||
                !EntityManager.HasComponent<PrefabRef>(topOwner) ||
                !EntityManager.HasComponent<global::Game.Objects.Transform>(topOwner)) return false;

            ObjectToolDefinitionIntent root = operation.Definitions[operation.RootIndex];
            if (root == null || root.Kind != ObjectToolDefinitionKind.Object ||
                root.PrefabIsNull || string.IsNullOrEmpty(root.PrefabName) ||
                root.Original.Kind != PortableEntityKind.None) return false;
            Entity ownerPrefab = EntityManager.GetComponentData<PrefabRef>(topOwner).m_Prefab;
            if (_prefabSystem.GetPrefabName(ownerPrefab) != root.PrefabName) return false;
            global::Game.Objects.Transform ownerTransform =
                EntityManager.GetComponentData<global::Game.Objects.Transform>(topOwner);
            float3 wantedPosition = new float3(root.Object.PosX, root.Object.PosY, root.Object.PosZ);
            if (math.distancesq(ownerTransform.m_Position, wantedPosition) > 4f) return false;

            quaternion wantedRotation = new quaternion(root.Object.RotX, root.Object.RotY,
                root.Object.RotZ, root.Object.RotW);
            return math.abs(math.dot(ownerTransform.m_Rotation.value,
                       wantedRotation.value)) >= 0.98f;
        }

        private bool IsSpecializedAreaPrefab(Entity prefab)
        {
            return prefab != Entity.Null && EntityManager.Exists(prefab) &&
                   (EntityManager.HasComponent<ExtractorAreaData>(prefab) ||
                    EntityManager.HasComponent<StorageAreaData>(prefab));
        }

        private bool IsSpecializedAreaDefinitionForRoot(ObjectToolDefinitionIntent definition,
            ObjectToolDefinitionIntent root)
        {
            if (definition == null || definition.Kind != ObjectToolDefinitionKind.Area ||
                !definition.HasOwnerDefinition ||
                definition.OwnerDefinitionPrefabName != root.PrefabName ||
                string.IsNullOrEmpty(definition.PrefabName)) return false;
            Entity prefab;
            return _prefabIndex.TryResolve(definition.PrefabName, out prefab) &&
                   IsSpecializedAreaPrefab(prefab);
        }

        private void PublishSpecializedAreaOperation()
        {
            ObjectToolOperationCommand source = _pendingSpecializedObjectOperation;
            ObjectToolDefinitionIntent root = source.Definitions[source.RootIndex];
            var definitions = new List<ObjectToolDefinitionIntent>(source.Definitions.Length + 1);
            short rootIndex = -1;
            for (int i = 0; i < source.Definitions.Length; i++)
            {
                ObjectToolDefinitionIntent definition = source.Definitions[i];
                if (IsSpecializedAreaDefinitionForRoot(definition, root)) continue;
                if (i == source.RootIndex) rootIndex = (short)definitions.Count;
                definitions.Add(definition);
            }
            definitions.Add(_pendingSpecializedAreaDefinition);

            if (rootIndex < 0 || definitions.Count > ObjectToolOperationCommand.MaxDefinitions)
            {
                Mod.log.Warn("[MP] BuildSync: specialized object/area operation was incomplete; not sent.");
                if (Mod.Service != null)
                    Mod.Service.RequestAutomaticWorldRecovery(
                        "specialized building capture was incomplete");
                ClearSpecializedAreaCapture();
                return;
            }

            var operation = new ObjectToolOperationCommand
            {
                RootIndex = rootIndex,
                // Keep the compact placement input captured before AreaToolSystem took over.
                // Without it, landfills/extractors fall back to resolving every sender-local owner
                // and road definition after the polygon closes and can disappear on the receiver.
                HasPlacementInput = source.HasPlacementInput,
                ToolRandomSeed = source.ToolRandomSeed,
                PlacementTarget = source.PlacementTarget,
                Definitions = definitions.ToArray(),
            };
            try
            {
                if (TryPublishLocalObjectOperation(operation))
                {
                    Diagnostics.FlightRecorder.Note("specialized object/area operation captured op=" +
                        operation.OperationId + " defs=" + operation.Definitions.Length +
                        " areaNodes=" + _pendingSpecializedAreaDefinition.AreaNodes.Length);
                    PublishOwnedAreaSnapshot(root, _pendingSpecializedAreaDefinition);
                }
            }
            catch (System.Exception ex)
            {
                Mod.log.Warn("[MP] BuildSync: specialized object/area operation was not sent: " +
                             ex.Message);
                Diagnostics.FlightRecorder.Note("specialized object/area capture rejected=" +
                                                  ex.GetType().Name);
                if (Mod.Service != null)
                    Mod.Service.RequestAutomaticWorldRecovery(
                        "specialized building capture failed");
            }
            finally
            {
                ClearSpecializedAreaCapture();
                _cachedLocalObjectOperation = null;
            }
        }

        private void PublishOwnedAreaSnapshot(ObjectToolDefinitionIntent root,
            ObjectToolDefinitionIntent area)
        {
            if (root == null || area == null ||
                !IsClosedAreaNodeRing(area.AreaNodes)) return;
            MultiplayerService service = Mod.Service;
            if (service == null || !service.GameplaySyncReady) return;

            int count = area.AreaNodes.Length - 1;
            var command = new OwnedAreaSnapshotCommand
            {
                AreaPrefabName = area.PrefabName,
                OwnerPrefabName = root.PrefabName,
                OwnerX = root.Object.PosX,
                OwnerY = root.Object.PosY,
                OwnerZ = root.Object.PosZ,
                OwnerRotX = root.Object.RotX,
                OwnerRotY = root.Object.RotY,
                OwnerRotZ = root.Object.RotZ,
                OwnerRotW = root.Object.RotW,
                NodeX = new float[count],
                NodeY = new float[count],
                NodeZ = new float[count],
                NodeElevation = new float[count],
            };
            for (int i = 0; i < count; i++)
            {
                command.NodeX[i] = area.AreaNodes[i].X;
                command.NodeY[i] = area.AreaNodes[i].Y;
                command.NodeZ[i] = area.AreaNodes[i].Z;
                command.NodeElevation[i] = area.AreaNodes[i].Elevation;
            }

            try
            {
                service.Session.SendCommand(0, OwnedAreaSnapshotCommand.Id,
                    command.Encode());
                Diagnostics.FlightRecorder.Note("specialized owned-area safeguard sent nodes=" +
                                                  count);
            }
            catch (System.Exception ex)
            {
                Mod.log.Warn("[MP] BuildSync: owned-area safeguard was not sent: " +
                             ex.Message);
                if (Mod.Service != null)
                    Mod.Service.RequestAutomaticWorldRecovery(
                        "specialized owned-area safeguard failed");
            }
        }

        /// <summary>
        /// True while this lot belongs to a placement whose building has not been published yet.
        /// The periodic owned-area scan must leave such a lot alone: sending the polygon first
        /// gives every receiver an owner-less snapshot it can only wait on and then give up.
        /// </summary>
        internal bool IsSpecializedAreaHeld(Entity area)
        {
            return area != Entity.Null && area == _pendingSpecializedArea &&
                   _pendingSpecializedObjectOperation != null;
        }

        /// <summary>
        /// Publish the held object half when the area tool hands a specialized-industry building
        /// back without a drawn polygon. Cancelling the polygon, or leaving the tool, keeps the
        /// committed building and the lot it was placed with - a complete local change that
        /// used to be discarded with the pending capture, leaving the building on one machine.
        /// The graph is sent exactly as the object tool emitted it, since the abandoned edit
        /// changed nothing the receiver has to reproduce.
        /// </summary>
        private void FinishSpecializedAreaCaptureWithoutPolygon()
        {
            ObjectToolOperationCommand operation = _pendingSpecializedObjectOperation;
            if (operation == null)
            {
                ClearSpecializedAreaCapture();
                return;
            }

            if (!SpecializedPlacementStillCommitted(operation))
            {
                Diagnostics.FlightRecorder.Note(
                    "specialized object/area handoff ended with no committed building");
                ClearSpecializedAreaCapture();
                return;
            }

            try
            {
                if (TryPublishLocalObjectOperation(operation))
                    Diagnostics.FlightRecorder.Note("specialized object without area captured op=" +
                        operation.OperationId + " defs=" + operation.Definitions.Length);
            }
            catch (System.Exception ex)
            {
                Mod.log.Warn("[MP] BuildSync: specialized object without area was not sent: " +
                             ex.Message);
                Diagnostics.FlightRecorder.Note("specialized object without area rejected=" +
                                                  ex.GetType().Name);
                if (Mod.Service != null)
                    Mod.Service.RequestAutomaticWorldRecovery(
                        "specialized building capture failed");
            }
            finally
            {
                ClearSpecializedAreaCapture();
                _cachedLocalObjectOperation = null;
            }
        }

        /// <summary>
        /// True when the building this held graph describes is standing. The held lot is the
        /// cheapest proof; if the abandoned edit took the lot with it, the committed root object
        /// itself still decides. Either way a placement that never committed is not published.
        /// </summary>
        private bool SpecializedPlacementStillCommitted(ObjectToolOperationCommand operation)
        {
            if (SpecializedAreaOwnerStillMatches(_pendingSpecializedArea, operation)) return true;

            ObjectToolDefinitionIntent root;
            Entity rootPrefab;
            if (!TryGetNewCommittedObjectRoot(operation, out root) ||
                !_prefabIndex.TryResolve(root.PrefabName, out rootPrefab)) return false;

            BeginPortableResolve();
            try
            {
                return FindPortableObject(rootPrefab,
                    new float3(root.Object.PosX, root.Object.PosY, root.Object.PosZ),
                    default(PortableEntityRef)) != Entity.Null;
            }
            finally
            {
                EndPortableResolve();
            }
        }

        private void ClearSpecializedAreaCapture()
        {
            _pendingSpecializedObjectOperation = null;
            _pendingSpecializedAreaDefinition = null;
            _pendingSpecializedArea = Entity.Null;
            _completeSpecializedAreaThisFrame = false;
        }

        /// <summary>
        /// Publish only after the area apply has reached live entities. DefinitionGateSystem can
        /// discard local definitions while a remote transaction owns the apply slot; checking the
        /// live polygon here prevents broadcasting an edit that was not committed on this machine.
        /// </summary>
        private void CaptureCompletedSpecializedArea()
        {
            if (!_completeSpecializedAreaThisFrame) return;
            _completeSpecializedAreaThisFrame = false;
            ObjectToolDefinitionIntent completed;
            if (!TryCaptureCompletedSpecializedArea(out completed))
            {
                Diagnostics.FlightRecorder.Note("specialized object/area apply not observed");
                FinishSpecializedAreaCaptureWithoutPolygon();
                return;
            }
            _pendingSpecializedAreaDefinition = completed;
            PublishSpecializedAreaOperation();
        }

        private bool TryCaptureCompletedSpecializedArea(
            out ObjectToolDefinitionIntent completed)
        {
            completed = null;
            ObjectToolOperationCommand operation = _pendingSpecializedObjectOperation;
            if (operation == null || operation.Definitions == null ||
                operation.RootIndex < 0 || operation.RootIndex >= operation.Definitions.Length)
                return false;

            Entity area = _pendingSpecializedArea;
            if (area == Entity.Null || !EntityManager.Exists(area) ||
                !EntityManager.HasComponent<global::Game.Areas.Area>(area) ||
                !EntityManager.HasComponent<PrefabRef>(area) ||
                !EntityManager.HasBuffer<global::Game.Areas.Node>(area)) return false;

            Entity topOwner;
            if (!TryFindTopOwner(area, out topOwner) ||
                !SpecializedObjectMatchesRoot(topOwner, operation)) return false;

            global::Game.Areas.Area areaData =
                EntityManager.GetComponentData<global::Game.Areas.Area>(area);
            if ((areaData.m_Flags & global::Game.Areas.AreaFlags.Complete) == 0) return false;

            Entity areaPrefab = EntityManager.GetComponentData<PrefabRef>(area).m_Prefab;
            Entity ownerPrefab = EntityManager.GetComponentData<PrefabRef>(topOwner).m_Prefab;
            if (!IsSpecializedAreaPrefab(areaPrefab) ||
                !PrefabDeclaresOwnedArea(ownerPrefab, areaPrefab)) return false;
            string areaPrefabName = _prefabSystem.GetPrefabName(areaPrefab);
            if (string.IsNullOrEmpty(areaPrefabName)) return false;

            DynamicBuffer<global::Game.Areas.Node> liveNodes =
                EntityManager.GetBuffer<global::Game.Areas.Node>(area, isReadOnly: true);
            int liveCount = liveNodes.Length;
            if (liveCount >= 4 &&
                liveNodes[0].m_Position.Equals(liveNodes[liveCount - 1].m_Position))
                liveCount--;
            if (liveCount < 3 ||
                liveCount >= ObjectToolOperationCommand.MaxAreaNodesPerDefinition) return false;

            // A live complete area stores only its polygon vertices. GenerateAreasSystem expects a
            // repeated first vertex in a new definition to recognize and commit a closed polygon.
            var wireNodes = new ObjectAreaNodeIntent[liveCount + 1];
            for (int i = 0; i < liveCount; i++)
            {
                global::Game.Areas.Node node = liveNodes[i];
                wireNodes[i] = new ObjectAreaNodeIntent
                {
                    X = node.m_Position.x,
                    Y = node.m_Position.y,
                    Z = node.m_Position.z,
                    Elevation = node.m_Elevation,
                };
            }
            wireNodes[liveCount] = wireNodes[0];

            ObjectToolDefinitionIntent root = operation.Definitions[operation.RootIndex];
            completed = new ObjectToolDefinitionIntent
            {
                Kind = ObjectToolDefinitionKind.Area,
                PrefabName = areaPrefabName,
                CreationFlags = 0,
                RandomSeed = EntityManager.HasComponent<PseudoRandomSeed>(area)
                    ? EntityManager.GetComponentData<PseudoRandomSeed>(area).m_Seed
                    : 0,
                HasOwnerDefinition = true,
                OwnerDefinitionPrefabName = root.PrefabName,
                OwnerDefinitionX = root.Object.PosX,
                OwnerDefinitionY = root.Object.PosY,
                OwnerDefinitionZ = root.Object.PosZ,
                OwnerDefinitionRotX = root.Object.RotX,
                OwnerDefinitionRotY = root.Object.RotY,
                OwnerDefinitionRotZ = root.Object.RotZ,
                OwnerDefinitionRotW = root.Object.RotW,
                AreaNodes = wireNodes,
            };
            return true;
        }

        private void RememberObjectToolControlPoint(ObjectToolSystem tool)
        {
            Unity.Jobs.JobHandle dependencies;
            NativeList<ControlPoint> points = tool.GetControlPoints(out dependencies);
            dependencies.Complete();
            if (!points.IsCreated || points.Length == 0)
            {
                _hasLastObjectToolControlPoint = false;
                return;
            }

            // Relocation is a single-point mode and CreateDefinitions consumes index zero.
            _lastObjectToolControlPoint = points[0];
            _hasLastObjectToolControlPoint = true;
        }

        private void RememberPlacementControlPoint(ObjectToolSystem tool)
        {
            Unity.Jobs.JobHandle dependencies;
            NativeList<ControlPoint> points = tool.GetControlPoints(out dependencies);
            dependencies.Complete();
            if (!points.IsCreated || points.Length == 0)
            {
                _hasLastPlacementControlPoint = false;
                return;
            }

            // CreateDefinitions consumes index zero for an ordinary single-object placement.
            _lastPlacementControlPoint = points[0];
            _hasLastPlacementControlPoint = true;
        }

        /// <summary>
        /// Preserve the semantic input that a finished building graph cannot safely express: which
        /// local road/node the placement snapped to. This applies to ordinary service buildings and
        /// specialized-industry roots alike. The latter publish later, after their area polygon
        /// closes, but must retain the original point and seed through that hand-off.
        /// </summary>
        private void AttachPlacementInput(ObjectToolOperationCommand operation)
        {
            if (!_hasLastPlacementControlPoint || operation == null || operation.IsAssetStamp ||
                operation.Definitions == null || operation.RootIndex < 0 ||
                operation.RootIndex >= operation.Definitions.Length ||
                !CanDeriveNativeTransactions) return;

            ObjectToolDefinitionIntent root = operation.Definitions[operation.RootIndex];
            if (root == null || root.Kind != ObjectToolDefinitionKind.Object || root.PrefabIsNull ||
                string.IsNullOrEmpty(root.PrefabName) ||
                root.Original.Kind != PortableEntityKind.None ||
                root.Owner.Kind != PortableEntityKind.None || root.HasOwnerDefinition) return;

            CreationFlags flags = (CreationFlags)root.CreationFlags;
            if ((flags & (CreationFlags.Delete | CreationFlags.Relocate |
                          CreationFlags.Recreate | CreationFlags.Upgrade |
                          CreationFlags.Permanent)) != 0) return;

            ControlPoint point = _lastPlacementControlPoint;
            float3 rootPosition = new float3(root.Object.PosX, root.Object.PosY, root.Object.PosZ);
            float4 rootRotation = math.normalizesafe(new float4(root.Object.RotX,
                    root.Object.RotY, root.Object.RotZ, root.Object.RotW),
                new float4(0f, 0f, 0f, 1f));
            float4 pointRotation = math.normalizesafe(point.m_Rotation.value,
                new float4(0f, 0f, 0f, 1f));
            // A standing definition may survive while the cursor moves to a new preview. Never
            // pair that old graph with the new point merely because both use ObjectTool.Create.
            if (math.distancesq(rootPosition, point.m_Position) > 0.25f ||
                math.abs(math.dot(rootRotation, pointRotation)) < 0.995f) return;

            PortableEntityRef target;
            if (!TryCapturePortableRef(point.m_OriginalEntity, out target))
            {
                if (PlacementSnapTargetReachesGenerator(point.m_OriginalEntity))
                {
                    Diagnostics.FlightRecorder.Note(
                        "building placement snap target was not portable");
                    return;
                }
                // The target only shaped the position, which the root definition already carries.
                target = default(PortableEntityRef);
            }

            operation.HasPlacementInput = true;
            operation.ToolRandomSeed = AppliedLifecycleToolSeed;
            operation.PlacementTarget = target;
            Diagnostics.FlightRecorder.Note("building placement inputs captured prefab=" +
                                              root.PrefabName + " target=" + target.Kind);
        }

        /// <summary>
        /// True when the definition generator would actually read the entity a placement snapped to.
        /// It does so only for a parent it can hang the object under - one carrying
        /// <see cref="global::Game.Objects.SubObject"/> (a road, a node, a building), a placeholder
        /// the object fills, or an attached object a net-object placement replaces.
        ///
        /// A building placed along a zoned street snaps to that street's <c>Zones.Block</c>, which is
        /// none of those: the block shaped the position, and the position is already in the root
        /// definition. Refusing the compact input for it is what pushed every service building onto
        /// the exact-graph path, whose hundred-plus sender-local references then have to resolve
        /// one-for-one on the receiver.
        /// </summary>
        private bool PlacementSnapTargetReachesGenerator(Entity snapTarget)
        {
            return snapTarget != Entity.Null && EntityManager.Exists(snapTarget) &&
                   (EntityManager.HasBuffer<global::Game.Objects.SubObject>(snapTarget) ||
                    EntityManager.HasComponent<PlaceholderBuildingData>(snapTarget) ||
                    EntityManager.HasComponent<global::Game.Objects.Attached>(snapTarget));
        }

        private void RememberStampControlPoint(ObjectToolSystem tool)
        {
            Unity.Jobs.JobHandle dependencies;
            NativeList<ControlPoint> points = tool.GetControlPoints(out dependencies);
            dependencies.Complete();
            if (!points.IsCreated || points.Length == 0)
            {
                _hasLastStampControlPoint = false;
                return;
            }

            // Stamping is a single-point mode; the generator consumes index zero.
            _lastStampControlPoint = points[0];
            _hasLastStampControlPoint = true;
        }

        /// <summary>
        /// Publish a stamp as the inputs its tool had, so the peer runs the game's own generator
        /// over them instead of rebuilding the transaction from transmitted definitions.
        ///
        /// The stamp's internal junctions exist only because every course sharing a prefab node
        /// index receives the identical computed endpoint position - the node generator merges on
        /// an exact float comparison, with no tolerance. Regenerating on the peer recreates that
        /// identity; replaying finished definitions depends on all of it surviving the round trip,
        /// and a single endpoint that does not becomes a ramp connected to nothing.
        /// </summary>
        private bool TryPublishLocalAssetStamp(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName) || !_hasLastStampControlPoint ||
                !CanDeriveNativeTransactions) return false;
            MultiplayerService service = Mod.Service;
            if (service == null || !service.GameplaySyncReady) return false;

            ControlPoint point = _lastStampControlPoint;
            var command = new AssetStampCommand
            {
                OperationId = _nextLocalObjectOperationId++,
                PrefabName = prefabName,
                PosX = point.m_Position.x,
                PosY = point.m_Position.y,
                PosZ = point.m_Position.z,
                RotX = point.m_Rotation.value.x,
                RotY = point.m_Rotation.value.y,
                RotZ = point.m_Rotation.value.z,
                RotW = point.m_Rotation.value.w,
                Elevation = point.m_Elevation,
                ToolRandomSeed = AppliedLifecycleToolSeed,
            };

            try
            {
                service.Session.SendCommand(0, AssetStampCommand.Id, command.Encode());
            }
            catch (System.Exception ex)
            {
                // Fall back to the definition batch rather than losing the placement entirely.
                Mod.log.Warn("[MP] BuildSync: asset-stamp inputs were not sent: " + ex.Message);
                Diagnostics.FlightRecorder.Note("asset stamp inputs rejected=" + ex.GetType().Name);
                return false;
            }

            _nativeLifecycleCapturedThisFrame = true;
            Diagnostics.FlightRecorder.Note("asset stamp inputs published op=" +
                command.OperationId + " prefab=" + prefabName +
                " seed=" + command.ToolRandomSeed);
            return true;
        }

        private bool TryTakeRelocationControlPoint(float3 position, quaternion rotation,
            out ControlPoint controlPoint)
        {
            controlPoint = _lastObjectToolControlPoint;
            if (!_hasLastObjectToolControlPoint) return false;
            _hasLastObjectToolControlPoint = false;

            // Prevent a stale point from a previously selected object tool being attached to a
            // later relocation. Quaternion sign is immaterial, hence the absolute dot product.
            if (math.distancesq(controlPoint.m_Position, position) > 0.25f) return false;
            return math.abs(math.dot(controlPoint.m_Rotation.value, rotation.value)) >= 0.98f;
        }

        /// <summary>
        /// Publish a relocation from the definition graph the tool is applying.
        ///
        /// A tool records definitions through <c>ToolOutputBarrier</c>, which plays back at the end of
        /// ToolUpdate, and drops their one-frame <see cref="Updated"/> tag at Cleanup. So in the window
        /// before <see cref="ToolOutputSystem"/> the un-tagged definitions still standing are exactly
        /// the ones the Temps now committing were generated from - the tools' own definition query.
        /// The root <see cref="CreationFlags.Relocate"/> definition names the moved entity in
        /// <c>m_Original</c> and its destination in <see cref="ObjectDefinition"/>. The snapped
        /// control point sampled while the preview stood supplies the destination road/node; the
        /// receiver re-derives the owned graph from that compact input set.
        /// </summary>
        private void CaptureLocalRelocationForApply(NativeArray<Entity> definitions)
        {
            MoveSyncSystem moveSync = World.GetExistingSystemManaged<MoveSyncSystem>();
            if (moveSync == null) return;

            for (int i = 0; i < definitions.Length; i++)
            {
                Entity entity = definitions[i];
                if (!EntityManager.Exists(entity) ||
                    !EntityManager.HasComponent<CreationDefinition>(entity) ||
                    !EntityManager.HasComponent<ObjectDefinition>(entity)) continue;

                CreationDefinition creation =
                    EntityManager.GetComponentData<CreationDefinition>(entity);
                if ((creation.m_Flags & CreationFlags.Relocate) == 0) continue;
                // Owned elements carried along by a relocation - the moved building's own installed
                // upgrades - are emitted without a prefab, taking it from the entity they name. Only
                // the definition the tool was given carries the selected prefab, so that test finds
                // the root whether or not this relocation has a host: an upgrade relocated from the
                // building's upgrade list is itself owned, and its root definition therefore carries
                // an OwnerDefinition naming that host.
                if (creation.m_Owner != Entity.Null || creation.m_Prefab == Entity.Null) continue;

                Entity original = creation.m_Original;
                if (original == Entity.Null || !EntityManager.Exists(original) ||
                    !EntityManager.HasComponent<global::Game.Objects.Transform>(original) ||
                    !EntityManager.HasComponent<PrefabRef>(original)) continue;

                ObjectDefinition placement =
                    EntityManager.GetComponentData<ObjectDefinition>(entity);
                ControlPoint appliedPoint;
                if (!TryTakeRelocationControlPoint(placement.m_Position, placement.m_Rotation,
                        out appliedPoint))
                {
                    // The final-entity detector remains available later in the frame. Do not send a
                    // compact move without knowing whether the tool snapped it to a road.
                    Diagnostics.FlightRecorder.Note("relocation control point unavailable; final-entity fallback");
                    return;
                }

                Entity destinationParent = NetAttachment.NormalizeNetParent(
                    EntityManager, appliedPoint.m_OriginalEntity);
                moveSync.PublishLocalRelocation(
                    EntityManager.GetComponentData<PrefabRef>(original).m_Prefab,
                    original,
                    EntityManager.GetComponentData<global::Game.Objects.Transform>(original)
                        .m_Position,
                    placement.m_Position, placement.m_Rotation, placement.m_Elevation,
                    AppliedLifecycleToolSeed, destinationParent,
                    destinationAttachmentKnown: true);
                return;
            }
        }

        /// <summary>
        /// Capture an object lifecycle action once, in the narrow phase between its tool selecting
        /// Apply and ToolOutputSystem consuming the standing preview. Hover/movement frames never
        /// encode the graph or build portable world indexes.
        /// </summary>
        public void CaptureLocalObjectApplyBeforeToolOutput()
        {
            if (_nativeLifecycleCapturedThisFrame || _toolSystem == null ||
                _toolSystem.applyMode != ApplyMode.Apply) return;
            if (_standingDefinitions.IsEmptyIgnoreFilter) return;

            NativeArray<Entity> definitions = _standingDefinitions.ToEntityArray(Allocator.Temp);
            try
            {
                bool fromObjectLifecycleTool = _localObjectToolRanThisFrame;
                // The structural root is authoritative. A one-shot network prefab can switch back
                // to another tool during the same Apply frame, so active-tool identity is no longer
                // reliable at this last pre-output hook.
                bool fromNetOwnedObjectGraph = !fromObjectLifecycleTool &&
                    NativeObjectGraph.HasNewTopLevelObjectRoot(EntityManager, definitions);
                if (!fromObjectLifecycleTool && !fromNetOwnedObjectGraph) return;

                // A remote net transaction owns this frame's ApplyTool pass. Its isolation
                // deliberately prevents the local preview from committing, so it must not be
                // published as local work.
                if (_nativeNetCoordinator != null && _nativeNetCoordinator.HasArmedToolCommit)
                    return;

                // Read a relocation from the same one-shot snapshot. The committed entity is not a
                // reliable signal because the apply pass does not retain its old position.
                if (fromObjectLifecycleTool)
                    CaptureLocalRelocationForApply(definitions);

                CaptureObjectToolOperation(definitions);
                ObjectToolOperationCommand operation = _cachedLocalObjectOperation;
                if (operation == null || operation.Definitions == null) return;

                // Register the exact spawnable definition before ToolOutput applies it. The
                // specialized-industry object half becomes Created immediately, while its native
                // command is intentionally held until the area-tool polygon is finished.
                RememberPlayerPlacedSpawnables(operation,
                    Mod.Service != null ? Mod.Service.NowMs : 0);

                if (operation.IsAssetStamp)
                {
                    string selectedStamp = GetSelectedAssetStampPrefabName(_toolSystem.activeTool) ??
                                           _selectedAssetStampPrefabName;
                    if (!string.Equals(selectedStamp, operation.AssetStampPrefabName,
                            System.StringComparison.Ordinal)) return;

                    // Preferred path: ship the tool's inputs. The definition batch below stays as
                    // the fallback for a game build whose generator we cannot reach.
                    if (TryPublishLocalAssetStamp(operation.AssetStampPrefabName))
                    {
                        _localObjectApplyThisFrame = true;
                        _localLifecycleApplyThisFrame = true;
                        _cachedLocalObjectOperation = null;
                        return;
                    }
                }

                _localObjectApplyThisFrame = true;
                _localLifecycleApplyThisFrame = true;
                Diagnostics.FlightRecorder.Note((operation.IsAssetStamp
                    ? "asset stamp"
                    : "object lifecycle") + " apply captured from standing definitions=" +
                                                  operation.Definitions.Length);
                PublishCachedLocalObjectOperation();
            }
            finally
            {
                definitions.Dispose();
            }
        }

        /// <summary>
        /// Reset the one-frame native-capture marker at the front of ToolUpdate. Actual capture is
        /// deferred to <see cref="CaptureLocalObjectApplyBeforeToolOutput"/>, where the applying
        /// standing definitions are still available and the work happens only once per click.
        /// </summary>
        public void CaptureLocalObjectApply()
        {
            _nativeLifecycleCapturedThisFrame = false;
        }

        private void PublishCachedLocalObjectOperation()
        {
            if (_cachedLocalObjectOperation == null) return;

            // A new top-level object has a stronger commit signal than ApplyMode: its generated
            // root preserves the preview definition's prefab, transform, and random seed. Keep the
            // graph in the bounded recent set and publish it only after that root exists. This also
            // prevents the replacement ghost generated after a click from becoming a placement.
            ObjectToolDefinitionIntent newRoot;
            if (TryGetNewCommittedObjectRoot(_cachedLocalObjectOperation, out newRoot)) return;

            try
            {
                if (TryPublishLocalObjectOperation(_cachedLocalObjectOperation))
                    Diagnostics.FlightRecorder.Note("object operation captured op=" +
                        _cachedLocalObjectOperation.OperationId + " defs=" +
                        _cachedLocalObjectOperation.Definitions.Length);
            }
            catch (System.Exception ex)
            {
                Mod.log.Warn("[MP] BuildSync: native object operation was not sent: " + ex.Message);
                Diagnostics.FlightRecorder.Note("object operation capture rejected=" +
                                                  ex.GetType().Name);
                if (Mod.Service != null)
                    Mod.Service.RequestAutomaticWorldRecovery(
                        "native object operation could not be sent");
            }
            finally
            {
                _cachedLocalObjectOperation = null;
            }
        }

        private bool TryPublishLocalObjectOperation(ObjectToolOperationCommand operation)
        {
            MultiplayerService service = Mod.Service;
            if (service == null || !service.GameplaySyncReady) return false;
            RememberPlayerPlacedSpawnables(operation, service.NowMs);
            operation.OperationId = _nextLocalObjectOperationId++;
            byte[] body = operation.Encode();
            service.Session.SendCommand(0, ObjectToolOperationCommand.Id, body);
            ForgetRecentLocalObjectOperation(operation);
            _nativeLifecycleCapturedThisFrame = true;
            return true;
        }

        /// <summary>
        /// Consume the one-shot identity of a spawnable building produced by an explicitly applied
        /// object-tool graph. A fixed root requires the same prefab and 16-bit variant seed,
        /// position within 10 cm, and the captured orientation; attached visible buildings use a
        /// bounded snap envelope because attachment resolution changes their definition transform.
        /// The live specialized owner/attachment graph is also accepted as a durable fallback.
        /// </summary>
        internal bool ConsumePlayerPlacedSpawnable(Entity entity, long now)
        {
            if (entity == Entity.Null || !EntityManager.Exists(entity) ||
                !EntityManager.HasComponent<PrefabRef>(entity) ||
                !EntityManager.HasComponent<global::Game.Objects.Transform>(entity)) return false;

            Entity prefab = EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab;
            if (prefab == Entity.Null || !EntityManager.Exists(prefab) ||
                !EntityManager.HasComponent<SpawnableBuildingData>(prefab)) return false;

            PrunePlayerPlacedSpawnables(now);
            global::Game.Objects.Transform transform =
                EntityManager.GetComponentData<global::Game.Objects.Transform>(entity);
            float4 rotation = math.normalizesafe(transform.m_Rotation.value,
                new float4(0f, 0f, 0f, 1f));
            bool hasSeed = EntityManager.HasComponent<PseudoRandomSeed>(entity);
            ushort seed = hasSeed
                ? EntityManager.GetComponentData<PseudoRandomSeed>(entity).m_Seed
                : (ushort)0;

            for (int i = _playerPlacedSpawnableCreations.Count - 1; i >= 0; i--)
            {
                PlayerPlacedSpawnableCreation candidate =
                    _playerPlacedSpawnableCreations[i];
                if (candidate.Prefab != prefab ||
                    !hasSeed || candidate.RandomSeed != seed) continue;

                // Attachment resolution can snap and rotate the committed visible building away
                // from its prefab-local definition. Seed + prefab remain exact; use the same
                // bounded transform envelope as committed-root correlation for an attached live
                // instance, while ordinary roots retain the strict 10 cm/orientation match.
                bool attached = EntityManager.HasComponent<global::Game.Objects.Attached>(entity);
                bool transformMatches = attached
                    ? math.distancesq(candidate.Position.xz, transform.m_Position.xz) <=
                          AttachedPlayerPlacedSpawnableMatchRadiusSq &&
                      math.abs(candidate.Position.y - transform.m_Position.y) <=
                          AttachedPlayerPlacedSpawnableMatchHeight
                    : math.distancesq(candidate.Position, transform.m_Position) <=
                          PlayerPlacedSpawnableMatchDistanceSq &&
                      math.abs(math.dot(candidate.Rotation, rotation)) >=
                          PlayerPlacedSpawnableMatchRotationDot;
                if (!transformMatches) continue;

                _playerPlacedSpawnableCreations.RemoveAt(i);
                Diagnostics.FlightRecorder.Note("player-placed spawnable guard consumed");
                return true;
            }

            return IsLiveSpecializedIndustrySpawnable(entity, prefab);
        }

        private void RememberPlayerPlacedSpawnables(ObjectToolOperationCommand operation, long now)
        {
            if (!IsSpecializedIndustryPlacement(operation)) return;
            PrunePlayerPlacedSpawnables(now);

            int remembered = 0;
            for (int i = 0; i < operation.Definitions.Length; i++)
            {
                ObjectToolDefinitionIntent definition = operation.Definitions[i];
                Entity prefab;
                if (definition == null || definition.Kind != ObjectToolDefinitionKind.Object ||
                    definition.PrefabIsNull ||
                    !_prefabIndex.TryResolve(definition.PrefabName, out prefab) ||
                    !IsAllowedSpecializedSpawnable(operation, i, prefab)) continue;

                var candidate = new PlayerPlacedSpawnableCreation
                {
                    Prefab = prefab,
                    Position = new float3(definition.Object.PosX, definition.Object.PosY,
                        definition.Object.PosZ),
                    Rotation = math.normalizesafe(new float4(definition.Object.RotX,
                            definition.Object.RotY, definition.Object.RotZ,
                            definition.Object.RotW),
                        new float4(0f, 0f, 0f, 1f)),
                    RandomSeed = unchecked((ushort)definition.RandomSeed),
                    ExpiryMs = now > 0 ? now + PlayerPlacedSpawnableLifetimeMs : long.MaxValue,
                };

                bool duplicate = false;
                for (int j = _playerPlacedSpawnableCreations.Count - 1; j >= 0; j--)
                {
                    PlayerPlacedSpawnableCreation existing =
                        _playerPlacedSpawnableCreations[j];
                    if (existing.Prefab != candidate.Prefab ||
                        existing.RandomSeed != candidate.RandomSeed ||
                        math.distancesq(existing.Position, candidate.Position) >
                        PlayerPlacedSpawnableMatchDistanceSq ||
                        math.abs(math.dot(existing.Rotation, candidate.Rotation)) <
                        PlayerPlacedSpawnableMatchRotationDot) continue;
                    _playerPlacedSpawnableCreations[j] = candidate;
                    duplicate = true;
                    break;
                }
                if (duplicate) continue;

                if (_playerPlacedSpawnableCreations.Count >=
                    MaxPlayerPlacedSpawnableCreations)
                    _playerPlacedSpawnableCreations.RemoveAt(0);
                _playerPlacedSpawnableCreations.Add(candidate);
                remembered++;
            }

            if (remembered > 0)
                Diagnostics.FlightRecorder.Note("player-placed spawnable guard armed=" +
                                                  remembered);
        }

        private void PrunePlayerPlacedSpawnables(long now)
        {
            if (now <= 0) return;
            for (int i = _playerPlacedSpawnableCreations.Count - 1; i >= 0; i--)
                if (_playerPlacedSpawnableCreations[i].ExpiryMs <= now)
                    _playerPlacedSpawnableCreations.RemoveAt(i);
        }

        private void ClearPlayerPlacedSpawnables()
        {
            _playerPlacedSpawnableCreations.Clear();
        }

        private bool TryCaptureObjectToolDefinition(Entity entity,
            out ObjectToolDefinitionIntent result)
        {
            result = null;
            CreationDefinition creation = EntityManager.GetComponentData<CreationDefinition>(entity);
            bool isObject = EntityManager.HasComponent<ObjectDefinition>(entity);
            bool isNet = EntityManager.HasComponent<NetCourse>(entity);
            bool isArea = EntityManager.HasBuffer<global::Game.Areas.Node>(entity);
            int shapeCount = (isObject ? 1 : 0) + (isNet ? 1 : 0) + (isArea ? 1 : 0);
            if (shapeCount != 1) return false;

            var value = new ObjectToolDefinitionIntent
            {
                Kind = isObject ? ObjectToolDefinitionKind.Object :
                    isNet ? ObjectToolDefinitionKind.NetCourse : ObjectToolDefinitionKind.Area,
                PrefabIsNull = creation.m_Prefab == Entity.Null,
                // Permanent is an execution-policy bit for definitions that are consumed on this
                // machine without ToolOutputSystem's transaction. It is not object intent. Sending
                // it made the receiver refuse the whole native batch because remote work must pass
                // through the isolated Temp/apply/drain lifecycle.
                CreationFlags = (uint)(creation.m_Flags & ~CreationFlags.Permanent),
                RandomSeed = creation.m_RandomSeed,
            };
            if (creation.m_Prefab != Entity.Null &&
                !TryPrefabName(creation.m_Prefab, out value.PrefabName)) return false;
            if (creation.m_SubPrefab != Entity.Null &&
                !TryPrefabName(creation.m_SubPrefab, out value.SubPrefabName)) return false;
            if (!TryCapturePortableRef(creation.m_Original, out value.Original) ||
                !TryCapturePortableRef(creation.m_Owner, out value.Owner) ||
                !TryCaptureAttachment(creation.m_Attached, creation.m_Prefab,
                    creation.m_Flags, out value.Attached,
                    out value.AttachedPrefabName)) return false;

            if (EntityManager.HasComponent<OwnerDefinition>(entity))
            {
                OwnerDefinition owner = EntityManager.GetComponentData<OwnerDefinition>(entity);
                if (owner.m_Prefab == Entity.Null ||
                    !TryPrefabName(owner.m_Prefab, out value.OwnerDefinitionPrefabName)) return false;
                value.HasOwnerDefinition = true;
                value.OwnerDefinitionX = owner.m_Position.x;
                value.OwnerDefinitionY = owner.m_Position.y;
                value.OwnerDefinitionZ = owner.m_Position.z;
                value.OwnerDefinitionRotX = owner.m_Rotation.value.x;
                value.OwnerDefinitionRotY = owner.m_Rotation.value.y;
                value.OwnerDefinitionRotZ = owner.m_Rotation.value.z;
                value.OwnerDefinitionRotW = owner.m_Rotation.value.w;
            }

            if (isObject)
            {
                ObjectDefinition data = EntityManager.GetComponentData<ObjectDefinition>(entity);
                value.Object = new ObjectDefinitionIntent
                {
                    PosX = data.m_Position.x, PosY = data.m_Position.y, PosZ = data.m_Position.z,
                    LocalX = data.m_LocalPosition.x, LocalY = data.m_LocalPosition.y,
                    LocalZ = data.m_LocalPosition.z,
                    ScaleX = data.m_Scale.x, ScaleY = data.m_Scale.y, ScaleZ = data.m_Scale.z,
                    RotX = data.m_Rotation.value.x, RotY = data.m_Rotation.value.y,
                    RotZ = data.m_Rotation.value.z, RotW = data.m_Rotation.value.w,
                    LocalRotX = data.m_LocalRotation.value.x,
                    LocalRotY = data.m_LocalRotation.value.y,
                    LocalRotZ = data.m_LocalRotation.value.z,
                    LocalRotW = data.m_LocalRotation.value.w,
                    Elevation = data.m_Elevation,
                    Intensity = data.m_Intensity,
                    Age = data.m_Age,
                    IsDecoration = data.m_IsDecoration,
                    ParentMesh = data.m_ParentMesh,
                    GroupIndex = data.m_GroupIndex,
                    Probability = data.m_Probability,
                    PrefabSubIndex = data.m_PrefabSubIndex,
                };
            }
            else if (isNet)
            {
                NetCourse data = EntityManager.GetComponentData<NetCourse>(entity);
                ObjectCoursePositionIntent start, end;
                if (!TryCaptureCoursePosition(data.m_StartPosition, out start) ||
                    !TryCaptureCoursePosition(data.m_EndPosition, out end)) return false;
                value.NetCourse = new ObjectNetCourseIntent
                {
                    Start = start,
                    End = end,
                    Ax = data.m_Curve.a.x, Ay = data.m_Curve.a.y, Az = data.m_Curve.a.z,
                    Bx = data.m_Curve.b.x, By = data.m_Curve.b.y, Bz = data.m_Curve.b.z,
                    Cx = data.m_Curve.c.x, Cy = data.m_Curve.c.y, Cz = data.m_Curve.c.z,
                    Dx = data.m_Curve.d.x, Dy = data.m_Curve.d.y, Dz = data.m_Curve.d.z,
                    ElevationLeft = data.m_Elevation.x,
                    ElevationRight = data.m_Elevation.y,
                    Length = data.m_Length,
                    FixedIndex = data.m_FixedIndex,
                };
            }
            else
            {
                DynamicBuffer<global::Game.Areas.Node> nodes =
                    EntityManager.GetBuffer<global::Game.Areas.Node>(entity, isReadOnly: true);
                if (nodes.Length == 0 ||
                    nodes.Length > ObjectToolOperationCommand.MaxAreaNodesPerDefinition) return false;
                value.AreaNodes = new ObjectAreaNodeIntent[nodes.Length];
                for (int i = 0; i < nodes.Length; i++)
                {
                    value.AreaNodes[i] = new ObjectAreaNodeIntent
                    {
                        X = nodes[i].m_Position.x,
                        Y = nodes[i].m_Position.y,
                        Z = nodes[i].m_Position.z,
                        Elevation = nodes[i].m_Elevation,
                    };
                }
            }

            if (EntityManager.HasComponent<Upgraded>(entity))
            {
                CompositionFlags flags = EntityManager.GetComponentData<Upgraded>(entity).m_Flags;
                value.HasUpgraded = true;
                value.UpgradeGeneral = (uint)flags.m_General;
                value.UpgradeLeft = (uint)flags.m_Left;
                value.UpgradeRight = (uint)flags.m_Right;
            }

            result = value;
            return true;
        }

        private bool TryCaptureAttachment(Entity attached, Entity objectPrefab,
            CreationFlags flags, out PortableEntityRef portable, out string prefabName)
        {
            portable = new PortableEntityRef { Kind = PortableEntityKind.None };
            prefabName = null;
            if (attached == Entity.Null) return true;

            // Placeholder facilities emit their visible level-one building as a second object
            // definition whose attachment target is the placeholder prefab entity itself. That is
            // a local prefab relationship, not a live-world entity reference.
            if (EntityManager.Exists(attached) &&
                EntityManager.HasComponent<PrefabData>(attached))
            {
                if ((flags & CreationFlags.Attach) == 0 ||
                    objectPrefab == Entity.Null ||
                    !EntityManager.Exists(objectPrefab) ||
                    !EntityManager.HasComponent<SpawnableBuildingData>(objectPrefab) ||
                    !EntityManager.HasComponent<PlaceholderBuildingData>(attached) ||
                    !TryPrefabName(attached, out prefabName))
                    return false;
                return true;
            }

            return TryCapturePortableRef(attached, out portable);
        }

        private bool TryCaptureCoursePosition(CoursePos data,
            out ObjectCoursePositionIntent value)
        {
            value = new ObjectCoursePositionIntent();
            PortableEntityRef target;
            if (!TryCaptureCourseTarget(data.m_Entity, out target)) return false;
            value.Entity = target;
            value.PosX = data.m_Position.x; value.PosY = data.m_Position.y;
            value.PosZ = data.m_Position.z;
            value.RotX = data.m_Rotation.value.x; value.RotY = data.m_Rotation.value.y;
            value.RotZ = data.m_Rotation.value.z; value.RotW = data.m_Rotation.value.w;
            value.ElevationLeft = data.m_Elevation.x;
            value.ElevationRight = data.m_Elevation.y;
            value.CourseDelta = data.m_CourseDelta;
            value.SplitPosition = data.m_SplitPosition;
            value.Flags = (uint)data.m_Flags;
            value.ParentMesh = data.m_ParentMesh;
            return true;
        }

        private bool TryCaptureCourseTarget(Entity entity, out PortableEntityRef value)
        {
            return TryCapturePortableRef(entity, out value);
        }

        private bool TryGetStablePortableEntity(Entity entity, out Entity stable)
        {
            stable = entity;
            // Standing definitions can reference the previous preview's Temp graph. Follow its
            // live original; a preview-only target is represented as None and regenerated from the
            // transmitted definition on the receiver.
            const int maxTempDepth = 16;
            for (int depth = 0; stable != Entity.Null && depth < maxTempDepth; depth++)
            {
                if (!EntityManager.Exists(stable)) return false;
                if (!EntityManager.HasComponent<Temp>(stable))
                {
                    return !EntityManager.HasComponent<Deleted>(stable);
                }

                Entity original = EntityManager.GetComponentData<Temp>(stable).m_Original;
                if (original == stable) return false;
                stable = original;
            }

            return stable == Entity.Null;
        }

        private bool TryCapturePortableRef(Entity entity, out PortableEntityRef value)
        {
            value = new PortableEntityRef { Kind = PortableEntityKind.None };
            if (!TryGetStablePortableEntity(entity, out entity)) return false;
            if (entity == Entity.Null) return true;
            if (!EntityManager.Exists(entity) || !EntityManager.HasComponent<PrefabRef>(entity))
                return false;

            Entity prefab = EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab;
            if (!TryPrefabName(prefab, out value.PrefabName)) return false;
            value.RotW = 1f;

            if (EntityManager.HasComponent<global::Game.Net.Edge>(entity) &&
                EntityManager.HasComponent<global::Game.Net.Curve>(entity))
            {
                value.Kind = PortableEntityKind.NetEdge;
                Bezier4x3 curve = EntityManager.GetComponentData<global::Game.Net.Curve>(entity).m_Bezier;
                value.Ax = curve.a.x; value.Ay = curve.a.y; value.Az = curve.a.z;
                value.Bx = curve.b.x; value.By = curve.b.y; value.Bz = curve.b.z;
                value.Cx = curve.c.x; value.Cy = curve.c.y; value.Cz = curve.c.z;
                value.Dx = curve.d.x; value.Dy = curve.d.y; value.Dz = curve.d.z;
                float3 midpoint = MathUtils.Position(curve, 0.5f);
                value.PosX = midpoint.x; value.PosY = midpoint.y; value.PosZ = midpoint.z;
            }
            else if (EntityManager.HasComponent<global::Game.Net.Node>(entity))
            {
                value.Kind = PortableEntityKind.NetNode;
                float3 position = EntityManager.GetComponentData<global::Game.Net.Node>(entity).m_Position;
                value.PosX = position.x; value.PosY = position.y; value.PosZ = position.z;
            }
            else if (EntityManager.HasComponent<global::Game.Areas.Area>(entity) &&
                     EntityManager.HasBuffer<global::Game.Areas.Node>(entity))
            {
                value.Kind = PortableEntityKind.Area;
                DynamicBuffer<global::Game.Areas.Node> nodes =
                    EntityManager.GetBuffer<global::Game.Areas.Node>(entity, isReadOnly: true);
                if (nodes.Length == 0) return false;
                value.PosX = nodes[0].m_Position.x;
                value.PosY = nodes[0].m_Position.y;
                value.PosZ = nodes[0].m_Position.z;
            }
            else if (EntityManager.HasComponent<global::Game.Objects.Transform>(entity))
            {
                value.Kind = PortableEntityKind.Object;
                global::Game.Objects.Transform transform =
                    EntityManager.GetComponentData<global::Game.Objects.Transform>(entity);
                value.PosX = transform.m_Position.x; value.PosY = transform.m_Position.y;
                value.PosZ = transform.m_Position.z;
                value.RotX = transform.m_Rotation.value.x; value.RotY = transform.m_Rotation.value.y;
                value.RotZ = transform.m_Rotation.value.z; value.RotW = transform.m_Rotation.value.w;
            }
            else return false;

            if (EntityManager.HasComponent<NetData>(prefab))
            {
                NetData netData = EntityManager.GetComponentData<NetData>(prefab);
                value.RequiredLayers = (uint)netData.m_RequiredLayers;
                value.ConnectLayers = (uint)netData.m_ConnectLayers;
            }

            Entity topOwner;
            if (!TryFindTopOwner(entity, out topOwner) || topOwner == Entity.Null) return true;
            if (!EntityManager.HasComponent<PrefabRef>(topOwner) ||
                !EntityManager.HasComponent<global::Game.Objects.Transform>(topOwner)) return false;
            Entity ownerPrefab = EntityManager.GetComponentData<PrefabRef>(topOwner).m_Prefab;
            if (!TryPrefabName(ownerPrefab, out value.OwnerPrefabName)) return false;
            global::Game.Objects.Transform ownerTransform =
                EntityManager.GetComponentData<global::Game.Objects.Transform>(topOwner);
            value.OwnerX = ownerTransform.m_Position.x;
            value.OwnerY = ownerTransform.m_Position.y;
            value.OwnerZ = ownerTransform.m_Position.z;
            value.OwnerRotX = ownerTransform.m_Rotation.value.x;
            value.OwnerRotY = ownerTransform.m_Rotation.value.y;
            value.OwnerRotZ = ownerTransform.m_Rotation.value.z;
            value.OwnerRotW = ownerTransform.m_Rotation.value.w;
            PortableOwnerPathStep[] ownerPath;
            if (TryCaptureOwnerPath(entity, topOwner, out ownerPath))
                value.OwnerPath = ownerPath;
            return true;
        }

        private bool TryCaptureOwnerPath(Entity entity, Entity topOwner,
            out PortableOwnerPathStep[] result)
        {
            result = null;
            if (entity == Entity.Null || topOwner == Entity.Null || entity == topOwner)
                return false;

            var reversed = new List<PortableOwnerPathStep>();
            Entity cursor = entity;
            while (cursor != topOwner)
            {
                if (reversed.Count >= ObjectToolOperationCommand.MaxOwnerPathDepth ||
                    !EntityManager.HasComponent<Owner>(cursor)) return false;
                Entity owner = EntityManager.GetComponentData<Owner>(cursor).m_Owner;
                if (owner == Entity.Null || owner == cursor || !EntityManager.Exists(owner))
                    return false;
                PortableOwnerPathStep step;
                if (!TryCaptureOwnerPathStep(owner, cursor, out step)) return false;
                reversed.Add(step);
                cursor = owner;
            }

            reversed.Reverse();
            result = reversed.ToArray();
            return result.Length != 0;
        }

        private bool TryCaptureOwnerPathStep(Entity owner, Entity child,
            out PortableOwnerPathStep step)
        {
            step = default(PortableOwnerPathStep);
            if (!EntityManager.HasComponent<PrefabRef>(child)) return false;
            Entity childPrefab = EntityManager.GetComponentData<PrefabRef>(child).m_Prefab;
            string childPrefabName;
            PortableEntityKind childKind;
            if (!TryPrefabName(childPrefab, out childPrefabName) ||
                !TryGetPortableEntityKind(child, out childKind)) return false;

            if (EntityManager.HasBuffer<global::Game.Buildings.InstalledUpgrade>(owner))
            {
                DynamicBuffer<global::Game.Buildings.InstalledUpgrade> buffer =
                    EntityManager.GetBuffer<global::Game.Buildings.InstalledUpgrade>(
                        owner, isReadOnly: true);
                int ordinal = 0;
                for (int i = 0; i < buffer.Length; i++)
                {
                    Entity candidate = buffer[i].m_Upgrade;
                    bool same = MatchesOwnerPathSibling(owner, candidate, childPrefab, childKind);
                    if (candidate == child)
                    {
                        step = CreateOwnerPathStep(PortableOwnerPathKind.InstalledUpgrade,
                            childKind, childPrefabName, i, ordinal);
                        return true;
                    }
                    if (same) ordinal++;
                }
            }
            if (EntityManager.HasBuffer<global::Game.Objects.SubObject>(owner))
            {
                DynamicBuffer<global::Game.Objects.SubObject> buffer =
                    EntityManager.GetBuffer<global::Game.Objects.SubObject>(
                        owner, isReadOnly: true);
                int ordinal = 0;
                for (int i = 0; i < buffer.Length; i++)
                {
                    Entity candidate = buffer[i].m_SubObject;
                    bool same = MatchesOwnerPathSibling(owner, candidate, childPrefab, childKind);
                    if (candidate == child)
                    {
                        step = CreateOwnerPathStep(PortableOwnerPathKind.SubObject,
                            childKind, childPrefabName, i, ordinal);
                        return true;
                    }
                    if (same) ordinal++;
                }
            }
            if (EntityManager.HasBuffer<global::Game.Net.SubNet>(owner))
            {
                DynamicBuffer<global::Game.Net.SubNet> buffer =
                    EntityManager.GetBuffer<global::Game.Net.SubNet>(
                        owner, isReadOnly: true);
                int ordinal = 0;
                for (int i = 0; i < buffer.Length; i++)
                {
                    Entity candidate = buffer[i].m_SubNet;
                    bool same = MatchesOwnerPathSibling(owner, candidate, childPrefab, childKind);
                    if (candidate == child)
                    {
                        step = CreateOwnerPathStep(PortableOwnerPathKind.SubNet,
                            childKind, childPrefabName, i, ordinal);
                        return true;
                    }
                    if (same) ordinal++;
                }
            }
            if (EntityManager.HasBuffer<global::Game.Areas.SubArea>(owner))
            {
                DynamicBuffer<global::Game.Areas.SubArea> buffer =
                    EntityManager.GetBuffer<global::Game.Areas.SubArea>(
                        owner, isReadOnly: true);
                int ordinal = 0;
                for (int i = 0; i < buffer.Length; i++)
                {
                    Entity candidate = buffer[i].m_Area;
                    bool same = MatchesOwnerPathSibling(owner, candidate, childPrefab, childKind);
                    if (candidate == child)
                    {
                        step = CreateOwnerPathStep(PortableOwnerPathKind.SubArea,
                            childKind, childPrefabName, i, ordinal);
                        return true;
                    }
                    if (same) ordinal++;
                }
            }
            return false;
        }

        private static PortableOwnerPathStep CreateOwnerPathStep(
            PortableOwnerPathKind bufferKind, PortableEntityKind entityKind, string prefabName,
            int bufferIndex, int prefabOrdinal)
        {
            return new PortableOwnerPathStep
            {
                BufferKind = bufferKind,
                EntityKind = entityKind,
                PrefabName = prefabName,
                BufferIndex = bufferIndex,
                PrefabOrdinal = prefabOrdinal,
            };
        }

        private bool MatchesOwnerPathSibling(Entity owner, Entity candidate, Entity prefab,
            PortableEntityKind kind)
        {
            if (candidate == Entity.Null || !EntityManager.Exists(candidate) ||
                EntityManager.HasComponent<Temp>(candidate) ||
                EntityManager.HasComponent<Deleted>(candidate) ||
                !EntityManager.HasComponent<PrefabRef>(candidate) ||
                EntityManager.GetComponentData<PrefabRef>(candidate).m_Prefab != prefab ||
                !EntityManager.HasComponent<Owner>(candidate) ||
                EntityManager.GetComponentData<Owner>(candidate).m_Owner != owner)
                return false;
            PortableEntityKind candidateKind;
            return TryGetPortableEntityKind(candidate, out candidateKind) &&
                   candidateKind == kind;
        }

        private bool TryGetPortableEntityKind(Entity entity, out PortableEntityKind kind)
        {
            if (EntityManager.HasComponent<global::Game.Net.Edge>(entity) &&
                EntityManager.HasComponent<global::Game.Net.Curve>(entity))
            {
                kind = PortableEntityKind.NetEdge;
                return true;
            }
            if (EntityManager.HasComponent<global::Game.Net.Node>(entity))
            {
                kind = PortableEntityKind.NetNode;
                return true;
            }
            if (EntityManager.HasComponent<global::Game.Areas.Area>(entity))
            {
                kind = PortableEntityKind.Area;
                return true;
            }
            if (EntityManager.HasComponent<global::Game.Objects.Object>(entity))
            {
                kind = PortableEntityKind.Object;
                return true;
            }
            kind = PortableEntityKind.None;
            return false;
        }

        private bool TryFindTopOwner(Entity entity, out Entity topOwner)
        {
            topOwner = Entity.Null;
            Entity cursor = entity;
            for (int depth = 0; depth < 64 && EntityManager.HasComponent<Owner>(cursor); depth++)
            {
                Entity next = EntityManager.GetComponentData<Owner>(cursor).m_Owner;
                if (next == Entity.Null || next == cursor || !EntityManager.Exists(next)) return false;
                topOwner = next;
                cursor = next;
            }
            return cursor == entity || !EntityManager.HasComponent<Owner>(cursor);
        }

        private bool TryPrefabName(Entity prefab, out string name)
        {
            name = prefab != Entity.Null ? _prefabSystem.GetPrefabName(prefab) : null;
            return !string.IsNullOrEmpty(name);
        }
    }
}
