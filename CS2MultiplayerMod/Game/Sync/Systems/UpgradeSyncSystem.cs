using System.Collections.Concurrent;
using Game;
using Game.Buildings;
using Game.Common;
using Game.Objects;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;

using CS2MultiplayerMod.Game.Sync.Infrastructure;
using CS2MultiplayerMod.Game.Sync.Commands;
namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Replicates service-building upgrades (<see cref="ServiceUpgrade"/>, <see cref="Extension"/>):
    /// detect Created + broadcast <see cref="UpgradePlacementCommand"/> with owner prefab+position.
    /// Realization first creates the owned extension, then emits its prefab-owned areas and access
    /// networks once the extension entity is queryable. Host charges via <see cref="ConstructionCharger"/>.
    /// </summary>
    public partial class UpgradeSyncSystem : GameSystemBase
    {
        private readonly ConcurrentQueue<SimulationCommandMessage> _incoming =
            new ConcurrentQueue<SimulationCommandMessage>();
        private readonly ReplicationGuard _guard = new ReplicationGuard();

        /// <summary>An upgrade can outrun the building it attaches to; hold it until the owner exists.</summary>
        private const long OwnerRetryWindowMs = 10000;

        /// <summary>Ceiling on the wait list, so a peer can never grow it without bound.</summary>
        private const int MaxPendingOwners = 256;

        private readonly System.Collections.Generic.List<(UpgradePlacementCommand cmd, int origin, long deadline)> _ownerRetry =
            new System.Collections.Generic.List<(UpgradePlacementCommand, int, long)>();

        private readonly System.Collections.Generic.List<(
            Entity prefab, Entity buildingOwner, float3 position, int seed, long deadline)>
            _ownedElementRetry = new System.Collections.Generic.List<(Entity, Entity, float3, int, long)>();

        private const float UpgradeMatchRadiusSq = 0.1f * 0.1f;
        private const float UpgradeMatchMaxDy = 0.25f;

        private PrefabSystem _prefabSystem;
        private PrefabIndex _prefabIndex;
        private BuildSyncSystem _buildSync;
        private EntityQuery _createdUpgrades;
        private EntityQuery _liveUpgrades;
        private EntityQuery _liveOwners;
        private CommandObserver _observer;

        protected override void OnCreate()
        {
            base.OnCreate();

            Mod.log.Info(nameof(UpgradeSyncSystem) + " ready.");
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _prefabIndex = new PrefabIndex(_prefabSystem, GetEntityQuery(ComponentType.ReadOnly<PrefabData>()));
            _buildSync = World.GetOrCreateSystemManaged<BuildSyncSystem>();

            // Owned sub-objects created this frame that are genuine service upgrades —
            // Any{} keeps out the decorative props the game also parents to buildings.
            _createdUpgrades = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Created>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Transform>(),
                    ComponentType.ReadOnly<Owner>(),
                },
                Any = new[]
                {
                    ComponentType.ReadOnly<global::Game.Buildings.ServiceUpgrade>(),
                    ComponentType.ReadOnly<Extension>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                },
            });

            _liveUpgrades = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Transform>(),
                    ComponentType.ReadOnly<Owner>(),
                },
                Any = new[]
                {
                    ComponentType.ReadOnly<global::Game.Buildings.ServiceUpgrade>(),
                    ComponentType.ReadOnly<Extension>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                },
            });

            // Candidate owner buildings for realizing a remote upgrade.
            _liveOwners = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Building>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Transform>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Owner>(),
                    ComponentType.ReadOnly<Deleted>(),
                },
            });

            if (Mod.Service != null)
            {
                _observer = new CommandObserver(_incoming, UpgradePlacementCommand.Id);
                Mod.Service.Session.AddObserver(_observer);
            }
            SyncInbox.RegisterDrain(DrainQueue);
        }

        protected override void OnDestroy()
        {
            SyncInbox.UnregisterDrain(DrainQueue);
            if (_observer != null && Mod.Service != null)
                Mod.Service.Session.RemoveObserver(_observer);
            base.OnDestroy();
        }

        private void DrainQueue()
        {
            SyncInbox.Clear(_incoming);
            _ownerRetry.Clear();
            _ownedElementRetry.Clear();
        }

        protected override void OnUpdate()
        {
            MultiplayerService service = Mod.Service;
            if (service == null) return;

            MultiplayerSession session = service.Session;
            if (!service.GameplaySyncReady) return;

            long now = service.NowMs;
            _guard.Prune(now);
            CaptureNewUpgrades(session, now);
        }

        /// <summary>Called by <see cref="SyncRealizeSystem"/> during ToolUpdate (see there for why).</summary>
        public void RealizePending()
        {
            MultiplayerService service = Mod.Service;
            if (service == null) return;

            MultiplayerSession session = service.Session;
            if (!service.GameplaySyncReady)
            {
                DrainQueue();
                return;
            }
            long now = service.NowMs;
            RealizePendingOwnedElements(now);
            RealizeIncoming(session, now);
        }

        private void CaptureNewUpgrades(MultiplayerSession session, long now)
        {
            if (_createdUpgrades.IsEmptyIgnoreFilter) return;

            NativeArray<Entity> entities = _createdUpgrades.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity entity = entities[i];
                    string name = _prefabSystem.GetPrefabName(EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab);
                    if (string.IsNullOrEmpty(name)) continue;

                    Transform transform = EntityManager.GetComponentData<Transform>(entity);
                    if (_guard.Consume(UpgradeKey(name, transform.m_Position), now)) continue;

                    // The owner travels as prefab + position so the receiver can find its
                    // own building entity.
                    Entity owner = EntityManager.GetComponentData<Owner>(entity).m_Owner;
                    if (!EntityManager.HasComponent<PrefabRef>(owner) ||
                        !EntityManager.HasComponent<Transform>(owner)) continue;

                    // An owner Created THIS frame is a brand-new building whose integral sub-objects
                    // (a helipad's airspace, a fire station's parking) auto-spawn WITH it — not a
                    // player-applied upgrade. Replicating them re-runs the spawn on the receiver
                    // (which already made its own with the building) and echoes a duplicate. Only a
                    // sub-object attached to a PRE-EXISTING building is a real upgrade.
                    if (EntityManager.HasComponent<Created>(owner)) continue;

                    // Growables grow their own extensions (mixed-use storefronts) as they level;
                    // players can only upgrade placed service buildings, so a zone-spawnable owner
                    // marks simulation churn the receiver's own zone growth reproduces.
                    Entity ownerPrefabEntity = EntityManager.GetComponentData<PrefabRef>(owner).m_Prefab;
                    if (EntityManager.HasComponent<SpawnableObjectData>(ownerPrefabEntity)) continue;

                    string ownerName = _prefabSystem.GetPrefabName(ownerPrefabEntity);
                    if (string.IsNullOrEmpty(ownerName)) continue;
                    float3 ownerPos = EntityManager.GetComponentData<Transform>(owner).m_Position;
                    int randomSeed = EntityManager.HasComponent<PseudoRandomSeed>(entity)
                        ? EntityManager.GetComponentData<PseudoRandomSeed>(entity).m_Seed
                        : (int)(math.hash(transform.m_Position) & 0xffffu);

                    var command = new UpgradePlacementCommand
                    {
                        PrefabName = name,
                        OwnerPrefabName = ownerName,
                        OwnerX = ownerPos.x, OwnerY = ownerPos.y, OwnerZ = ownerPos.z,
                        PosX = transform.m_Position.x, PosY = transform.m_Position.y, PosZ = transform.m_Position.z,
                        RotX = transform.m_Rotation.value.x, RotY = transform.m_Rotation.value.y,
                        RotZ = transform.m_Rotation.value.z, RotW = transform.m_Rotation.value.w,
                        RandomSeed = randomSeed,
                    };
                    session.SendCommand(0, UpgradePlacementCommand.Id, command.Encode());
                    Mod.Verbose("[MP] UpgradeSync captured '" + name + "' on '" + ownerName + "'.");
                }
            }
            finally
            {
                entities.Dispose();
            }
        }

        private void RealizeIncoming(MultiplayerSession session, long now)
        {
            // Retry upgrades whose owner building was missing last cycle before draining new ones.
            for (int i = _ownerRetry.Count - 1; i >= 0; i--)
            {
                var pending = _ownerRetry[i];
                if (TryRealize(pending.cmd, pending.origin, now)) { _ownerRetry.RemoveAt(i); continue; }
                if (now >= pending.deadline)
                {
                    _ownerRetry.RemoveAt(i);
                    Mod.log.Warn("[MP] UpgradeSync realize: no local '" + pending.cmd.OwnerPrefabName +
                                 "' after " + (OwnerRetryWindowMs / 1000) + " s to attach '" +
                                 pending.cmd.PrefabName + "'; dropping.");
                }
            }

            SimulationCommandMessage message;
            while (_incoming.TryDequeue(out message))
            {
                if (message.OriginPlayerId == session.LocalPlayerId) continue;

                UpgradePlacementCommand command;
                try { command = UpgradePlacementCommand.Decode(message.Body); }
                catch (System.Exception ex) { Mod.log.Warn("[MP] UpgradeSync: dropping malformed command: " + ex.Message); continue; }

                if (TryRealize(command, message.OriginPlayerId, now)) continue;

                // Its owner building may simply not have realized here yet — wait for it.
                if (_ownerRetry.Count >= MaxPendingOwners) _ownerRetry.RemoveAt(0);
                _ownerRetry.Add((command, message.OriginPlayerId, now + OwnerRetryWindowMs));
            }
        }

        /// <summary>
        /// Attempt one upgrade; false when its owner building is not (yet) local, so the caller can
        /// retry. An unknown prefab is a hard drop (returns true — nothing to wait for).
        /// </summary>
        private bool TryRealize(UpgradePlacementCommand command, int origin, long now)
        {
            Entity prefab, ownerPrefab;
            if (!_prefabIndex.TryResolve(command.PrefabName, out prefab) ||
                !_prefabIndex.TryResolve(command.OwnerPrefabName, out ownerPrefab))
            {
                Mod.log.Warn("[MP] UpgradeSync realize: unknown prefab '" + command.PrefabName +
                             "'/'" + command.OwnerPrefabName + "'; skipping.");
                return true;
            }

            // Mirrors the capture filter: an upgrade "on" a zone-spawnable owner can only be a
            // peer's capture leak of its own zone growth — realizing it stacks a duplicate
            // extension into a building this machine's growth already populates.
            if (EntityManager.HasComponent<SpawnableObjectData>(ownerPrefab))
            {
                Mod.log.Warn("[MP] UpgradeSync realize: refusing upgrade '" + command.PrefabName +
                             "' on zone-spawnable '" + command.OwnerPrefabName + "' from player " + origin + ".");
                return true;
            }

            var ownerPos = new float3(command.OwnerX, command.OwnerY, command.OwnerZ);
            Entity owner = FindOwner(ownerPrefab, ownerPos);
            if (owner == Entity.Null) return false;

            var position = new float3(command.PosX, command.PosY, command.PosZ);
            var rotation = new quaternion(math.normalizesafe(
                new float4(command.RotX, command.RotY, command.RotZ, command.RotW),
                new float4(0f, 0f, 0f, 1f)));

            // Reliable retries and reconnect boundaries must not duplicate an already-realized
            // extension. Ownership is part of the identity because two nearby service buildings can
            // legitimately use the same upgrade prefab.
            if (FindUpgrade(prefab, position, owner) != Entity.Null) return true;

            _guard.Mark(UpgradeKey(command.PrefabName, position), now);
            try
            {
                RealizeUpgrade(prefab, owner, position, rotation,
                    EntityManager.GetComponentData<Transform>(owner), command.RandomSeed);
                ConstructionCharger.ChargeUpgrade(EntityManager, prefab, command.PrefabName);
                Mod.Verbose("[MP] UpgradeSync realize: attached '" + command.PrefabName + "' to '" +
                             command.OwnerPrefabName + "' from player " + origin + ".");
            }
            catch (System.Exception ex)
            {
                Mod.log.Error("[MP] UpgradeSync realize FAILED for '" + command.PrefabName + "': " + ex);
            }
            return true;
        }

        private Entity FindOwner(Entity ownerPrefab, float3 ownerPos)
        {
            NativeArray<Entity> candidates = _liveOwners.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < candidates.Length; i++)
                {
                    if (EntityManager.GetComponentData<PrefabRef>(candidates[i]).m_Prefab != ownerPrefab) continue;
                    float3 pos = EntityManager.GetComponentData<Transform>(candidates[i]).m_Position;
                    if (math.distancesq(pos, ownerPos) <= 4f) return candidates[i];
                }
            }
            finally
            {
                candidates.Dispose();
            }
            return Entity.Null;
        }

        /// <summary>Create the top-level service extension with a direct, already-live owner.</summary>
        private void RealizeUpgrade(Entity prefab, Entity owner, float3 position, quaternion rotation,
            Transform ownerTransform, int randomSeed)
        {
            Entity definition = EntityManager.CreateEntity();
            EntityManager.AddComponentData(definition, new CreationDefinition
            {
                m_Prefab = prefab,
                m_Owner = owner,
                m_RandomSeed = randomSeed,
                m_Flags = CreationFlags.Permanent,
            });
            // World transform travels on the wire; the local one (relative to the owner)
            // is derived here. m_ParentMesh = -1 means "attached to the building itself,
            // not one of its sub-meshes" — flagged for in-game tuning.
            quaternion inverseOwner = math.inverse(ownerTransform.m_Rotation);
            EntityManager.AddComponentData(definition, new ObjectDefinition
            {
                m_Position = position,
                m_Rotation = rotation,
                m_LocalPosition = math.mul(inverseOwner, position - ownerTransform.m_Position),
                m_LocalRotation = math.mul(inverseOwner, rotation),
                m_ParentMesh = EntityManager.HasComponent<BuildingData>(prefab) ? -1 : 0,
                m_Scale = new float3(1f, 1f, 1f),
                m_Intensity = 1f,
                m_Probability = 100,
                m_PrefabSubIndex = -1,
            });
            EntityManager.AddComponent<Updated>(definition);
            EntityManager.AddComponent<Deleted>(definition);

            if (EntityManager.HasBuffer<SubNet>(prefab) || EntityManager.HasBuffer<SubArea>(prefab))
            {
                if (_ownedElementRetry.Count >= MaxPendingOwners) _ownedElementRetry.RemoveAt(0);
                long now = Mod.Service != null ? Mod.Service.NowMs : 0;
                _ownedElementRetry.Add((prefab, owner, position, randomSeed,
                    now + OwnerRetryWindowMs));
            }
        }

        private void RealizePendingOwnedElements(long now)
        {
            for (int i = _ownedElementRetry.Count - 1; i >= 0; i--)
            {
                var pending = _ownedElementRetry[i];
                Entity upgrade = FindUpgrade(pending.prefab, pending.position,
                    pending.buildingOwner);
                if (upgrade != Entity.Null)
                {
                    try
                    {
                        var random = new Unity.Mathematics.Random((uint)math.max(1, pending.seed));
                        _buildSync.RealizeOwnedSubElements(pending.prefab, upgrade,
                            EntityManager.GetComponentData<Transform>(upgrade), ref random);
                    }
                    catch (System.Exception ex)
                    {
                        Mod.log.Error("[MP] UpgradeSync owned-element realization FAILED: " + ex);
                    }
                    _ownedElementRetry.RemoveAt(i);
                }
                else if (now >= pending.deadline)
                {
                    _ownedElementRetry.RemoveAt(i);
                    Mod.log.Warn("[MP] UpgradeSync: created extension did not become queryable; " +
                                 "owned access elements were dropped.");
                }
            }
        }

        private Entity FindUpgrade(Entity prefab, float3 position, Entity expectedOwner)
        {
            NativeArray<Entity> candidates = _liveUpgrades.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < candidates.Length; i++)
                {
                    Entity candidate = candidates[i];
                    if (EntityManager.GetComponentData<PrefabRef>(candidate).m_Prefab != prefab) continue;
                    if (expectedOwner != Entity.Null &&
                        EntityManager.GetComponentData<Owner>(candidate).m_Owner != expectedOwner) continue;
                    float3 candidatePosition = EntityManager.GetComponentData<Transform>(candidate).m_Position;
                    if (math.distancesq(candidatePosition.xz, position.xz) <= UpgradeMatchRadiusSq &&
                        math.abs(candidatePosition.y - position.y) <= UpgradeMatchMaxDy) return candidate;
                }
            }
            finally
            {
                candidates.Dispose();
            }
            return Entity.Null;
        }

        private static string UpgradeKey(string prefabName, float3 position) =>
            "upg|" + ReplicationGuard.Key(prefabName, position);

    }
}
