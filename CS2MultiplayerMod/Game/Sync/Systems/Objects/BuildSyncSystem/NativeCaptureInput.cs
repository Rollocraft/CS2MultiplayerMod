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
    // The tool's input (control points, snap target), read while the tool still holds it.
    public partial class BuildSyncSystem
    {
        private void RememberObjectToolControlPoint(ObjectToolSystem tool)
        {
            NativeList<ControlPoint> points = tool.GetControlPoints(out Unity.Jobs.JobHandle dependencies);
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
            NativeList<ControlPoint> points = tool.GetControlPoints(out Unity.Jobs.JobHandle dependencies);
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
        /// Adds the road/node the placement snapped to, kept through the specialized-industry handoff.
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
            // A standing definition may outlive the cursor's move to a new point.
            if (math.distancesq(rootPosition, point.m_Position) > 0.25f ||
                math.abs(math.dot(rootRotation, pointRotation)) < 0.995f) return;

            if (!TryCapturePortableRef(point.m_OriginalEntity, out PortableEntityRef target))
            {
                if (PlacementSnapTargetReachesGenerator(point.m_OriginalEntity))
                {
                    SyncLog.Trace(LogTopic.Buildings,
                        "building placement snap target was not portable");
                    return;
                }
                // The target only shaped the position, which the root definition already carries.
                target = default(PortableEntityRef);
            }

            operation.HasPlacementInput = true;
            operation.ToolRandomSeed = AppliedLifecycleToolSeed;
            operation.PlacementTarget = target;
            SyncLog.Trace(LogTopic.Buildings, "building placement inputs captured prefab=" +
                root.PrefabName + " target=" + target.Kind);
        }

        /// <summary>
        /// Whether the generator reads the snap target: a parent with SubObject, a placeholder, or an
        /// attached object. A zoned street's Block is none of these; its effect is already in the position.
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
            NativeList<ControlPoint> points = tool.GetControlPoints(out Unity.Jobs.JobHandle dependencies);
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
        /// Publishes a stamp as its tool's inputs. Internal junctions merge only on bit-identical endpoints,
        /// which regeneration reproduces and a definition round trip does not.
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
                SyncLog.Warn(LogTopic.Buildings, "BuildSync: asset-stamp inputs were not sent: " +
                    ex.Message);
                return false;
            }

            _nativeLifecycleCapturedThisFrame = true;
            SyncLog.Trace(LogTopic.Buildings, "asset stamp inputs published op=" +
                command.OperationId + " prefab=" + prefabName + " seed=" + command.ToolRandomSeed);
            return true;
        }

        private bool TryTakeRelocationControlPoint(float3 position, quaternion rotation,
            out ControlPoint controlPoint)
        {
            controlPoint = _lastObjectToolControlPoint;
            if (!_hasLastObjectToolControlPoint) return false;
            _hasLastObjectToolControlPoint = false;

            // Reject a stale point from an earlier tool; quaternion sign is immaterial.
            if (math.distancesq(controlPoint.m_Position, position) > 0.25f) return false;
            return math.abs(math.dot(controlPoint.m_Rotation.value, rotation.value)) >= 0.98f;
        }

        /// <summary>
        /// Publishes a relocation from the untagged standing definitions: before ToolOutputSystem they are
        /// exactly what the committing Temps came from. The Relocate root names the moved entity and its
        /// destination; the sampled control point gives the snapped road or node.
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
                // Carried upgrades have no prefab; only the tool's own root carries it, owned or not.
                if (creation.m_Owner != Entity.Null || creation.m_Prefab == Entity.Null) continue;

                Entity original = creation.m_Original;
                if (original == Entity.Null || !EntityManager.Exists(original) ||
                    !EntityManager.HasComponent<global::Game.Objects.Transform>(original) ||
                    !EntityManager.HasComponent<PrefabRef>(original)) continue;

                ObjectDefinition placement =
                    EntityManager.GetComponentData<ObjectDefinition>(entity);
                if (!TryTakeRelocationControlPoint(placement.m_Position, placement.m_Rotation,
                        out ControlPoint appliedPoint))
                {
                    // Without the snap target, leave it to the final-entity detector.
                    SyncLog.Trace(LogTopic.Buildings,
                        "relocation control point unavailable; final-entity fallback");
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
        /// Captures a lifecycle action once, between Apply and ToolOutputSystem; hover frames never encode.
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
                // The structural root decides: a one-shot net prefab may already have switched tools.
                bool fromNetOwnedObjectGraph = !fromObjectLifecycleTool &&
                    NativeObjectGraph.HasNewTopLevelObjectRoot(EntityManager, definitions);
                if (!fromObjectLifecycleTool && !fromNetOwnedObjectGraph) return;

                // An armed remote commit keeps the local preview from committing, unless it was stood down for
                // this Apply; then the placement commits and must be published.
                if (_nativeNetCoordinator != null && _nativeNetCoordinator.HasArmedToolCommit &&
                    !_nativeNetCoordinator.LocalToolOutputProtectedThisFrame)
                    return;

                // The apply pass does not keep the old position; read it from this snapshot.
                if (fromObjectLifecycleTool)
                    CaptureLocalRelocationForApply(definitions);

                CaptureObjectToolOperation(definitions);
                ObjectToolOperationCommand operation = _cachedLocalObjectOperation;
                if (operation == null || operation.Definitions == null) return;

                // Registered before ToolOutput applies it; a specialized object half is Created before it publishes.
                RememberPlayerPlacedSpawnables(operation,
                    Mod.Service != null ? Mod.Service.NowMs : 0);

                if (operation.IsAssetStamp)
                {
                    string selectedStamp = GetSelectedAssetStampPrefabName(_toolSystem.activeTool) ??
                                           _selectedAssetStampPrefabName;
                    if (!string.Equals(selectedStamp, operation.AssetStampPrefabName,
                            System.StringComparison.Ordinal)) return;

                    // Prefer the tool's inputs; the definition batch is the fallback.
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
                SyncLog.Trace(LogTopic.Buildings,
                    (operation.IsAssetStamp ? "asset stamp" : "object lifecycle") +
                    " apply captured from standing definitions=" + operation.Definitions.Length);
                PublishCachedLocalObjectOperation();
            }
            finally
            {
                definitions.Dispose();
            }
        }

        /// <summary>Resets the one-frame marker; capture happens in <see cref="CaptureLocalObjectApplyBeforeToolOutput"/>.</summary>
        public void CaptureLocalObjectApply() => _nativeLifecycleCapturedThisFrame = false;

        private void PublishCachedLocalObjectOperation()
        {
            if (_cachedLocalObjectOperation == null) return;

            // Publish only once a new root exists with the preview's prefab, transform and seed; this also
            // ignores the replacement ghost after a click.
            if (TryGetNewCommittedObjectRoot(_cachedLocalObjectOperation,
                out ObjectToolDefinitionIntent newRoot)) return;

            try
            {
                if (TryPublishLocalObjectOperation(_cachedLocalObjectOperation))
                    SyncLog.Trace(LogTopic.Buildings, "object operation captured op=" +
                        _cachedLocalObjectOperation.OperationId + " defs=" +
                        _cachedLocalObjectOperation.Definitions.Length);
            }
            catch (System.Exception ex)
            {
                SyncLog.Warn(LogTopic.Buildings, "BuildSync: native object operation was not sent: " +
                    ex.Message);
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
    }
}
