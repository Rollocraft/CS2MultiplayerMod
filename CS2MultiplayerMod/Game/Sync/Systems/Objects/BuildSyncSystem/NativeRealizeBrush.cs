using Game.Common;
using Game.Objects;
using Game.Prefabs;
using Game.Tools;
using Unity.Entities;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.Infrastructure;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    public partial class BuildSyncSystem
    {
        /// <summary>
        /// Independent top-level creates/deletes, the only object operations that may bypass the serialized
        /// coordinator; owned graphs stay on the atomic path.
        /// </summary>
        private bool IsIndependentObjectBatch(ObjectToolOperationCommand command,
            ResolvedObjectDefinition[] resolved)
        {
            if (command == null || command.HasPlacementInput || command.IsAssetStamp ||
                command.Definitions == null || command.Definitions.Length == 0 ||
                resolved == null || resolved.Length != command.Definitions.Length) return false;

            for (int i = 0; i < command.Definitions.Length; i++)
            {
                ObjectToolDefinitionIntent definition = command.Definitions[i];
                if (!ObjectBrushCapture.IsIndependentObjectDefinition(definition)) return false;

                if (!definition.PrefabIsNull)
                {
                    Entity prefab = resolved[i].Prefab;
                    if (prefab == Entity.Null || IsSimulationOnlyPlacementPrefab(prefab) ||
                        RequiresCompleteObjectLifecycle(prefab)) return false;
                    continue;
                }

                Entity original = resolved[i].Original;
                if (original == Entity.Null || !EntityManager.Exists(original) ||
                    EntityManager.HasComponent<Deleted>(original) ||
                    EntityManager.HasComponent<Temp>(original) ||
                    EntityManager.HasComponent<Owner>(original) ||
                    EntityManager.HasComponent<Attached>(original) ||
                    !EntityManager.HasComponent<PrefabRef>(original) ||
                    !EntityManager.HasComponent<Transform>(original)) return false;

                Entity originalPrefab = EntityManager.GetComponentData<PrefabRef>(original).m_Prefab;
                if (IsSimulationOnlyPlacementPrefab(originalPrefab) ||
                    RequiresCompleteObjectLifecycle(originalPrefab)) return false;
            }
            return true;
        }

        /// <summary>
        /// One simple-object batch this ToolUpdate, deletes first so a replacement is not taken for a
        /// duplicate; every definition exists before Modification1.
        /// </summary>
        private NativeObjectResult RealizeIndependentObjectBatch(NativeObjectOperationKey key,
            ObjectToolOperationCommand command, ResolvedObjectDefinition[] resolved,
            int originPlayerId, long now)
        {
            int deleted = 0;
            int placedBefore = _rzFrameSpawned;
            DeleteSyncSystem deleteSync = World.GetExistingSystemManaged<DeleteSyncSystem>();

            for (int i = 0; i < command.Definitions.Length; i++)
            {
                ObjectToolDefinitionIntent definition = command.Definitions[i];
                if (!definition.PrefabIsNull) continue;

                Entity original = resolved[i].Original;
                if (!EntityManager.Exists(original) || EntityManager.HasComponent<Deleted>(original))
                    continue;

                PrefabRef prefabRef = EntityManager.GetComponentData<PrefabRef>(original);
                Transform transform = EntityManager.GetComponentData<Transform>(original);
                string prefabName = _prefabSystem.GetPrefabName(prefabRef.m_Prefab);
                if (deleteSync != null)
                    deleteSync.MarkRemoteObjectDelete(prefabName, transform.m_Position, now);
                EntityManager.AddComponent<Deleted>(original);
                deleted++;
            }

            _suppressBatchedObjectDetail = true;
            try
            {
                for (int i = 0; i < command.Definitions.Length; i++)
                {
                    ObjectToolDefinitionIntent definition = command.Definitions[i];
                    if (definition.PrefabIsNull) continue;

                    ObjectDefinitionIntent value = definition.Object;
                    RealizeCommand(new ObjectPlacementCommand
                    {
                        PrefabName = definition.PrefabName,
                        PosX = value.PosX,
                        PosY = value.PosY,
                        PosZ = value.PosZ,
                        RotX = value.RotX,
                        RotY = value.RotY,
                        RotZ = value.RotZ,
                        RotW = value.RotW,
                        RandomSeed = definition.RandomSeed,
                        Age = value.Age,
                        AttachKind = ObjectAttachKind.None,
                    }, resolved[i].Prefab, originPlayerId, now);
                }
            }
            finally { _suppressBatchedObjectDetail = false; }

            _recentNativeObjectOperations.Remember(key, now, NativeObjectReplayRememberMs);
            SyncLog.Detail(LogTopic.Buildings, "BuildSync realize: applied independent object " +
                "batch in one frame op=" + command.OperationId + " placed=" +
                (_rzFrameSpawned - placedBefore) + " deleted=" + deleted + " requested=" +
                command.Definitions.Length + ".");
            return NativeObjectResult.Completed;
        }
    }
}
