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

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    // Publishing once the object graph committed, by matching new entities to a remembered operation.
    public partial class BuildSyncSystem
    {
        /// <summary>
        /// Correlates any newly applied object, including an owned extension, with its preview graph; runs
        /// before the reduced capture paths.
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

        /// <summary>The committed root's prefab, transform and seed identify its preview graph.</summary>
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
                    if (!TryGetNewCommittedObjectRoot(operation, out ObjectToolDefinitionIntent root) ||
                        !CommittedRootMatches(root, prefabName, transform, randomSeed)) continue;

                    int definitionCount = operation.Definitions.Length;
                    try
                    {
                        if (!TryPublishLocalObjectOperation(operation)) return false;
                        if (object.ReferenceEquals(_cachedLocalObjectOperation, operation))
                            _cachedLocalObjectOperation = null;
                        SyncLog.Trace(LogTopic.Buildings, "object graph matched committed root op=" +
                            operation.OperationId + " defs=" + definitionCount + " prefab=" +
                            prefabName + " seed=" + randomSeed);
                        return true;
                    }
                    catch (System.Exception ex)
                    {
                        ForgetRecentLocalObjectOperation(operation);
                        if (object.ReferenceEquals(_cachedLocalObjectOperation, operation))
                            _cachedLocalObjectOperation = null;
                        SyncLog.Warn(LogTopic.Buildings,
                            "BuildSync: committed object graph was not sent: " + ex.Message);
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

            // Attachment moves road-attached roots; prefab and seed identify, bounded offsets allow height fixes.
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
                if (TryGetNewCommittedObjectRoot(
                        _recentLocalObjectOperations[_recentLocalObjectOperations.Count - 1].Operation,
                        out ObjectToolDefinitionIntent root))
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
            SyncLog.Trace(LogTopic.Buildings, "object graph match missed " +
                _lastObjectGraphMissDetail);
        }
    }
}
