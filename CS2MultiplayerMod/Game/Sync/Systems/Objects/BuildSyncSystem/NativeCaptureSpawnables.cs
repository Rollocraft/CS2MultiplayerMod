using Game.Common;
using Game.Prefabs;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Commands;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    public partial class BuildSyncSystem
    {
        /// <summary>
        /// Consumes the identity of a spawnable the player's tool placed: exact prefab, seed, 10 cm and
        /// orientation for fixed roots, a bounded envelope for attached ones, or the live specialized graph.
        /// </summary>
        internal bool ConsumePlayerPlacedSpawnable(Entity entity, long now)
        {
            if (entity == Entity.Null || !EntityManager.Exists(entity) ||
                !EntityManager.HasComponent<PrefabRef>(entity) ||
                !EntityManager.HasComponent<global::Game.Objects.Transform>(entity)) return false;

            Entity prefab = EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab;
            if (prefab == Entity.Null || !EntityManager.Exists(prefab) ||
                !EntityManager.HasComponent<SpawnableBuildingData>(prefab)) return false;

            PrunePlayerPlacedSpawnables(now);
            global::Game.Objects.Transform transform =
                EntityManager.GetComponentData<global::Game.Objects.Transform>(entity);
            float4 rotation = math.normalizesafe(transform.m_Rotation.value,
                new float4(0f, 0f, 0f, 1f));
            bool hasSeed = EntityManager.HasComponent<PseudoRandomSeed>(entity);
            ushort seed = hasSeed
                ? EntityManager.GetComponentData<PseudoRandomSeed>(entity).m_Seed
                : (ushort)0;

            for (int i = _playerPlacedSpawnableCreations.Count - 1; i >= 0; i--)
            {
                PlayerPlacedSpawnableCreation candidate =
                    _playerPlacedSpawnableCreations[i];
                if (candidate.Prefab != prefab ||
                    (hasSeed && candidate.RandomSeed != seed)) continue;

                // Attachment can move the visible building; the definition already proves attachment intent.
                bool attached = candidate.AllowAttachmentEnvelope ||
                                EntityManager.HasComponent<global::Game.Objects.Attached>(entity);
                bool transformMatches = attached
                    ? math.distancesq(candidate.Position.xz, transform.m_Position.xz) <=
                          AttachedPlayerPlacedSpawnableMatchRadiusSq &&
                      math.abs(candidate.Position.y - transform.m_Position.y) <=
                          AttachedPlayerPlacedSpawnableMatchHeight
                    : math.distancesq(candidate.Position, transform.m_Position) <=
                          PlayerPlacedSpawnableMatchDistanceSq &&
                      math.abs(math.dot(candidate.Rotation, rotation)) >=
                          PlayerPlacedSpawnableMatchRotationDot;
                if (!transformMatches) continue;

                _playerPlacedSpawnableCreations.RemoveAt(i);
                SyncLog.Trace(LogTopic.Buildings, "player-placed spawnable guard consumed");
                return true;
            }

            return IsLiveSpecializedIndustrySpawnable(entity, prefab);
        }

        private void RememberPlayerPlacedSpawnables(ObjectToolOperationCommand operation, long now)
        {
            if (!IsSpecializedIndustryPlacement(operation)) return;
            PrunePlayerPlacedSpawnables(now);

            int remembered = 0;
            Entity rootPrefab = ResolveSpecializedRootPrefab(operation);
            for (int i = 0; i < operation.Definitions.Length; i++)
            {
                ObjectToolDefinitionIntent definition = operation.Definitions[i];
                if (definition == null || definition.Kind != ObjectToolDefinitionKind.Object ||
                    definition.PrefabIsNull ||
                    !_prefabIndex.TryResolve(definition.PrefabName, out Entity prefab) ||
                    !IsAllowedSpecializedSpawnable(operation, i, prefab)) continue;

                bool attached = i != operation.RootIndex &&
                                TryGetSpecializedPlaceholderAttachment(operation, i,
                                    operation.Definitions[operation.RootIndex],
                                    rootPrefab, out Entity attachmentPrefab);
                RememberPlayerPlacedSpawnable(prefab,
                    new float3(definition.Object.PosX, definition.Object.PosY,
                        definition.Object.PosZ),
                    new float4(definition.Object.RotX, definition.Object.RotY,
                        definition.Object.RotZ, definition.Object.RotW),
                    unchecked((ushort)definition.RandomSeed), attached, now);
                remembered++;
            }

            if (remembered > 0)
                SyncLog.Trace(LogTopic.Buildings, "player-placed spawnable guard armed=" +
                    remembered);
        }

        private Entity ResolveSpecializedRootPrefab(ObjectToolOperationCommand operation)
        {
            ObjectToolDefinitionIntent root = operation != null && operation.Definitions != null &&
                                              operation.RootIndex >= 0 &&
                                              operation.RootIndex < operation.Definitions.Length
                ? operation.Definitions[operation.RootIndex]
                : null;
            return root != null && _prefabIndex.TryResolve(root.PrefabName, out Entity rootPrefab)
                ? rootPrefab : Entity.Null;
        }

        private void RememberPlayerPlacedSpawnable(Entity prefab, float3 position,
            float4 rotation, ushort randomSeed, bool allowAttachmentEnvelope, long now)
        {
            var candidate = new PlayerPlacedSpawnableCreation
            {
                Prefab = prefab,
                Position = position,
                Rotation = math.normalizesafe(rotation, new float4(0f, 0f, 0f, 1f)),
                RandomSeed = randomSeed,
                AllowAttachmentEnvelope = allowAttachmentEnvelope,
                ExpiryMs = now > 0 ? now + PlayerPlacedSpawnableLifetimeMs : long.MaxValue,
            };

            for (int i = _playerPlacedSpawnableCreations.Count - 1; i >= 0; i--)
            {
                PlayerPlacedSpawnableCreation existing =
                    _playerPlacedSpawnableCreations[i];
                if (existing.Prefab != candidate.Prefab ||
                    existing.RandomSeed != candidate.RandomSeed ||
                    math.distancesq(existing.Position, candidate.Position) >
                    PlayerPlacedSpawnableMatchDistanceSq ||
                    math.abs(math.dot(existing.Rotation, candidate.Rotation)) <
                    PlayerPlacedSpawnableMatchRotationDot) continue;
                _playerPlacedSpawnableCreations[i] = candidate;
                return;
            }

            if (_playerPlacedSpawnableCreations.Count >= MaxPlayerPlacedSpawnableCreations)
                _playerPlacedSpawnableCreations.RemoveAt(0);
            _playerPlacedSpawnableCreations.Add(candidate);
        }

        private void PrunePlayerPlacedSpawnables(long now)
        {
            if (now <= 0) return;
            for (int i = _playerPlacedSpawnableCreations.Count - 1; i >= 0; i--)
                if (_playerPlacedSpawnableCreations[i].ExpiryMs <= now)
                    _playerPlacedSpawnableCreations.RemoveAt(i);
        }

        private void ClearPlayerPlacedSpawnables() => _playerPlacedSpawnableCreations.Clear();
    }
}
