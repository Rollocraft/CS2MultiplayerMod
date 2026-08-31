using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
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
    // The tool input behind an operation: the control points a placement, stamp or relocation was
    // drawn with, and the snap target it was attached to. Read from the tool while it still holds
    // them, because they are gone by the time the definitions commit.
    public partial class BuildSyncSystem
    {
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
