using System.Collections.Generic;
using Game.Common;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.Infrastructure;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    // Capturing the local object tool's action, inferred from the definitions it emits and the state
    // it leaves; the rest lives in the NativeCapture*.cs siblings.
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
            public bool AllowAttachmentEnvelope;
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
        // Sampled before ToolOutputSystem: a one-shot stamp can switch tools while its rootless graph is emitted.
        private string _selectedAssetStampPrefabName;
        private long _nextLocalObjectOperationId = 1;
        private bool _nativeLifecycleCapturedThisFrame;
        private ObjectToolOperationCommand _pendingSpecializedObjectOperation;
        private ObjectToolDefinitionIntent _pendingSpecializedAreaDefinition;
        private Entity _pendingSpecializedArea;
        private bool _completeSpecializedAreaThisFrame;

        /// <summary>This frame's Apply was published natively; legacy final-entity capture stays silent.</summary>
        public bool NativeLifecycleCapturedThisFrame => _nativeLifecycleCapturedThisFrame;

        /// <summary>
        /// A specialized placement's object half is held for its polygon; the compact upgrade command must
        /// not stand in for it.
        /// </summary>
        internal bool HasPendingSpecializedAreaCapture =>
            _pendingSpecializedObjectOperation != null ||
            (_areaToolSystem != null && _areaToolSystem.recreate != Entity.Null);

        /// <summary>
        /// After the output barrier. Object graphs are captured on Apply; network-owned ones keep their exact
        /// preview batch in case the prefab switches tools while applying.
        /// </summary>
        public void ObserveLocalObjectToolOutput(NativeArray<Entity> definitions)
        {
            ObserveLocalObjectToolStateAfterOutput();

            global::Game.Tools.ToolBaseSystem active =
                _toolSystem != null ? _toolSystem.activeTool : null;
            if (!(active is global::Game.Tools.NetToolSystem) ||
                !NativeObjectGraph.HasNewTopLevelObjectRoot(EntityManager, definitions)) return;

            // Owner-linked courses travel with their object graph; the recent set retains it, not the tool cache.
            CaptureObjectToolOperation(definitions);
            _cachedLocalObjectOperation = null;
        }

        /// <summary>Advances the specialized-industry handoff; no encoding on hover frames.</summary>
        private void ObserveLocalObjectToolStateAfterOutput()
        {
            global::Game.Tools.ToolBaseSystem active = _toolSystem != null ? _toolSystem.activeTool : null;
            Entity recreate = _areaToolSystem != null ? _areaToolSystem.recreate : Entity.Null;

            // Specialized industry: the object tool commits the building and hands its lot to the area tool;
            // the object definition is held through that handoff and published with the polygon.
            bool areaHandoff = recreate != Entity.Null &&
                               (active is AreaToolSystem || active is ObjectToolSystem);
            if (areaHandoff)
            {
                if (_pendingSpecializedObjectOperation == null &&
                    _cachedLocalObjectOperation != null &&
                    TryBeginSpecializedAreaCapture(recreate))
                {
                    SyncLog.Trace(LogTopic.Buildings, "specialized object/area handoff tracked");
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
                        // On completion activeTool is already the object tool while applyMode is the area tool's; the live
                        // area is captured at ModificationEnd.
                        if (active is ObjectToolSystem &&
                            _toolSystem.applyMode == ApplyMode.Apply)
                            _completeSpecializedAreaThisFrame = true;
                        return;
                    }
                }

                if (active is AreaToolSystem)
                {
                    // The area can appear a frame before its owner path links; retry rather than lose the capture.
                    return;
                }
            }

            // No polygon completed: the held graph is the whole local change.
            if (_pendingSpecializedObjectOperation != null)
                FinishSpecializedAreaCaptureWithoutPolygon();
        }

        private static bool IsObjectLifecycleTool(global::Game.Tools.ToolBaseSystem tool) =>
            tool is ObjectToolSystem || tool is UpgradeToolSystem;

        /// <summary>A move-tool batch; returns on the first Relocate.</summary>
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
        /// A service upgrade added to a live building: a new upgrade/extension object with an
        /// <see cref="OwnerDefinition"/> and no original, plus the host's prefab-less Upgrade definition on
        /// a live original (a new building has none). Player-drawn lots are excluded.
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

        /// <summary>The owned elements rebuild from the prefab alone; drawn extractor/storage lots do not.</summary>
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
            // Relocations and upgrades travel as their tool's compact inputs (MoveSync, UpgradeSync) and are
            // regenerated on the receiver; capturing their full graph each preview frame was the FPS cost and
            // cannot resolve across differently subdivided roads. Drawn-lot upgrades are the exception.
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
            // One read-only snapshot serves the whole batch's owner lookups.
            BeginPortableResolve();
            try
            {
                for (int i = 0; i < definitions.Length; i++)
                {
                    Entity entity = definitions[i];
                    if (!EntityManager.Exists(entity) ||
                        !EntityManager.HasComponent<CreationDefinition>(entity)) continue;

                    // The brush display has no world-edit shape.
                    if (ObjectBrushCapture.IsVisualDefinition(EntityManager, entity)) continue;

                    if (!TryCaptureObjectToolDefinition(entity, out ObjectToolDefinitionIntent definition))
                    {
                        // All-or-nothing; unsupported output uses the final-entity path.
                        _cachedLocalObjectOperation = null;
                        return;
                    }

                    // Prefer a definition without OwnerDefinition, then a new object over an owner update.
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
                // An empty batch with ApplyMode.None means the preview is unchanged, not gone.
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
                    SyncLog.Trace(LogTopic.Buildings,
                        "asset stamp definitions lacked selected prefab");
                    return;
                }
                // These are independently placed stamp sub-objects, not an owner.
                root = ObjectToolOperationCommand.AssetStampRootIndex;
            }

            var operation = new ObjectToolOperationCommand
            {
                RootIndex = (short)root,
                AssetStampPrefabName = stampPrefabName,
                Definitions = captured.ToArray(),
            };
            AttachPlacementInput(operation);

            // A dam arrives already divided into fixed elements; send the undivided graph from a frame earlier
            // so the receiver divides once.
            if (hasFixedElementCut &&
                TryFindUndividedFixedNetOperation(operation, out ObjectToolOperationCommand undivided))
            {
                AttachPlacementInput(undivided);
                _cachedLocalObjectOperation = undivided;
                RememberRecentLocalObjectOperation(undivided);
                SyncLog.Trace(LogTopic.Buildings, "fixed-element net kept undivided defs=" +
                    undivided.Definitions.Length + " divided=" + captured.Count);
                return;
            }
            if (hasFixedElementCut)
                SyncLog.Trace(LogTopic.Buildings,
                    "fixed-element net has no undivided graph; publishing divided defs=" +
                    captured.Count);

            _cachedLocalObjectOperation = operation;
            RememberRecentLocalObjectOperation(_cachedLocalObjectOperation);
            SyncLog.Trace(LogTopic.Buildings,
                hasStampingNet ? "asset stamp native definitions captured=" + captured.Count +
                " prefab=" + stampPrefabName : "object native definitions captured=" +
                captured.Count + " root=" + captured[root].PrefabName + " seed=" +
                unchecked((ushort)captured[root].RandomSeed));
        }
    }
}
