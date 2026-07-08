using System.Collections.Concurrent;
using System.Collections.Generic;
using Game;
using Game.City;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;

using CS2MultiplayerMod.Game.Sync.Infrastructure;
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.Channels;
namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Replicates dev-tree node purchases by detecting <see cref="DevTreeNodeData"/> unlocks
    /// and broadcasting <see cref="DevTreePurchaseCommand"/>. Applies remotely via
    /// <see cref="EndFrameBarrier"/> (load-bearing: must reach MainLoop, not UIUpdate),
    /// charges the host's <see cref="DevTreePoints"/> to stop refill-on-snapshot. Echo guard
    /// suppresses re-detecting applied unlocks.
    /// </summary>
    public partial class DevTreeSyncSystem : GameSystemBase
    {
        private readonly ConcurrentQueue<SimulationCommandMessage> _incoming =
            new ConcurrentQueue<SimulationCommandMessage>();
        private readonly ReplicationGuard _guard = new ReplicationGuard();
        private readonly HashSet<string> _knownUnlocked = new HashSet<string>();
        private readonly Dictionary<string, Entity> _nodeByName = new Dictionary<string, Entity>();

        private PrefabSystem _prefabSystem;
        private EndFrameBarrier _endFrameBarrier;
        private EntityQuery _nodes;
        private EntityQuery _pointsQuery;
        private EntityArchetype _unlockArchetype;
        private CommandObserver _observer;
        private bool _initialized;

        protected override void OnCreate()
        {
            base.OnCreate();
            Mod.log.Info(nameof(DevTreeSyncSystem) + " ready.");

            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            // Unlock events must be raised through the same barrier the game uses so
            // UnlockSystem (MainLoop) consumes them before CleanUpSystem reaps them.
            _endFrameBarrier = World.GetOrCreateSystemManaged<EndFrameBarrier>();
            // DevTree nodes are prefab entities — IncludePrefab so the query finds them.
            _nodes = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<DevTreeNodeData>() },
                None = new[] { ComponentType.ReadOnly<Temp>() },
                Options = EntityQueryOptions.IncludePrefab,
            });
            _pointsQuery = GetEntityQuery(ComponentType.ReadWrite<DevTreePoints>());
            // The exact archetype the game raises to unlock a node (see DevTreeSystem).
            _unlockArchetype = EntityManager.CreateArchetype(
                ComponentType.ReadWrite<Unlock>(), ComponentType.ReadWrite<global::Game.Common.Event>());

            if (Mod.Service != null)
            {
                _observer = new CommandObserver(_incoming, DevTreePurchaseCommand.Id);
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
            if (!service.GameplaySyncReady)
            {
                _initialized = false;
                return;
            }

            long now = service.NowMs;
            _guard.Prune(now);

            // Apply remote purchases first so their unlocks are accounted for before we
            // diff for local ones.
            ApplyIncoming(session, now);

            // First ready tick: adopt the current unlocked set as the baseline so the
            // already-unlocked nodes from the loaded save are never re-broadcast.
            if (!_initialized)
            {
                SeedKnown();
                _initialized = true;
                return;
            }

            DetectLocalPurchases(session, now);
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
                    string name = _prefabSystem.GetPrefabName(nodes[i]);
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
                    string name = _prefabSystem.GetPrefabName(nodes[i]);
                    if (string.IsNullOrEmpty(name)) continue;

                    bool unlocked = !IsLocked(nodes[i]);
                    bool known = _knownUnlocked.Contains(name);

                    if (unlocked && !known)
                    {
                        _knownUnlocked.Add(name);
                        if (_guard.Consume(NodeKey(name), now)) continue; // we applied it — no echo

                        var command = new DevTreePurchaseCommand { NodePrefabName = name };
                        session.SendCommand(0, DevTreePurchaseCommand.Id, command.Encode());
                        Mod.Verbose("[MP] DevTreeSync: broadcast purchase of '" + name + "'.");
                    }
                    else if (!unlocked && known)
                    {
                        // Re-locked (a world resync reloaded the host's state) — let a
                        // future unlock be detected again.
                        _knownUnlocked.Remove(name);
                    }
                }
            }
            finally { nodes.Dispose(); }
        }

        private void ApplyIncoming(MultiplayerSession session, long now)
        {
            SimulationCommandMessage message;
            while (_incoming.TryDequeue(out message))
            {
                if (message.OriginPlayerId == session.LocalPlayerId) continue;

                DevTreePurchaseCommand command;
                try { command = DevTreePurchaseCommand.Decode(message.Body); }
                catch (System.Exception ex) { Mod.log.Warn("[MP] DevTreeSync: dropping malformed command: " + ex.Message); continue; }

                Entity node = ResolveNode(command.NodePrefabName);
                if (node == Entity.Null)
                {
                    Mod.log.Warn("[MP] DevTreeSync: unknown node '" + command.NodePrefabName +
                                 "' from player " + message.OriginPlayerId + "; skipping.");
                    continue;
                }
                if (!IsLocked(node)) continue; // already unlocked here — nothing to do

                _guard.Mark(NodeKey(command.NodePrefabName), now);

                // Unlock the node everywhere so the partner's tree updates. Defer the event
                // to the EndFrameBarrier — creating it directly from UIUpdate would have it
                // reaped by CleanUpSystem this same frame, before UnlockSystem (MainLoop)
                // could process it. The barrier replays it at the next MainLoop where the
                // game's own unlock pipeline (node + dependent-content cascade) runs.
                EntityCommandBuffer ecb = _endFrameBarrier.CreateCommandBuffer();
                Entity e = ecb.CreateEntity(_unlockArchetype);
                ecb.SetComponent(e, new Unlock(node));

                // Only the host owns the points: charge the node's cost so the authoritative
                // snapshot reflects the spend instead of refilling the buyer.
                if (session.Role == SessionRole.Host &&
                    EntityManager.HasComponent<DevTreeNodeData>(node) &&
                    !_pointsQuery.IsEmptyIgnoreFilter)
                {
                    int cost = EntityManager.GetComponentData<DevTreeNodeData>(node).m_Cost;
                    DevTreePoints points = _pointsQuery.GetSingleton<DevTreePoints>();
                    points.m_Points -= cost;
                    _pointsQuery.SetSingleton(points);
                }

                Mod.Verbose("[MP] DevTreeSync: applied purchase of '" + command.NodePrefabName +
                             "' from player " + message.OriginPlayerId + ".");
            }
        }

        private Entity ResolveNode(string name)
        {
            if (string.IsNullOrEmpty(name)) return Entity.Null;

            Entity cached;
            if (_nodeByName.TryGetValue(name, out cached) && EntityManager.Exists(cached)) return cached;

            NativeArray<Entity> nodes = _nodes.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < nodes.Length; i++)
                {
                    string candidate = _prefabSystem.GetPrefabName(nodes[i]);
                    if (!string.IsNullOrEmpty(candidate)) _nodeByName[candidate] = nodes[i];
                }
            }
            finally { nodes.Dispose(); }

            return _nodeByName.TryGetValue(name, out cached) ? cached : Entity.Null;
        }

        private static string NodeKey(string name) => "devtree|" + name;

    }
}
