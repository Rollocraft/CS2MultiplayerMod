using System.Collections.Concurrent;
using System.Collections.Generic;
using Game;
using Game.Areas;
using Game.Common;
using Game.Prefabs;
using Game.Tools;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;

using CS2MultiplayerMod.Game.Sync.Infrastructure;
using CS2MultiplayerMod.Game.Sync.Commands;
namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Replicates player-drawn areas and their redraws. Building-owned extractor/storage lots
    /// use owner-qualified snapshots so their draggable borders can be repaired without replacing
    /// the owning building. Map tiles and other building-owned lots remain excluded.
    /// </summary>
    public partial class AreaSyncSystem : GameSystemBase
    {
        private const long EditScanIntervalMs = 1000;
        private const long OwnedAreaRetryWindowMs = 10000;
        private const int MaxPendingOwnedAreas = 256;

        private readonly ConcurrentQueue<SimulationCommandMessage> _incoming =
            new ConcurrentQueue<SimulationCommandMessage>();
        private readonly ReplicationGuard _guard = new ReplicationGuard();
        private readonly List<(OwnedAreaSnapshotCommand command, int origin, long deadline)>
            _ownedAreaRetry =
                new List<(OwnedAreaSnapshotCommand, int, long)>();
        private Dictionary<Entity, float3[]> _knownRings = new Dictionary<Entity, float3[]>();
        private Dictionary<Entity, float3[]> _nextRings = new Dictionary<Entity, float3[]>();
        private long _lastEditScanMs;

        private PrefabSystem _prefabSystem;
        private BuildSyncSystem _buildSync;
        private PrefabIndex _prefabIndex;
        private EntityQuery _createdAreas;
        private EntityQuery _deletedAreas;
        private EntityQuery _liveAreas;
        private EntityQuery _ownedSpecializedAreas;
        private EntityQuery _ownedAreaOwners;
        private CommandObserver _observer;

        protected override void OnCreate()
        {
            base.OnCreate();

            Mod.log.Info(nameof(AreaSyncSystem) + " ready.");
            // A specialized placement's lot must not be published ahead of its building, which
            // BuildSync holds until the polygon closes (see the redraw scan).
            _buildSync = World.GetOrCreateSystemManaged<BuildSyncSystem>();
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _prefabIndex = new PrefabIndex(_prefabSystem, GetEntityQuery(ComponentType.ReadOnly<PrefabData>()));

            _createdAreas = GetEntityQuery(AreaQuery(ComponentType.ReadOnly<Created>()));
            _deletedAreas = GetEntityQuery(AreaQuery(ComponentType.ReadOnly<Deleted>()));
            _liveAreas = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Area>(),
                    ComponentType.ReadOnly<Node>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Owner>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<MapTile>(),
                },
            });
            _ownedSpecializedAreas = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Area>(),
                    ComponentType.ReadOnly<Node>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Owner>(),
                },
                Any = new[]
                {
                    ComponentType.ReadOnly<Extractor>(),
                    ComponentType.ReadOnly<Storage>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<MapTile>(),
                },
            });
            _ownedAreaOwners = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<global::Game.Objects.Object>(),
                    ComponentType.ReadOnly<global::Game.Objects.Transform>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<global::Game.Areas.SubArea>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Owner>(),
                },
            });

            if (Mod.Service != null)
            {
                _observer = new CommandObserver(_incoming, AreaCreateCommand.Id,
                    AreaUpdateCommand.Id, AreaDeleteCommand.Id,
                    OwnedAreaSnapshotCommand.Id);
                Mod.Service.Session.AddObserver(_observer);
            }
        }

        private static EntityQueryDesc AreaQuery(ComponentType lifecycleTag) => new EntityQueryDesc
        {
            All = new[]
            {
                lifecycleTag,
                ComponentType.ReadOnly<Area>(),
                ComponentType.ReadOnly<Node>(),
                ComponentType.ReadOnly<PrefabRef>(),
            },
            None = new[]
            {
                ComponentType.ReadOnly<Temp>(),
                // Owned areas (building lots) live and die with their owner on both sides.
                ComponentType.ReadOnly<Owner>(),
                ComponentType.ReadOnly<MapTile>(),
            },
        };

        protected override void OnDestroy()
        {
            if (_observer != null && Mod.Service != null)
                Mod.Service.Session.RemoveObserver(_observer);
            base.OnDestroy();
        }

        protected override void OnUpdate()
        {
            using (Diagnostics.SyncProfiler.Measure("AreaSync"))
            {
                MultiplayerService service = Mod.Service;
                if (service == null) return;

                MultiplayerSession session = service.Session;
                if (!service.GameplaySyncReady)
                {
                    if (_knownRings.Count > 0) _knownRings.Clear();
                    _ownedAreaRetry.Clear();
                    SyncInbox.Clear(_incoming);
                    return;
                }

                long now = service.NowMs;
                _guard.Prune(now);
                CaptureCreated(session, now);
                CaptureDeleted(session, now);
                ScanForEdits(session, now);
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
                _ownedAreaRetry.Clear();
                SyncInbox.Clear(_incoming);
                return;
            }

            List<AreaDeleteCommand> deletes = null;
            long now = service.NowMs;
            RetryOwnedAreaSnapshots(now);
            SimulationCommandMessage message;
            while (_incoming.TryDequeue(out message))
            {
                if (message.OriginPlayerId == session.LocalPlayerId) continue;
                try
                {
                    if (message.CommandId == AreaCreateCommand.Id)
                        RealizeCreate(AreaCreateCommand.Decode(message.Body), message.OriginPlayerId, now);
                    else if (message.CommandId == AreaUpdateCommand.Id)
                        RealizeUpdate(AreaUpdateCommand.Decode(message.Body), message.OriginPlayerId, now);
                    else if (message.CommandId == AreaDeleteCommand.Id)
                        (deletes ?? (deletes = new List<AreaDeleteCommand>())).Add(AreaDeleteCommand.Decode(message.Body));
                    else if (message.CommandId == OwnedAreaSnapshotCommand.Id)
                    {
                        OwnedAreaSnapshotCommand command =
                            OwnedAreaSnapshotCommand.Decode(message.Body);
                        if (!TryRealizeOwnedAreaSnapshot(command, message.OriginPlayerId, now))
                            QueueOwnedAreaRetry(command, message.OriginPlayerId,
                                now + OwnedAreaRetryWindowMs);
                    }
                }
                catch (System.Exception ex) { Mod.log.Warn("[MP] AreaSync: dropping malformed command: " + ex.Message); }
            }
            if (deletes != null) RealizeDeletes(deletes, now);
        }

        // ---- Polygon edits (redraws) -------------------------------------------










        private static string AreaKey(string prefabName, float3 firstNode) =>
            "area|" + ReplicationGuard.Key(prefabName, firstNode);

        private static string AreaDeleteKey(string prefabName, float3 firstNode) =>
            "areadel|" + ReplicationGuard.Key(prefabName, firstNode);

        private static string AreaUpdateKey(string prefabName, float3 centroid) =>
            "areaupd|" + ReplicationGuard.Key(prefabName, centroid);

        private static string OwnedAreaUpdateKey(string areaPrefabName,
            string ownerPrefabName, float3 ownerPosition) =>
            "ownedareaupd|" + ReplicationGuard.Key(
                areaPrefabName + "|" + ownerPrefabName, ownerPosition);

        private bool TryGetOwnedAreaIdentity(Entity area, out Entity areaPrefab,
            out Entity ownerPrefab,
            out global::Game.Objects.Transform ownerTransform)
        {
            areaPrefab = Entity.Null;
            ownerPrefab = Entity.Null;
            ownerTransform = default(global::Game.Objects.Transform);
            if (area == Entity.Null || !EntityManager.Exists(area) ||
                !EntityManager.HasComponent<Owner>(area) ||
                !EntityManager.HasComponent<PrefabRef>(area)) return false;

            areaPrefab = EntityManager.GetComponentData<PrefabRef>(area).m_Prefab;
            Entity topOwner;
            if (!IsSpecializedAreaPrefab(areaPrefab) ||
                !TryFindTopAreaOwner(area, out topOwner) || topOwner == Entity.Null ||
                !EntityManager.HasComponent<PrefabRef>(topOwner) ||
                !EntityManager.HasComponent<global::Game.Objects.Transform>(topOwner))
                return false;

            ownerPrefab = EntityManager.GetComponentData<PrefabRef>(topOwner).m_Prefab;
            if (!PrefabDeclaresOwnedArea(ownerPrefab, areaPrefab)) return false;
            ownerTransform =
                EntityManager.GetComponentData<global::Game.Objects.Transform>(topOwner);
            return true;
        }

        private bool TryFindTopAreaOwner(Entity entity, out Entity topOwner)
        {
            topOwner = Entity.Null;
            Entity cursor = entity;
            for (int depth = 0; depth < 64 && EntityManager.HasComponent<Owner>(cursor);
                 depth++)
            {
                Entity next = EntityManager.GetComponentData<Owner>(cursor).m_Owner;
                if (next == Entity.Null || next == cursor || !EntityManager.Exists(next))
                    return false;
                topOwner = next;
                cursor = next;
            }
            return cursor == entity || !EntityManager.HasComponent<Owner>(cursor);
        }

        private bool IsSpecializedAreaPrefab(Entity prefab)
        {
            return prefab != Entity.Null && EntityManager.Exists(prefab) &&
                   (EntityManager.HasComponent<ExtractorAreaData>(prefab) ||
                    EntityManager.HasComponent<StorageAreaData>(prefab));
        }

        private bool PrefabDeclaresOwnedArea(Entity ownerPrefab, Entity areaPrefab)
        {
            if (ownerPrefab == Entity.Null || areaPrefab == Entity.Null ||
                !EntityManager.Exists(ownerPrefab) ||
                !EntityManager.HasBuffer<global::Game.Prefabs.SubArea>(ownerPrefab))
                return false;

            DynamicBuffer<global::Game.Prefabs.SubArea> declared =
                EntityManager.GetBuffer<global::Game.Prefabs.SubArea>(
                    ownerPrefab, isReadOnly: true);
            for (int i = 0; i < declared.Length; i++)
            {
                Entity candidate = declared[i].m_Prefab;
                if (candidate == areaPrefab) return true;
                if (candidate == Entity.Null || !EntityManager.Exists(candidate) ||
                    !EntityManager.HasBuffer<PlaceholderObjectElement>(candidate)) continue;
                DynamicBuffer<PlaceholderObjectElement> placeholders =
                    EntityManager.GetBuffer<PlaceholderObjectElement>(
                        candidate, isReadOnly: true);
                for (int j = 0; j < placeholders.Length; j++)
                    if (placeholders[j].m_Object == areaPrefab) return true;
            }
            return false;
        }

    }
}
