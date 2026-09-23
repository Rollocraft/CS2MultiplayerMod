using System.Collections.Generic;
using Game.Common;
using Game.Prefabs;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Commands;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    // An extractor or storage lot drawn while placing its building, held until the game completes it.
    public partial class BuildSyncSystem
    {
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

            return TryFindTopOwner(area, out Entity topOwner) &&
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
            return _prefabIndex.TryResolve(definition.PrefabName, out Entity prefab) &&
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
                SyncLog.Warn(LogTopic.Buildings,
                    "BuildSync: specialized object/area operation was incomplete; not sent.");
                if (Mod.Service != null)
                    Mod.Service.RequestAutomaticWorldRecovery(
                        "specialized building capture was incomplete");
                ClearSpecializedAreaCapture();
                return;
            }

            var operation = new ObjectToolOperationCommand
            {
                RootIndex = rootIndex,
                // Keep the compact placement input, or the receiver must resolve every sender-local reference.
                HasPlacementInput = source.HasPlacementInput,
                ToolRandomSeed = source.ToolRandomSeed,
                PlacementTarget = source.PlacementTarget,
                Definitions = definitions.ToArray(),
            };
            try
            {
                if (TryPublishLocalObjectOperation(operation))
                {
                    SyncLog.Trace(LogTopic.Buildings,
                        "specialized object/area operation captured op=" + operation.OperationId +
                        " defs=" + operation.Definitions.Length + " areaNodes=" +
                        _pendingSpecializedAreaDefinition.AreaNodes.Length);
                    PublishOwnedAreaSnapshot(root, _pendingSpecializedAreaDefinition);
                }
            }
            catch (System.Exception ex)
            {
                SyncLog.Warn(LogTopic.Buildings,
                    "BuildSync: specialized object/area operation was not sent: " + ex.Message);
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
                SyncLog.Trace(LogTopic.Buildings, "specialized owned-area safeguard sent nodes=" +
                    count);
            }
            catch (System.Exception ex)
            {
                SyncLog.Warn(LogTopic.Buildings, "BuildSync: owned-area safeguard was not sent: " +
                    ex.Message);
                if (Mod.Service != null)
                    Mod.Service.RequestAutomaticWorldRecovery(
                        "specialized owned-area safeguard failed");
            }
        }

        /// <summary>The building is not published yet, so the periodic area scan must leave this lot alone.</summary>
        internal bool IsSpecializedAreaHeld(Entity area)
        {
            return area != Entity.Null && area == _pendingSpecializedArea &&
                   _pendingSpecializedObjectOperation != null;
        }

        /// <summary>
        /// Publishes the held object half when the area tool returns without a polygon: the building
        /// committed with its default lot, exactly as emitted.
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
                SyncLog.Trace(LogTopic.Buildings,
                    "specialized object/area handoff ended with no committed building");
                ClearSpecializedAreaCapture();
                return;
            }

            try
            {
                if (TryPublishLocalObjectOperation(operation))
                    SyncLog.Trace(LogTopic.Buildings, "specialized object without area captured op=" +
                        operation.OperationId + " defs=" + operation.Definitions.Length);
            }
            catch (System.Exception ex)
            {
                SyncLog.Warn(LogTopic.Buildings,
                    "BuildSync: specialized object without area was not sent: " + ex.Message);
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

        /// <summary>The building is standing (held lot, else the committed root); never publish an uncommitted one.</summary>
        private bool SpecializedPlacementStillCommitted(ObjectToolOperationCommand operation)
        {
            if (SpecializedAreaOwnerStillMatches(_pendingSpecializedArea, operation)) return true;

            if (!TryGetNewCommittedObjectRoot(operation, out ObjectToolDefinitionIntent root) ||
                !_prefabIndex.TryResolve(root.PrefabName, out Entity rootPrefab)) return false;

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
        /// Only once the area apply reached live entities: the gate may have discarded local definitions.
        /// </summary>
        private void CaptureCompletedSpecializedArea()
        {
            if (!_completeSpecializedAreaThisFrame) return;
            _completeSpecializedAreaThisFrame = false;
            if (!TryCaptureCompletedSpecializedArea(out ObjectToolDefinitionIntent completed))
            {
                SyncLog.Trace(LogTopic.Buildings, "specialized object/area apply not observed");
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

            if (!TryFindTopOwner(area, out Entity topOwner) ||
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

            // A new definition closes its ring with a repeated first vertex.
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
    }
}
