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
    /// complete spatial upgrades are owned by the atomic object-lifecycle transaction; this legacy
    /// command remains only for upgrades with no owned geometry. Host charges via
    /// <see cref="ConstructionCharger"/>.
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
            RealizeIncoming(session, now);
        }

        private void CaptureNewUpgrades(MultiplayerSession session, long now)
        {
            if (_buildSync.NativeLifecycleCapturedThisFrame ||
                _buildSync.HasPendingSpecializedAreaCapture ||
                World.GetOrCreateSystemManaged<Net.NetSyncSystem>().DidCommitObjectGraphThisFrame) return;
            if (_createdUpgrades.IsEmptyIgnoreFilter) return;

            NativeArray<Entity> entities = _createdUpgrades.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity entity = entities[i];
                    Entity prefab = EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab;
                    string name = _prefabSystem.GetPrefabName(prefab);
                    if (string.IsNullOrEmpty(name)) continue;

                    // Both tool entry points for an upgrade require ServiceUpgradeData on the prefab
                    // (UpgradeToolSystem.TrySetPrefab and the object tool's Upgrade mode), so anything
                    // without it was not placed by a player - a storage yard's container piles, for
                    // instance, are content the simulation spawns into the lot and would otherwise be
                    // published as upgrades, frame after frame, until the inbox overflowed.
                    if (!EntityManager.HasComponent<ServiceUpgradeData>(prefab)) continue;

                    // An extractor/storage lot is drawn by the player, so this command cannot describe
                    // it; the receiver would rebuild the extension around a prefab-default polygon.
                    // Those travel as the complete native two-tool transaction instead.
                    if (!_buildSync.UpgradeOwnedGraphIsPrefabDeterministic(prefab)) continue;

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
                    string ownerName = _prefabSystem.GetPrefabName(EntityManager.GetComponentData<PrefabRef>(owner).m_Prefab);
                    if (string.IsNullOrEmpty(ownerName)) continue;
                    float3 ownerPos = EntityManager.GetComponentData<Transform>(owner).m_Position;
                    int randomSeed = EntityManager.HasComponent<PseudoRandomSeed>(entity)
                        ? EntityManager.GetComponentData<PseudoRandomSeed>(entity).m_Seed
                        : (int)(math.hash(transform.m_Position) & 0xffffu);

                    // Owner + prefab + transform + the placing tool's seed is exactly the input set
                    // the game's own definition generator takes, so the receiver re-runs that
                    // generator against its own geometry. Shipping the sender's finished 130-250
                    // definition batch instead meant resolving every one of its entity references
                    // here, which is both slow and impossible whenever the two machines have a road
                    // subdivided differently.
                    var command = new UpgradePlacementCommand
                    {
                        PrefabName = name,
                        OwnerPrefabName = ownerName,
                        OwnerX = ownerPos.x, OwnerY = ownerPos.y, OwnerZ = ownerPos.z,
                        PosX = transform.m_Position.x, PosY = transform.m_Position.y, PosZ = transform.m_Position.z,
                        RotX = transform.m_Rotation.value.x, RotY = transform.m_Rotation.value.y,
                        RotZ = transform.m_Rotation.value.z, RotW = transform.m_Rotation.value.w,
                        RandomSeed = randomSeed,
                        ToolRandomSeed = _buildSync.AppliedLifecycleToolSeed,
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
                QueueOwnerRetry(command, message.OriginPlayerId, now);
            }
        }

        private void QueueOwnerRetry(UpgradePlacementCommand command, int origin, long now)
        {
            if (_ownerRetry.Count >= MaxPendingOwners) _ownerRetry.RemoveAt(0);
            _ownerRetry.Add((command, origin, now + OwnerRetryWindowMs));
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

            // Only a player-placeable service upgrade may be built through this channel: both tool
            // entry points require ServiceUpgradeData, so anything else is simulation-owned content
            // (a storage yard's container piles) or an outright forgery, and would be created here
            // without the links it needs. One test refuses the whole class.
            if (!EntityManager.HasComponent<ServiceUpgradeData>(prefab))
            {
                Mod.log.Warn("[MP] UpgradeSync realize: '" + command.PrefabName +
                             "' is not a service upgrade; skipping.");
                return true;
            }

            var ownerPos = new float3(command.OwnerX, command.OwnerY, command.OwnerZ);
            Entity owner = FindOwner(ownerPrefab, ownerPos);
            if (owner == Entity.Null) return false;

            var position = new float3(command.PosX, command.PosY, command.PosZ);
            var rotation = new quaternion(command.RotX, command.RotY, command.RotZ, command.RotW);

            // Reliable retries and reconnect boundaries must not duplicate an already-realized
            // extension. Ownership is part of the identity because two nearby service buildings can
            // legitimately use the same upgrade prefab.
            if (FindUpgrade(prefab, position, owner) != Entity.Null) return true;

            // Preferred path: let the game generate the transaction. It produces the host building's
            // re-commit, the road it attaches to, re-commits of the host's existing sub-nets with
            // their end nodes preserved, the removal of host sub-nets the new footprint covers, and
            // the lot-surface snapping that makes the extension's own paths meet the street. None of
            // that is reproducible by creating the extension alone.
            UpgradePlacementCommand retained = command;
            int retainedOrigin = origin;
            BuildSyncSystem.NativeDeriveResult derived = _buildSync.TryDeriveObjectTransaction(
                prefab, owner, Entity.Null, Entity.Null, position, rotation, 0f,
                command.ToolRandomSeed,
                "upgrade " + command.PrefabName,
                () => QueueOwnerRetry(retained, retainedOrigin, Mod.Service != null ? Mod.Service.NowMs : 0),
                null);
            if (derived == BuildSyncSystem.NativeDeriveResult.Busy) return false;
            if (derived == BuildSyncSystem.NativeDeriveResult.Armed)
            {
                _guard.Mark(UpgradeKey(command.PrefabName, position), now);
                ConstructionCharger.ChargeUpgrade(EntityManager, prefab, command.PrefabName);
                Mod.Verbose("[MP] UpgradeSync realize: derived '" + command.PrefabName + "' on '" +
                             command.OwnerPrefabName + "' from player " + origin + ".");
                return true;
            }
            if (derived == BuildSyncSystem.NativeDeriveResult.Failed) return true;

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

        /// <summary>
        /// Create the top-level service extension with a direct, already-live owner, then rebuild the
        /// extension's own owned graph (connection sub-nets, lot sub-areas) from its prefab — the same
        /// deterministic recipe a building placement uses, so the peer gets the complete extension in
        /// one atomic realize instead of resolving the sender's 100+ entity batch.
        /// </summary>
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

            // The extension's own connection nets / lot areas, owned by the extension (which is in
            // turn owned by the building). RealizeOwnedSubElements is a no-op for extensions with no
            // owned buffers and validates every sub-prefab's game data before emitting, so a missing
            // component is skipped with a warning rather than crashing the native generators.
            // lotOwner = the host building: an extension's paths are laid on the host's lot surface,
            // not at their prefab-local height. That is what the tools do (they pass the building
            // being upgraded as the lot entity) and it is what makes those paths meet the street.
            var random = new Unity.Mathematics.Random((uint)math.max(1, randomSeed));
            _buildSync.RealizeOwnedSubElements(prefab, new OwnerDefinition
            {
                m_Prefab = prefab,
                m_Position = position,
                m_Rotation = rotation,
            }, ref random, lotOwner: owner);

            RederiveHostConnections(owner);
        }

        /// <summary>
        /// Re-derive what the tools re-commit as part of the same transaction. Placing an upgrade
        /// locally does more than add the extension: the host building is re-committed (its
        /// definition carries <see cref="CreationFlags.Upgrade"/>, so the apply pass tags the
        /// building <see cref="Updated"/>), and the road it connects to is re-committed together
        /// with every edge meeting at that road's end nodes.
        ///
        /// Those re-commits are what makes the building's paths re-derive their junction with the
        /// street. Creating only the extension leaves the host and the road untouched, so the new
        /// connector is built while the street keeps its old derivation - paths that sit beside the
        /// road without joining it. Tagging is enough here: the systems that own that derivation
        /// (road connection, sub-net references, composition) all key on Updated.
        /// </summary>
        private void RederiveHostConnections(Entity owner)
        {
            MarkUpdated(owner);
            if (!EntityManager.HasComponent<Building>(owner)) return;

            Entity roadEdge = EntityManager.GetComponentData<Building>(owner).m_RoadEdge;
            if (roadEdge == Entity.Null || !EntityManager.Exists(roadEdge) ||
                EntityManager.HasComponent<Deleted>(roadEdge) ||
                !EntityManager.HasComponent<global::Game.Net.Edge>(roadEdge)) return;

            // Tagging each end node also tags every edge meeting there, which is the same reach the
            // tools use when the placed object is a building that may not sit on road area.
            global::Game.Net.Edge ends =
                EntityManager.GetComponentData<global::Game.Net.Edge>(roadEdge);
            NetAttachment.TagParentUpdated(EntityManager, roadEdge);
            NetAttachment.TagParentUpdated(EntityManager, ends.m_Start);
            NetAttachment.TagParentUpdated(EntityManager, ends.m_End);
        }

        private void MarkUpdated(Entity entity)
        {
            if (entity == Entity.Null || !EntityManager.Exists(entity) ||
                EntityManager.HasComponent<Deleted>(entity) ||
                EntityManager.HasComponent<Updated>(entity)) return;
            EntityManager.AddComponent<Updated>(entity);
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
                    if (math.distancesq(candidatePosition, position) <= 4f) return candidate;
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
