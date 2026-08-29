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

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    // Capturing a specialized area - an extractor or storage lot drawn as part of placing its
    // building. The polygon is not known when the placement is captured, so the area is held
    // until the game completes it, and published then.
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
    }
}
