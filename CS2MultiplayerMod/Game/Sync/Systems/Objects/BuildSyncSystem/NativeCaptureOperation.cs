using Game.Prefabs;
using Game.Tools;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Game.Sync.Commands;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    public partial class BuildSyncSystem
    {
        /// <summary>
        /// One element of a fixed-element net the receiver would divide again. The tool emits -1 for a
        /// drawn course; a course naming an original is exempt.
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
        /// The undivided held graph for the same root (previews are seen before division). False keeps the
        /// divided graph.
        /// </summary>
        private bool TryFindUndividedFixedNetOperation(ObjectToolOperationCommand divided,
            out ObjectToolOperationCommand result)
        {
            result = null;
            if (!TryGetNewCommittedObjectRoot(divided, out ObjectToolDefinitionIntent root)) return false;

            for (int i = _recentLocalObjectOperations.Count - 1; i >= 0; i--)
            {
                ObjectToolOperationCommand candidate = _recentLocalObjectOperations[i].Operation;
                if (!TryGetNewCommittedObjectRoot(candidate, out ObjectToolDefinitionIntent candidateRoot) ||
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
            // The prefab-less host update must not outrank the new extension.
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

            if (!_prefabIndex.TryResolve(definition.PrefabName, out Entity prefab) ||
                (!EntityManager.HasComponent<ServiceUpgradeData>(prefab) &&
                 !EntityManager.HasComponent<BuildingExtensionData>(prefab)))
                return false;

            // Identified through OwnerDefinition, not necessarily Upgrade; a live owner rules out a new
            // building's integral objects.
            if (!_prefabIndex.TryResolve(definition.OwnerDefinitionPrefabName, out Entity ownerPrefab))
                return false;
            return FindPortableObject(ownerPrefab,
                       new float3(definition.OwnerDefinitionX, definition.OwnerDefinitionY,
                           definition.OwnerDefinitionZ),
                       default(PortableEntityRef)) != Entity.Null;
        }

        private void RememberSelectedAssetStampPrefab(global::Game.Tools.ToolBaseSystem active) =>
            _selectedAssetStampPrefabName = GetSelectedAssetStampPrefabName(active);

        private string GetSelectedAssetStampPrefabName(global::Game.Tools.ToolBaseSystem active)
        {
            PrefabBase selected = active != null ? active.GetPrefab() : null;
            if (!(selected is AssetStampPrefab)) return null;
            if (!_prefabSystem.TryGetEntity(selected, out Entity prefab) || prefab == Entity.Null ||
                !EntityManager.Exists(prefab) || !EntityManager.HasComponent<AssetStampData>(prefab))
                return null;
            return _prefabSystem.GetPrefabName(prefab);
        }

        private void RememberRecentLocalObjectOperation(ObjectToolOperationCommand operation)
        {
            if (!TryGetNewCommittedObjectRoot(operation, out ObjectToolDefinitionIntent root)) return;

            long now = Mod.Service != null ? Mod.Service.NowMs : 0;
            PruneRecentLocalObjectOperations(now);
            for (int i = _recentLocalObjectOperations.Count - 1; i >= 0; i--)
            {
                RecentLocalObjectOperation recent = _recentLocalObjectOperations[i];
                if (!TryGetNewCommittedObjectRoot(recent.Operation, out ObjectToolDefinitionIntent recentRoot) ||
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

        private void ClearRecentLocalObjectOperations() => _recentLocalObjectOperations.Clear();
    }
}
