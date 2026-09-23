using System.Collections.Generic;
using Game;
using Game.City;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Infrastructure;
using CS2MultiplayerMod.Game.Sync.Commands;
namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Replicates dev-tree purchases. Remote unlocks go through <see cref="EndFrameBarrier"/> so they
    /// reach MainLoop; the host charges <see cref="DevTreePoints"/> so the snapshot does not refill the buyer.
    /// </summary>
    public partial class DevTreeSyncSystem : CommandSyncSystem
    {
        private readonly ReplicationGuard _guard = new ReplicationGuard();
        private readonly HashSet<string> _knownUnlocked = new HashSet<string>();
        private readonly Dictionary<string, Entity> _nodeByName = new Dictionary<string, Entity>();

        private PrefabSystem _prefabSystem;
        private DeferredPrefabUnlocker _unlocks;
        private EntityQuery _nodes;
        private EntityQuery _pointsQuery;
        private bool _initialized;

        protected override void OnCreate()
        {
            base.OnCreate();

            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _unlocks = new DeferredPrefabUnlocker(EntityManager);
            // DevTree nodes are prefab entities — IncludePrefab so the query finds them.
            _nodes = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<DevTreeNodeData>(),
                None = SyncQuery.ReadOnly<Temp>(),
                Options = EntityQueryOptions.IncludePrefab,
            });
            _pointsQuery = GetEntityQuery(ComponentType.ReadWrite<DevTreePoints>());
            ListenFor(new[] { DevTreePurchaseCommand.Id }, drainOnReload: false);
        }

        protected override void OnUpdate()
        {
            using (Diagnostics.SyncProfiler.Measure("DevTree"))
            {
                MultiplayerService service = Mod.Service;
                if (service == null) return;

                MultiplayerSession session = service.Session;
                if (!service.GameplaySyncReady)
                {
                    _initialized = false;
                    _unlocks.Reset();
                    return;
                }

                long now = service.NowMs;
                _guard.Prune(now);
                _unlocks.PruneCompleted();

                // Remote purchases first, so their unlocks are not diffed as local ones.
                ApplyIncoming(session, now);

                // The loaded save's unlocks are the baseline, never re-broadcast.
                if (!_initialized)
                {
                    SeedKnown();
                    _initialized = true;
                    return;
                }

                DetectLocalPurchases(session, now);
            }
        }

        private bool IsLocked(Entity node) =>
            EntityManager.HasComponent<Locked>(node) && EntityManager.IsComponentEnabled<Locked>(node);

        private void SeedKnown()
        {
            _knownUnlocked.Clear();
            NativeArray<Entity> nodes = _nodes.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < nodes.Length; i++)
                {
                    if (IsLocked(nodes[i])) continue;
                    string name = PrefabIndex.SafeName(_prefabSystem, nodes[i]);
                    if (!string.IsNullOrEmpty(name)) _knownUnlocked.Add(name);
                }
            }
            finally { nodes.Dispose(); }
        }

        private void DetectLocalPurchases(MultiplayerSession session, long now)
        {
            NativeArray<Entity> nodes = _nodes.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < nodes.Length; i++)
                {
                    string name = PrefabIndex.SafeName(_prefabSystem, nodes[i]);
                    if (string.IsNullOrEmpty(name)) continue;

                    bool unlocked = !IsLocked(nodes[i]);
                    bool known = _knownUnlocked.Contains(name);

                    if (unlocked && !known)
                    {
                        _knownUnlocked.Add(name);
                        if (_guard.Consume(NodeKey(name), now)) continue; // we applied it — no echo

                        var command = new DevTreePurchaseCommand { NodePrefabName = name };
                        session.SendCommand(0, DevTreePurchaseCommand.Id, command.Encode());
                        SyncLog.Detail(LogTopic.City, "DevTreeSync: broadcast purchase of '" + name +
                            "'.");
                    }
                    else if (!unlocked && known)
                    {
                        // Re-locked by a world resync: detect a future unlock again.
                        _knownUnlocked.Remove(name);
                    }
                }
            }
            finally { nodes.Dispose(); }
        }

        private void ApplyIncoming(MultiplayerSession session, long now)
        {
            while (_incoming.TryDequeue(out SimulationCommandMessage message))
            {
                if (message.OriginPlayerId == session.LocalPlayerId) continue;

                if (!CommandDecode.TryDecode(message, DevTreePurchaseCommand.Decode, LogTopic.City,
                        "DevTreeSync", out DevTreePurchaseCommand command))
                    continue;

                Entity node = ResolveNode(command.NodePrefabName);
                if (node == Entity.Null)
                {
                    SyncLog.Warn(LogTopic.City, "DevTreeSync: unknown node '" +
                        command.NodePrefabName + "' from player " + message.OriginPlayerId +
                        "; skipping.");
                    continue;
                }
                if (!IsLocked(node)) continue; // already unlocked here — nothing to do

                // Created from UIUpdate the event would be cleaned up before UnlockSystem (MainLoop) sees it;
                // the barrier replays it there.
                if (!_unlocks.TryQueue(node)) continue;
                _guard.Mark(NodeKey(command.NodePrefabName), now);

                // The host owns the points.
                if (session.Role == SessionRole.Host &&
                    EntityManager.HasComponent<DevTreeNodeData>(node) &&
                    !_pointsQuery.IsEmptyIgnoreFilter)
                {
                    int cost = EntityManager.GetComponentData<DevTreeNodeData>(node).m_Cost;
                    DevTreePoints points = _pointsQuery.GetSingleton<DevTreePoints>();
                    points.m_Points -= cost;
                    _pointsQuery.SetSingleton(points);
                }

                SyncLog.Detail(LogTopic.City, "DevTreeSync: applied purchase of '" +
                    command.NodePrefabName + "' from player " + message.OriginPlayerId + ".");
            }
        }

        private Entity ResolveNode(string name)
        {
            if (string.IsNullOrEmpty(name)) return Entity.Null;

            if (_nodeByName.TryGetValue(name, out Entity cached) && EntityManager.Exists(cached)) return cached;

            NativeArray<Entity> nodes = _nodes.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < nodes.Length; i++)
                {
                    string candidate = PrefabIndex.SafeName(_prefabSystem, nodes[i]);
                    if (!string.IsNullOrEmpty(candidate)) _nodeByName[candidate] = nodes[i];
                }
            }
            finally { nodes.Dispose(); }

            return _nodeByName.TryGetValue(name, out cached) ? cached : Entity.Null;
        }

        private static string NodeKey(string name) => "devtree|" + name;
    }
}
