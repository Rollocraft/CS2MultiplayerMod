using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using Colossal.Mathematics;
using Game;
using Game.Common;
using Game.Net;
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
    /// Replicates in-place road composition changes - the whole "street tools" family:
    /// edge upgrades (trees, grass, wide sidewalks, sound barriers, street lights,
    /// crosswalks, tree-row styles) and node upgrades (traffic lights, all-way stops,
    /// roundabouts). The upgraded entity survives the change, so neither placement nor
    /// delete sync sees it.
    ///
    /// Observed runtime behaviour this must mirror:
    ///   - an upgrade lands as an <see cref="Upgraded"/> component (plus, for edges, a
    ///     <see cref="SubReplacement"/> buffer) on the ORIGINAL edge or node, tagged
    ///     Updated - the entity is otherwise untouched;
    ///   - node upgrades only ever carry the node-mask flags; committing one also strips
    ///     the node's <see cref="TrafficLights"/> runtime component (re-initialized from
    ///     the new composition) and re-updates the connected edges;
    ///   - REMOVING the last upgrade removes the Upgraded component entirely (zero flags
    ///     are never stored) - so capture must also watch Updated entities WITHOUT
    ///     Upgraded and ship a clear when we knew the entity as upgraded.
    ///
    ///   capture: Updated edge/node whose flags+sub-replacements differ from what we last
    ///            saw/sent for it -> broadcast a <see cref="NetUpgradeCommand"/> with the
    ///            full resulting state (all-zero = cleared).
    ///   realize: find the matching local edge (prefab + Bezier endpoints, either
    ///            orientation - a backward match swaps left/right flags and sub-
    ///            replacement sides, the game's own invert recipe) or node (position),
    ///            write/remove Upgraded + SubReplacement, tag Updated so the game
    ///            rebuilds the composition. Compare-before-write plus the last-seen
    ///            cache kills echo loops.
    ///
    /// A just-built upgraded road can race its own placement command, so unmatched
    /// upgrades are retried for a few seconds instead of dropped.
    /// </summary>
    // State, lifecycle and the per-update cycle. Noticing what this player upgraded is in
    // Capture.cs; applying what a peer sent is in Apply.cs.
    public partial class NetUpgradeSyncSystem : GameSystemBase
    {
        private const long RetryWindowMs = 10000;

        /// <summary>Edge endpoint / node position match tolerance, squared metres (2 m).</summary>
        private const float MatchTolSq = 4f;

        /// <summary>Never match a node stacked on another level (bridge over junction).</summary>
        private const float NodeMatchMaxDy = 4f;

        private struct SeenState
        {
            public uint General, Left, Right;
            public string SubRepSig;

            public bool IsCleared =>
                General == 0 && Left == 0 && Right == 0 && string.IsNullOrEmpty(SubRepSig);

            public bool Equals(in SeenState other) =>
                General == other.General && Left == other.Left && Right == other.Right &&
                (SubRepSig ?? "") == (other.SubRepSig ?? "");
        }

        private readonly ConcurrentQueue<SimulationCommandMessage> _incoming =
            new ConcurrentQueue<SimulationCommandMessage>();
        private readonly List<(NetUpgradeCommand command, long deadline)> _retry =
            new List<(NetUpgradeCommand, long)>();
        private readonly Dictionary<string, SeenState> _lastSeen =
            new Dictionary<string, SeenState>();

        private PrefabSystem _prefabSystem;
        private PrefabIndex _prefabIndex;
        private EntityQuery _upgradedEdges;
        private EntityQuery _bareEdges;
        private EntityQuery _upgradedNodes;
        private EntityQuery _bareNodes;
        private EntityQuery _liveEdges;
        private EntityQuery _liveNodes;
        private CommandObserver _observer;
        private bool _seeded;

        protected override void OnCreate()
        {
            base.OnCreate();

            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _prefabIndex = new PrefabIndex(_prefabSystem, GetEntityQuery(ComponentType.ReadOnly<PrefabData>()));

            // Created is intentionally NOT excluded: a road built with an upgrade already
            // applied (e.g. "road with trees" from the start) must ship its flags too -
            // the placement command alone rebuilds a plain edge on the other side.
            _upgradedEdges = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<Updated, Upgraded, Edge, Curve, PrefabRef>(),
                None = SyncQuery.ReadOnly<Temp, Deleted, Owner>(),
            });

            // Removal detection: the game strips Upgraded entirely when the last upgrade
            // goes, so a cleared segment is an Updated edge with NO Upgraded. Only edges
            // we knew as upgraded (last-seen cache) produce a command.
            _bareEdges = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<Updated, Edge, Curve, PrefabRef>(),
                None = SyncQuery.ReadOnly<Upgraded, Temp, Deleted, Owner>(),
            });

            _upgradedNodes = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<Updated, Upgraded, Node, PrefabRef>(),
                None = SyncQuery.ReadOnly<Temp, Deleted, Owner>(),
            });

            _bareNodes = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<Updated, Node, PrefabRef>(),
                None = SyncQuery.ReadOnly<Upgraded, Temp, Deleted, Owner>(),
            });

            _liveEdges = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<Edge, Curve, PrefabRef>(),
                None = SyncQuery.ReadOnly<Temp, Owner, Deleted>(),
            });

            _liveNodes = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<Node, PrefabRef>(),
                None = SyncQuery.ReadOnly<Temp, Owner, Deleted>(),
            });

            _observer = SyncObserverBinding.Bind(
                () => new CommandObserver(_incoming, NetUpgradeCommand.Id));
        }

        protected override void OnDestroy()
        {
            SyncObserverBinding.Unbind(_observer);
            base.OnDestroy();
        }

        protected override void OnUpdate()
        {
            using (Diagnostics.SyncProfiler.Measure("NetUpgrade"))
            {
                MultiplayerService service = Mod.Service;
                if (service == null) return;

                MultiplayerSession session = service.Session;
                if (!service.GameplaySyncReady)
                {
                    if (_lastSeen.Count > 0) { _lastSeen.Clear(); _retry.Clear(); }
                    _seeded = false;
                    return;
                }

                if (!_seeded) { SeedLastSeen(); _seeded = true; }

                CaptureEdgeUpgrades(session);
                CaptureEdgeClears(session);
                CaptureNodeUpgrades(session);
                CaptureNodeClears(session);
            }
        }

        /// <summary>
        /// Learn every upgrade that already exists when sync starts (both sides hold the
        /// same downloaded world) without sending anything. Without this, removing a
        /// pre-session upgrade would be invisible: the removal event leaves a bare entity,
        /// and bare entities only ship a clear when the cache knew them as upgraded.
        /// </summary>
        private void SeedLastSeen()
        {
            EntityQuery allUpgraded = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<Upgraded, PrefabRef>(),
                Any = SyncQuery.ReadOnly<Edge, Node>(),
                None = SyncQuery.ReadOnly<Temp, Deleted, Owner>(),
            });

            NativeArray<Entity> entities = allUpgraded.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity entity = entities[i];
                    CompositionFlags flags = EntityManager.GetComponentData<Upgraded>(entity).m_Flags;
                    string key;
                    string sig = "";
                    if (EntityManager.HasComponent<Edge>(entity))
                    {
                        if (!EntityManager.HasComponent<Curve>(entity)) continue;
                        Bezier4x3 b = EntityManager.GetComponentData<Curve>(entity).m_Bezier;
                        key = EdgeKey(b.a, b.d);
                        sig = SubRepSig(ReadSubReplacements(entity));
                    }
                    else
                    {
                        key = NodeKey(EntityManager.GetComponentData<Node>(entity).m_Position);
                    }
                    _lastSeen[key] = new SeenState
                    {
                        General = (uint)flags.m_General,
                        Left = (uint)flags.m_Left,
                        Right = (uint)flags.m_Right,
                        SubRepSig = sig,
                    };
                }
                if (entities.Length > 0)
                    SyncLog.Detail(LogTopic.Nets, "NetUpgradeSync: seeded " + entities.Length +
                        " existing upgrade(s).");
            }
            finally
            {
                entities.Dispose();
            }
        }

        /// <summary>Called by <see cref="SyncRealizeSystem"/> during ToolUpdate (see there for why).</summary>
        public void RealizePending()
        {
            MultiplayerService service = Mod.Service;
            if (service == null) return;

            MultiplayerSession session = service.Session;
            if (!service.GameplaySyncReady) return;

            long now = service.NowMs;
            List<NetUpgradeCommand> work = null;

            // Retries first (older), then fresh arrivals.
            if (_retry.Count > 0)
            {
                work = new List<NetUpgradeCommand>();
                for (int i = 0; i < _retry.Count; i++)
                    if (_retry[i].deadline >= now) work.Add(_retry[i].command);
                _retry.Clear();
            }

            SimulationCommandMessage message;
            while (_incoming.TryDequeue(out message))
            {
                if (message.OriginPlayerId == session.LocalPlayerId) continue;
                try { (work ?? (work = new List<NetUpgradeCommand>())).Add(NetUpgradeCommand.Decode(message.Body)); }
                catch (System.Exception ex) { SyncLog.Warn(LogTopic.Nets, "NetUpgradeSync: dropping malformed command: " + ex.Message); }
            }

            if (work != null && work.Count > 0) Apply(work, now);
        }
    }
}
