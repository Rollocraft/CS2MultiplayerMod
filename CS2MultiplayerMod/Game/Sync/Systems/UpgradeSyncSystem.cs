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
    /// Realize by finding owner building, spawn <see cref="CreationDefinition"/> with <c>m_Owner</c>
    /// and Permanent|Attach|Upgrade flags. Host charges via <see cref="ConstructionCharger"/>.
    /// </summary>
    public partial class UpgradeSyncSystem : GameSystemBase
    {
        private readonly ConcurrentQueue<SimulationCommandMessage> _incoming =
            new ConcurrentQueue<SimulationCommandMessage>();
        private readonly ReplicationGuard _guard = new ReplicationGuard();

        private PrefabSystem _prefabSystem;
        private PrefabIndex _prefabIndex;
        private EntityQuery _createdUpgrades;
        private EntityQuery _liveOwners;
        private CommandObserver _observer;

        protected override void OnCreate()
        {
            base.OnCreate();

            Mod.log.Info(nameof(UpgradeSyncSystem) + " ready.");
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _prefabIndex = new PrefabIndex(_prefabSystem, GetEntityQuery(ComponentType.ReadOnly<PrefabData>()));

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
        }

        protected override void OnDestroy()
        {
            if (_observer != null && Mod.Service != null)
                Mod.Service.Session.RemoveObserver(_observer);
            base.OnDestroy();
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
            if (!service.GameplaySyncReady) return;
            RealizeIncoming(session, service.NowMs);
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
                    string ownerName = _prefabSystem.GetPrefabName(EntityManager.GetComponentData<PrefabRef>(owner).m_Prefab);
                    if (string.IsNullOrEmpty(ownerName)) continue;
                    float3 ownerPos = EntityManager.GetComponentData<Transform>(owner).m_Position;

                    var command = new UpgradePlacementCommand
                    {
                        PrefabName = name,
                        OwnerPrefabName = ownerName,
                        OwnerX = ownerPos.x, OwnerY = ownerPos.y, OwnerZ = ownerPos.z,
                        PosX = transform.m_Position.x, PosY = transform.m_Position.y, PosZ = transform.m_Position.z,
                        RotX = transform.m_Rotation.value.x, RotY = transform.m_Rotation.value.y,
                        RotZ = transform.m_Rotation.value.z, RotW = transform.m_Rotation.value.w,
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
            SimulationCommandMessage message;
            while (_incoming.TryDequeue(out message))
            {
                if (message.OriginPlayerId == session.LocalPlayerId) continue;

                UpgradePlacementCommand command;
                try { command = UpgradePlacementCommand.Decode(message.Body); }
                catch (System.Exception ex) { Mod.log.Warn("[MP] UpgradeSync: dropping malformed command: " + ex.Message); continue; }

                Entity prefab, ownerPrefab;
                if (!_prefabIndex.TryResolve(command.PrefabName, out prefab) ||
                    !_prefabIndex.TryResolve(command.OwnerPrefabName, out ownerPrefab))
                {
                    Mod.log.Warn("[MP] UpgradeSync realize: unknown prefab '" + command.PrefabName +
                                 "'/'" + command.OwnerPrefabName + "'; skipping.");
                    continue;
                }

                var ownerPos = new float3(command.OwnerX, command.OwnerY, command.OwnerZ);
                Entity owner = FindOwner(ownerPrefab, ownerPos);
                if (owner == Entity.Null)
                {
                    Mod.log.Warn("[MP] UpgradeSync realize: no local '" + command.OwnerPrefabName +
                                 "' near (" + ownerPos.x.ToString("F0") + "," + ownerPos.z.ToString("F0") +
                                 ") to attach '" + command.PrefabName + "' to; skipping.");
                    continue;
                }

                var position = new float3(command.PosX, command.PosY, command.PosZ);
                var rotation = new quaternion(command.RotX, command.RotY, command.RotZ, command.RotW);

                _guard.Mark(UpgradeKey(command.PrefabName, position), now);
                try
                {
                    RealizeUpgrade(prefab, owner, position, rotation,
                        EntityManager.GetComponentData<Transform>(owner));
                    ConstructionCharger.ChargeUpgrade(EntityManager, prefab, command.PrefabName);
                    Mod.Verbose("[MP] UpgradeSync realize: attached '" + command.PrefabName + "' to '" +
                                 command.OwnerPrefabName + "' from player " + message.OriginPlayerId + ".");
                }
                catch (System.Exception ex)
                {
                    Mod.log.Error("[MP] UpgradeSync realize FAILED for '" + command.PrefabName + "': " + ex);
                }
            }
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

        /// <summary>
        /// Same definition recipe as <see cref="BuildSyncSystem"/>, plus <c>m_Owner</c> and
        /// the Attach|Upgrade flags so <c>GenerateObjectsSystem</c> parents the extension to
        /// the building (registering it as a service upgrade) instead of placing a loose prop.
        /// </summary>
        private void RealizeUpgrade(Entity prefab, Entity owner, float3 position, quaternion rotation, Transform ownerTransform)
        {
            Entity definition = EntityManager.CreateEntity();
            EntityManager.AddComponentData(definition, new CreationDefinition
            {
                m_Prefab = prefab,
                m_Owner = owner,
                m_RandomSeed = 0,
                m_Flags = CreationFlags.Permanent | CreationFlags.Attach | CreationFlags.Upgrade,
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
                m_ParentMesh = -1,
                m_Scale = new float3(1f, 1f, 1f),
                m_Probability = 100,
            });
            EntityManager.AddComponent<Updated>(definition);
            EntityManager.AddComponent<Deleted>(definition);
        }

        private static string UpgradeKey(string prefabName, float3 position) =>
            "upg|" + ReplicationGuard.Key(prefabName, position);

    }
}
