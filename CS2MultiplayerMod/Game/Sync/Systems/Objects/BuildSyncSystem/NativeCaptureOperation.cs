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
    // Recognising what an operation is - which of its definitions is the root, whether a course
    // was cut at a fixed element, whether it is a new service upgrade - and keeping the short
    // list of operations recently emitted locally, so a commit can be matched back to one.
    public partial class BuildSyncSystem
    {
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
    }
}
