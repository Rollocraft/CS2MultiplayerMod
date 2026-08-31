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
    // Capturing what this peer's own object tool did, so the other side can reproduce it. The
    // game never tells a mod "the player placed this"; it is inferred from the definitions the
    // tool emits and the state it is left in afterwards, which is why so much here is matching
    // and remembering rather than reading.
    //
    // This file holds the shared state and the observe-and-capture entry point. The rest is split
    // across the sibling NativeCapture*.cs files: operation bookkeeping, publishing a committed
    // graph, specialized areas, tool input, player-placed spawnables, definitions, and the
    // portable references that name an entity to a peer that does not share our ids.
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
    }
}
