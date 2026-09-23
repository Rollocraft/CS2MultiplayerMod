using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
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
    /// Replicates in-place composition changes (street tools): edge upgrades and node upgrades. An
    /// upgrade lands as <see cref="Upgraded"/> (plus <see cref="SubReplacement"/> for edges) on the
    /// original entity; node commits also strip <see cref="TrafficLights"/>; removing the last upgrade
    /// removes the component, so bare Updated entities are watched too. Commands carry the full state;
    /// a backward edge match mirrors the game's invert recipe. Unmatched upgrades retry briefly.
    /// </summary>
    public partial class NetUpgradeSyncSystem : CommandSyncSystem, IRealizeStage
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
        private bool _seeded;

        protected override void OnCreate()
        {
            base.OnCreate();

            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _prefabIndex = new PrefabIndex(_prefabSystem, GetEntityQuery(ComponentType.ReadOnly<PrefabData>()));

            // Created included: a road built already upgraded must ship its flags.
            _upgradedEdges = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<Updated, Upgraded, Edge, Curve, PrefabRef>(),
                None = SyncQuery.ReadOnly<Temp, Deleted, Owner>(),
            });

            // Removal: an Updated edge without Upgraded, sent only if we knew it as upgraded.
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

            ListenFor(new[] { NetUpgradeCommand.Id }, drainOnReload: false);
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
        /// Learns pre-existing upgrades without sending, so removing one is still detected.
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
            // An upgrade targets a road the held net pipeline may not have delivered yet.
            if (RealizeGate.WorldBuildingHeld) return;

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

            while (_incoming.TryDequeue(out SimulationCommandMessage message))
            {
                if (message.OriginPlayerId == session.LocalPlayerId) continue;
                try { (work ?? (work = new List<NetUpgradeCommand>())).Add(NetUpgradeCommand.Decode(message.Body)); }
                catch (System.Exception ex) { SyncLog.Warn(LogTopic.Nets, "NetUpgradeSync: dropping malformed command: " + ex.Message); }
            }

            if (work != null && work.Count > 0) Apply(work, now);
        }
    }
}
