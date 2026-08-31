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
    // Remembering the spawnable objects a local placement created, so the lifecycle capture can
    // tell one the player placed from one the simulation grew.
    public partial class BuildSyncSystem
    {
        /// <summary>
        /// Consume the one-shot identity of a spawnable building produced by an explicitly applied
        /// object-tool graph. A fixed root requires the same prefab and 16-bit variant seed,
        /// position within 10 cm, and the captured orientation; attached visible buildings use a
        /// bounded snap envelope because attachment resolution changes their definition transform.
        /// The live specialized owner/attachment graph is also accepted as a durable fallback.
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
                    !hasSeed || candidate.RandomSeed != seed) continue;

                // Attachment resolution can snap and rotate the committed visible building away
                // from its prefab-local definition. Seed + prefab remain exact; use the same
                // bounded transform envelope as committed-root correlation for an attached live
                // instance, while ordinary roots retain the strict 10 cm/orientation match.
                bool attached = EntityManager.HasComponent<global::Game.Objects.Attached>(entity);
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
                Diagnostics.FlightRecorder.Note("player-placed spawnable guard consumed");
                return true;
            }

            return IsLiveSpecializedIndustrySpawnable(entity, prefab);
        }

        private void RememberPlayerPlacedSpawnables(ObjectToolOperationCommand operation, long now)
        {
            if (!IsSpecializedIndustryPlacement(operation)) return;
            PrunePlayerPlacedSpawnables(now);

            int remembered = 0;
            for (int i = 0; i < operation.Definitions.Length; i++)
            {
                ObjectToolDefinitionIntent definition = operation.Definitions[i];
                Entity prefab;
                if (definition == null || definition.Kind != ObjectToolDefinitionKind.Object ||
                    definition.PrefabIsNull ||
                    !_prefabIndex.TryResolve(definition.PrefabName, out prefab) ||
                    !IsAllowedSpecializedSpawnable(operation, i, prefab)) continue;

                var candidate = new PlayerPlacedSpawnableCreation
                {
                    Prefab = prefab,
                    Position = new float3(definition.Object.PosX, definition.Object.PosY,
                        definition.Object.PosZ),
                    Rotation = math.normalizesafe(new float4(definition.Object.RotX,
                            definition.Object.RotY, definition.Object.RotZ,
                            definition.Object.RotW),
                        new float4(0f, 0f, 0f, 1f)),
                    RandomSeed = unchecked((ushort)definition.RandomSeed),
                    ExpiryMs = now > 0 ? now + PlayerPlacedSpawnableLifetimeMs : long.MaxValue,
                };

                bool duplicate = false;
                for (int j = _playerPlacedSpawnableCreations.Count - 1; j >= 0; j--)
                {
                    PlayerPlacedSpawnableCreation existing =
                        _playerPlacedSpawnableCreations[j];
                    if (existing.Prefab != candidate.Prefab ||
                        existing.RandomSeed != candidate.RandomSeed ||
                        math.distancesq(existing.Position, candidate.Position) >
                        PlayerPlacedSpawnableMatchDistanceSq ||
                        math.abs(math.dot(existing.Rotation, candidate.Rotation)) <
                        PlayerPlacedSpawnableMatchRotationDot) continue;
                    _playerPlacedSpawnableCreations[j] = candidate;
                    duplicate = true;
                    break;
                }
                if (duplicate) continue;

                if (_playerPlacedSpawnableCreations.Count >=
                    MaxPlayerPlacedSpawnableCreations)
                    _playerPlacedSpawnableCreations.RemoveAt(0);
                _playerPlacedSpawnableCreations.Add(candidate);
                remembered++;
            }

            if (remembered > 0)
                Diagnostics.FlightRecorder.Note("player-placed spawnable guard armed=" +
                                                  remembered);
        }

        private void PrunePlayerPlacedSpawnables(long now)
        {
            if (now <= 0) return;
            for (int i = _playerPlacedSpawnableCreations.Count - 1; i >= 0; i--)
                if (_playerPlacedSpawnableCreations[i].ExpiryMs <= now)
                    _playerPlacedSpawnableCreations.RemoveAt(i);
        }

        private void ClearPlayerPlacedSpawnables()
        {
            _playerPlacedSpawnableCreations.Clear();
        }
    }
}
