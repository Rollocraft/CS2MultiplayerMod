using Game.Buildings;
using Game.Common;
using Game.Objects;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Infrastructure;
using CS2MultiplayerMod.Game.Sync.Commands;
namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Replicates service-building upgrades as the tool's inputs; the receiver regenerates the
    /// transaction. Drawn-lot upgrades travel through the native object transaction. The host charges
    /// through <see cref="ConstructionCharger"/>.
    /// </summary>
    public partial class UpgradeSyncSystem : CommandSyncSystem, IRealizeStage
    {
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

        protected override void OnCreate()
        {
            base.OnCreate();

            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _prefabIndex = new PrefabIndex(_prefabSystem, GetEntityQuery(ComponentType.ReadOnly<PrefabData>()));
            _buildSync = World.GetOrCreateSystemManaged<BuildSyncSystem>();

            // Owned sub-objects that are real upgrades; Any excludes decorative props.
            _createdUpgrades = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<Created, PrefabRef, Transform, Owner>(),
                Any = SyncQuery.ReadOnly<global::Game.Buildings.ServiceUpgrade, Extension>(),
                None = SyncQuery.ReadOnly<Temp, Deleted>(),
            });

            _liveUpgrades = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<PrefabRef, Transform, Owner>(),
                Any = SyncQuery.ReadOnly<global::Game.Buildings.ServiceUpgrade, Extension>(),
                None = SyncQuery.ReadOnly<Temp, Deleted>(),
            });

            // Candidate owner buildings for realizing a remote upgrade.
            _liveOwners = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<Building, PrefabRef, Transform>(),
                None = SyncQuery.ReadOnly<Temp, Owner, Deleted>(),
            });

            ListenFor(new[] { UpgradePlacementCommand.Id });
        }

        protected override void DrainQueue()
        {
            SyncInbox.Clear(_incoming);
            _ownerRetry.Clear();
        }

        protected override void OnUpdate()
        {
            using (Diagnostics.SyncProfiler.Measure("UpgradeSync"))
            {
                MultiplayerService service = Mod.Service;
                if (service == null) return;

                MultiplayerSession session = service.Session;
                if (!service.GameplaySyncReady) return;

                long now = service.NowMs;
                _guard.Prune(now);
                CaptureNewUpgrades(session, now);
            }
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

                    // Both upgrade tools require ServiceUpgradeData; anything else is simulation lot content.
                    if (!EntityManager.HasComponent<ServiceUpgradeData>(prefab)) continue;

                    // A drawn lot cannot be described here; it travels as the native two-tool transaction.
                    if (!_buildSync.UpgradeOwnedGraphIsPrefabDeterministic(prefab)) continue;

                    Transform transform = EntityManager.GetComponentData<Transform>(entity);
                    if (_guard.Consume(UpgradeKey(name, transform.m_Position), now)) continue;

                    Entity owner = EntityManager.GetComponentData<Owner>(entity).m_Owner;
                    if (!EntityManager.HasComponent<PrefabRef>(owner) ||
                        !EntityManager.HasComponent<Transform>(owner)) continue;

                    // A new building's integral sub-objects spawn with it; only an addition to an existing building
                    // is an upgrade.
                    if (EntityManager.HasComponent<Created>(owner)) continue;
                    string ownerName = _prefabSystem.GetPrefabName(EntityManager.GetComponentData<PrefabRef>(owner).m_Prefab);
                    if (string.IsNullOrEmpty(ownerName)) continue;
                    float3 ownerPos = EntityManager.GetComponentData<Transform>(owner).m_Position;
                    int randomSeed = EntityManager.HasComponent<PseudoRandomSeed>(entity)
                        ? EntityManager.GetComponentData<PseudoRandomSeed>(entity).m_Seed
                        : (int)(math.hash(transform.m_Position) & 0xffffu);

                    // Owner, prefab, transform and tool seed are the generator's inputs; the receiver re-runs it.
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
                    SyncLog.Detail(LogTopic.Buildings, "UpgradeSync captured '" + name + "' on '" +
                        ownerName + "'.");
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
                    SyncLog.Warn(LogTopic.Buildings, "UpgradeSync realize: no local '" +
                        pending.cmd.OwnerPrefabName + "' after " + (OwnerRetryWindowMs / 1000) +
                        " s to attach '" + pending.cmd.PrefabName + "'; dropping.");
                }
            }

            while (_incoming.TryDequeue(out SimulationCommandMessage message))
            {
                if (message.OriginPlayerId == session.LocalPlayerId) continue;

                if (!CommandDecode.TryDecode(message, UpgradePlacementCommand.Decode, LogTopic.Buildings,
                        "UpgradeSync", out UpgradePlacementCommand command))
                    continue;

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

        /// <summary>False while the owner is not local yet; an unknown prefab is dropped (true).</summary>
        private bool TryRealize(UpgradePlacementCommand command, int origin, long now)
        {
            if (!_prefabIndex.TryResolve(command.PrefabName, out Entity prefab) ||
                !_prefabIndex.TryResolve(command.OwnerPrefabName, out Entity ownerPrefab))
            {
                SyncLog.Warn(LogTopic.Buildings, "UpgradeSync realize: unknown prefab '" +
                    command.PrefabName + "'/'" + command.OwnerPrefabName + "'; skipping.");
                return true;
            }

            // Only player-placeable upgrades; anything else is simulation content or a forgery.
            if (!EntityManager.HasComponent<ServiceUpgradeData>(prefab))
            {
                SyncLog.Warn(LogTopic.Buildings, "UpgradeSync realize: '" + command.PrefabName +
                    "' is not a service upgrade; skipping.");
                return true;
            }

            var ownerPos = new float3(command.OwnerX, command.OwnerY, command.OwnerZ);
            Entity owner = FindOwner(ownerPrefab, ownerPos);
            if (owner == Entity.Null) return false;

            var position = new float3(command.PosX, command.PosY, command.PosZ);
            var rotation = new quaternion(command.RotX, command.RotY, command.RotZ, command.RotW);

            // Idempotent across retries; the owner is part of the identity.
            if (FindUpgrade(prefab, position, owner) != Entity.Null) return true;

            // Preferred: the game's generator produces the host re-commit, road attachment, sub-net re-commits
            // and removals, and lot snapping, none of which creating the extension alone reproduces.
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
                TrackRemoteUpgradeOwner(owner, ownerPrefab);
                _guard.Mark(UpgradeKey(command.PrefabName, position), now);
                ConstructionCharger.ChargeUpgrade(EntityManager, prefab, command.PrefabName);
                SyncLog.Detail(LogTopic.Buildings, "UpgradeSync realize: derived '" +
                    command.PrefabName + "' on '" + command.OwnerPrefabName + "' from player " +
                    origin + ".");
                return true;
            }
            if (derived == BuildSyncSystem.NativeDeriveResult.Failed) return true;

            _guard.Mark(UpgradeKey(command.PrefabName, position), now);
            try
            {
                TrackRemoteUpgradeOwner(owner, ownerPrefab);
                RealizeUpgrade(prefab, owner, position, rotation,
                    EntityManager.GetComponentData<Transform>(owner), command.RandomSeed);
                ConstructionCharger.ChargeUpgrade(EntityManager, prefab, command.PrefabName);
                SyncLog.Detail(LogTopic.Buildings, "UpgradeSync realize: attached '" +
                    command.PrefabName + "' to '" + command.OwnerPrefabName + "' from player " +
                    origin + ".");
            }
            catch (System.Exception ex)
            {
                SyncLog.Error(LogTopic.Buildings, "UpgradeSync realize FAILED for '" +
                    command.PrefabName + "': " + ex);
            }
            return true;
        }

        private void TrackRemoteUpgradeOwner(Entity owner, Entity ownerPrefab)
        {
            if (owner == Entity.Null || !EntityManager.Exists(owner) ||
                EntityManager.HasComponent<Deleted>(owner) ||
                !EntityManager.HasComponent<Building>(owner) ||
                !EntityManager.HasComponent<Transform>(owner)) return;
            Transform transform = EntityManager.GetComponentData<Transform>(owner);
            Entity road = EntityManager.GetComponentData<Building>(owner).m_RoadEdge;
            bool connectedRoad = road != Entity.Null && EntityManager.Exists(road) &&
                                 !EntityManager.HasComponent<Deleted>(road);
            _buildSync.TrackRemoteBuilding(owner, ownerPrefab, transform.m_Position,
                transform.m_Rotation, connectedRoad, "upgrade owner");
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
        /// Fallback: the extension with a live owner, plus its owned graph from the prefab.
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
            // The wire carries the world transform; derive the owner-relative one. -1: attached to the building.
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

            // The extension's owned nets and areas, laid on the host's lot surface as the tools do.
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
        /// Tags what the tools re-commit with an upgrade: the host building and its road with every edge at
        /// that road's ends, so the new paths re-derive their junction with the street.
        /// </summary>
        private void RederiveHostConnections(Entity owner)
        {
            MarkUpdated(owner);
            if (!EntityManager.HasComponent<Building>(owner)) return;

            Entity roadEdge = EntityManager.GetComponentData<Building>(owner).m_RoadEdge;
            if (roadEdge == Entity.Null || !EntityManager.Exists(roadEdge) ||
                EntityManager.HasComponent<Deleted>(roadEdge) ||
                !EntityManager.HasComponent<global::Game.Net.Edge>(roadEdge)) return;

            // Tagging an end node also tags every edge meeting there.
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
