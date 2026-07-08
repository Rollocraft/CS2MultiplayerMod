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
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;

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

            Mod.log.Info(nameof(NetUpgradeSyncSystem) + " ready.");
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _prefabIndex = new PrefabIndex(_prefabSystem, GetEntityQuery(ComponentType.ReadOnly<PrefabData>()));

            // Created is intentionally NOT excluded: a road built with an upgrade already
            // applied (e.g. "road with trees" from the start) must ship its flags too -
            // the placement command alone rebuilds a plain edge on the other side.
            _upgradedEdges = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Updated>(),
                    ComponentType.ReadOnly<Upgraded>(),
                    ComponentType.ReadOnly<Edge>(),
                    ComponentType.ReadOnly<Curve>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Owner>(),
                },
            });

            // Removal detection: the game strips Upgraded entirely when the last upgrade
            // goes, so a cleared segment is an Updated edge with NO Upgraded. Only edges
            // we knew as upgraded (last-seen cache) produce a command.
            _bareEdges = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Updated>(),
                    ComponentType.ReadOnly<Edge>(),
                    ComponentType.ReadOnly<Curve>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Upgraded>(),
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Owner>(),
                },
            });

            _upgradedNodes = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Updated>(),
                    ComponentType.ReadOnly<Upgraded>(),
                    ComponentType.ReadOnly<Node>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Owner>(),
                },
            });

            _bareNodes = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Updated>(),
                    ComponentType.ReadOnly<Node>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Upgraded>(),
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Owner>(),
                },
            });

            _liveEdges = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Edge>(),
                    ComponentType.ReadOnly<Curve>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Owner>(),
                    ComponentType.ReadOnly<Deleted>(),
                },
            });

            _liveNodes = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Node>(),
                    ComponentType.ReadOnly<PrefabRef>(),
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
                _observer = new CommandObserver(_incoming, NetUpgradeCommand.Id);
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
                All = new[]
                {
                    ComponentType.ReadOnly<Upgraded>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                },
                Any = new[]
                {
                    ComponentType.ReadOnly<Edge>(),
                    ComponentType.ReadOnly<Node>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Owner>(),
                },
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
                    Mod.Verbose("[MP] NetUpgradeSync: seeded " + entities.Length + " existing upgrade(s).");
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
                catch (System.Exception ex) { Mod.log.Warn("[MP] NetUpgradeSync: dropping malformed command: " + ex.Message); }
            }

            if (work != null && work.Count > 0) Apply(work, now);
        }

        // ---------------------------------------------------------------- capture

        private void CaptureEdgeUpgrades(MultiplayerSession session)
        {
            if (_upgradedEdges.IsEmptyIgnoreFilter) return;

            NativeArray<Entity> entities = _upgradedEdges.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity entity = entities[i];
                    string name = _prefabSystem.GetPrefabName(EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab);
                    if (string.IsNullOrEmpty(name) || name.StartsWith("Invisible")) continue;

                    Bezier4x3 b = EntityManager.GetComponentData<Curve>(entity).m_Bezier;
                    CompositionFlags flags = EntityManager.GetComponentData<Upgraded>(entity).m_Flags;
                    NetUpgradeCommand.SubRep[] subs = ReadSubReplacements(entity);
                    var current = new SeenState
                    {
                        General = (uint)flags.m_General,
                        Left = (uint)flags.m_Left,
                        Right = (uint)flags.m_Right,
                        SubRepSig = SubRepSig(subs),
                    };

                    // Roads get Updated for many reasons (neighbour edits, traffic) - only
                    // an actual composition change for this segment is worth broadcasting.
                    // The cache is also written on apply, which suppresses the echo.
                    string key = EdgeKey(b.a, b.d);
                    SeenState last;
                    if (_lastSeen.TryGetValue(key, out last) && last.Equals(current)) continue;
                    _lastSeen[key] = current;

                    var command = new NetUpgradeCommand
                    {
                        PrefabName = name,
                        Ax = b.a.x, Ay = b.a.y, Az = b.a.z,
                        Dx = b.d.x, Dy = b.d.y, Dz = b.d.z,
                        General = current.General, Left = current.Left, Right = current.Right,
                        SubReps = subs,
                    };
                    session.SendCommand(0, NetUpgradeCommand.Id, command.Encode());
                    Mod.Verbose("[MP] NetUpgradeSync captured upgrade on '" + name + "'.");
                }
            }
            finally
            {
                entities.Dispose();
            }
        }

        private void CaptureEdgeClears(MultiplayerSession session)
        {
            if (_lastSeen.Count == 0 || _bareEdges.IsEmptyIgnoreFilter) return;

            NativeArray<Entity> entities = _bareEdges.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity entity = entities[i];
                    Bezier4x3 b = EntityManager.GetComponentData<Curve>(entity).m_Bezier;
                    string key = EdgeKey(b.a, b.d);
                    SeenState last;
                    if (!_lastSeen.TryGetValue(key, out last) || last.IsCleared) continue;

                    string name = _prefabSystem.GetPrefabName(EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab);
                    if (string.IsNullOrEmpty(name) || name.StartsWith("Invisible")) continue;
                    _lastSeen[key] = default(SeenState);

                    var command = new NetUpgradeCommand
                    {
                        PrefabName = name,
                        Ax = b.a.x, Ay = b.a.y, Az = b.a.z,
                        Dx = b.d.x, Dy = b.d.y, Dz = b.d.z,
                    };
                    session.SendCommand(0, NetUpgradeCommand.Id, command.Encode());
                    Mod.Verbose("[MP] NetUpgradeSync captured upgrade REMOVAL on '" + name + "'.");
                }
            }
            finally
            {
                entities.Dispose();
            }
        }

        private void CaptureNodeUpgrades(MultiplayerSession session)
        {
            if (_upgradedNodes.IsEmptyIgnoreFilter) return;

            NativeArray<Entity> entities = _upgradedNodes.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity entity = entities[i];
                    string name = _prefabSystem.GetPrefabName(EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab);
                    if (string.IsNullOrEmpty(name) || name.StartsWith("Invisible")) continue;

                    float3 pos = EntityManager.GetComponentData<Node>(entity).m_Position;
                    CompositionFlags flags = EntityManager.GetComponentData<Upgraded>(entity).m_Flags;
                    var current = new SeenState
                    {
                        General = (uint)flags.m_General,
                        Left = (uint)flags.m_Left,
                        Right = (uint)flags.m_Right,
                        SubRepSig = "",
                    };

                    string key = NodeKey(pos);
                    SeenState last;
                    if (_lastSeen.TryGetValue(key, out last) && last.Equals(current)) continue;
                    _lastSeen[key] = current;

                    var command = new NetUpgradeCommand
                    {
                        PrefabName = name,
                        Ax = pos.x, Ay = pos.y, Az = pos.z,
                        Dx = pos.x, Dy = pos.y, Dz = pos.z,
                        General = current.General, Left = current.Left, Right = current.Right,
                        IsNode = true,
                    };
                    session.SendCommand(0, NetUpgradeCommand.Id, command.Encode());
                    Mod.Verbose("[MP] NetUpgradeSync captured node upgrade at '" + name + "'.");
                }
            }
            finally
            {
                entities.Dispose();
            }
        }

        private void CaptureNodeClears(MultiplayerSession session)
        {
            if (_lastSeen.Count == 0 || _bareNodes.IsEmptyIgnoreFilter) return;

            NativeArray<Entity> entities = _bareNodes.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity entity = entities[i];
                    float3 pos = EntityManager.GetComponentData<Node>(entity).m_Position;
                    string key = NodeKey(pos);
                    SeenState last;
                    if (!_lastSeen.TryGetValue(key, out last) || last.IsCleared) continue;

                    string name = _prefabSystem.GetPrefabName(EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab);
                    if (string.IsNullOrEmpty(name) || name.StartsWith("Invisible")) continue;
                    _lastSeen[key] = default(SeenState);

                    var command = new NetUpgradeCommand
                    {
                        PrefabName = name,
                        Ax = pos.x, Ay = pos.y, Az = pos.z,
                        Dx = pos.x, Dy = pos.y, Dz = pos.z,
                        IsNode = true,
                    };
                    session.SendCommand(0, NetUpgradeCommand.Id, command.Encode());
                    Mod.Verbose("[MP] NetUpgradeSync captured node upgrade REMOVAL at '" + name + "'.");
                }
            }
            finally
            {
                entities.Dispose();
            }
        }

        // ---------------------------------------------------------------- realize

        private void Apply(List<NetUpgradeCommand> commands, long now)
        {
            // Each command carries the FULL resulting state, so within one drain the last
            // command per target wins - applying an older retry after a newer arrival
            // would land the wrong final state.
            var lastIndex = new Dictionary<string, int>();
            for (int i = 0; i < commands.Count; i++)
            {
                NetUpgradeCommand c = commands[i];
                var a = new float3(c.Ax, c.Ay, c.Az);
                var d = new float3(c.Dx, c.Dy, c.Dz);
                lastIndex[c.IsNode ? NodeKey(a) : EdgeKey(a, d)] = i;
            }

            var edgeTargets = new List<(Entity prefab, float3 a, float3 d, NetUpgradeCommand cmd)>();
            var nodeTargets = new List<(Entity prefab, float3 pos, NetUpgradeCommand cmd)>();
            for (int i = 0; i < commands.Count; i++)
            {
                NetUpgradeCommand c = commands[i];
                var a = new float3(c.Ax, c.Ay, c.Az);
                var d = new float3(c.Dx, c.Dy, c.Dz);
                if (lastIndex[c.IsNode ? NodeKey(a) : EdgeKey(a, d)] != i) continue;

                Entity prefab;
                if (!_prefabIndex.TryResolve(c.PrefabName, out prefab)) continue;
                if (c.IsNode) nodeTargets.Add((prefab, a, c));
                else edgeTargets.Add((prefab, a, d, c));
            }

            int applied = ApplyEdges(edgeTargets) + ApplyNodes(nodeTargets);

            // Whatever found no entity yet probably races its own placement - retry briefly.
            for (int t = 0; t < edgeTargets.Count; t++)
                _retry.Add((edgeTargets[t].cmd, now + RetryWindowMs));
            for (int t = 0; t < nodeTargets.Count; t++)
                _retry.Add((nodeTargets[t].cmd, now + RetryWindowMs));

            if (applied > 0)
                Mod.Verbose("[MP] NetUpgradeSync: applied " + applied + " road upgrade(s)" +
                             (edgeTargets.Count + nodeTargets.Count > 0
                                 ? ", " + (edgeTargets.Count + nodeTargets.Count) + " waiting for their segment"
                                 : "") + ".");
        }

        private int ApplyEdges(List<(Entity prefab, float3 a, float3 d, NetUpgradeCommand cmd)> targets)
        {
            if (targets.Count == 0) return 0;

            int applied = 0;
            NativeArray<Entity> entities = _liveEdges.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length && targets.Count > 0; i++)
                {
                    Entity entity = entities[i];
                    Entity candidatePrefab = EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab;
                    Bezier4x3 b = EntityManager.GetComponentData<Curve>(entity).m_Bezier;

                    for (int t = targets.Count - 1; t >= 0; t--)
                    {
                        if (targets[t].prefab != candidatePrefab) continue;
                        bool forward = math.distancesq(targets[t].a, b.a) <= MatchTolSq && math.distancesq(targets[t].d, b.d) <= MatchTolSq;
                        bool backward = !forward && math.distancesq(targets[t].a, b.d) <= MatchTolSq && math.distancesq(targets[t].d, b.a) <= MatchTolSq;
                        if (!forward && !backward) continue;

                        NetUpgradeCommand cmd = targets[t].cmd;
                        var flags = new CompositionFlags
                        {
                            m_General = (CompositionFlags.General)cmd.General,
                            m_Left = (CompositionFlags.Side)cmd.Left,
                            m_Right = (CompositionFlags.Side)cmd.Right,
                        };
                        NetUpgradeCommand.SubRep[] subs = cmd.SubReps ?? new NetUpgradeCommand.SubRep[0];

                        // Our edge runs the other way: mirror the game's own invert recipe -
                        // swap left/right flags and negate sub-replacement sides.
                        if (backward)
                        {
                            flags = NetCompositionHelpers.InvertCompositionFlags(flags);
                            if (subs.Length > 0)
                            {
                                var inverted = new NetUpgradeCommand.SubRep[subs.Length];
                                for (int s = 0; s < subs.Length; s++)
                                {
                                    inverted[s] = subs[s];
                                    inverted[s].Side = (sbyte)(-subs[s].Side);
                                }
                                subs = inverted;
                            }
                        }

                        // Record what this machine will now hold (LOCAL orientation) so our
                        // own capture sees "already known" instead of echoing it back.
                        _lastSeen[EdgeKey(b.a, b.d)] = new SeenState
                        {
                            General = (uint)flags.m_General,
                            Left = (uint)flags.m_Left,
                            Right = (uint)flags.m_Right,
                            SubRepSig = SubRepSig(subs),
                        };

                        bool hasUpgraded = EntityManager.HasComponent<Upgraded>(entity);
                        CompositionFlags currentFlags = hasUpgraded
                            ? EntityManager.GetComponentData<Upgraded>(entity).m_Flags
                            : default(CompositionFlags);
                        bool cleared = flags == default(CompositionFlags) && subs.Length == 0;

                        if (currentFlags == flags && SubRepSig(ReadSubReplacements(entity)) == SubRepSig(subs))
                        {
                            targets.RemoveAt(t); // already in this state - echo or replay
                            break;
                        }

                        if (cleared)
                        {
                            // The game never stores zero flags - removing the last upgrade
                            // strips the components, so mirror that exactly.
                            if (hasUpgraded) EntityManager.RemoveComponent<Upgraded>(entity);
                            if (EntityManager.HasBuffer<SubReplacement>(entity)) EntityManager.RemoveComponent<SubReplacement>(entity);
                        }
                        else
                        {
                            if (hasUpgraded) EntityManager.SetComponentData(entity, new Upgraded { m_Flags = flags });
                            else EntityManager.AddComponentData(entity, new Upgraded { m_Flags = flags });
                            WriteSubReplacements(entity, subs);
                        }

                        EntityManager.AddComponent<Updated>(entity);
                        // The composition at each end (crosswalks, transitions) is selected
                        // per node - re-update them like the game's own commit does.
                        Edge ends = EntityManager.GetComponentData<Edge>(entity);
                        TagUpdated(ends.m_Start);
                        TagUpdated(ends.m_End);

                        targets.RemoveAt(t);
                        applied++;
                        break;
                    }
                }
            }
            finally
            {
                entities.Dispose();
            }
            return applied;
        }

        private int ApplyNodes(List<(Entity prefab, float3 pos, NetUpgradeCommand cmd)> targets)
        {
            if (targets.Count == 0) return 0;

            int applied = 0;
            NativeArray<Entity> entities = _liveNodes.ToEntityArray(Allocator.Temp);
            try
            {
                for (int t = targets.Count - 1; t >= 0; t--)
                {
                    float3 wanted = targets[t].pos;
                    Entity best = Entity.Null;
                    bool bestExact = false;
                    float bestDistSq = float.MaxValue;

                    for (int i = 0; i < entities.Length; i++)
                    {
                        float3 pos = EntityManager.GetComponentData<Node>(entities[i]).m_Position;
                        if (math.abs(pos.y - wanted.y) > NodeMatchMaxDy) continue;
                        float distSq = math.distancesq(pos.xz, wanted.xz);
                        if (distSq > MatchTolSq) continue;

                        // Prefer a node of the announced prefab, but a junction's node prefab
                        // can legitimately differ per machine (it inherits one of the touching
                        // roads) - position decides when no exact-prefab node is nearby.
                        bool exact = EntityManager.GetComponentData<PrefabRef>(entities[i]).m_Prefab == targets[t].prefab;
                        if ((exact && !bestExact) || (exact == bestExact && distSq < bestDistSq))
                        {
                            best = entities[i];
                            bestExact = exact;
                            bestDistSq = distSq;
                        }
                    }

                    if (best == Entity.Null) continue; // stays in targets -> retried

                    NetUpgradeCommand cmd = targets[t].cmd;
                    var flags = new CompositionFlags
                    {
                        m_General = (CompositionFlags.General)cmd.General,
                        m_Left = (CompositionFlags.Side)cmd.Left,
                        m_Right = (CompositionFlags.Side)cmd.Right,
                    };

                    float3 bestPos = EntityManager.GetComponentData<Node>(best).m_Position;
                    _lastSeen[NodeKey(bestPos)] = new SeenState
                    {
                        General = (uint)flags.m_General,
                        Left = (uint)flags.m_Left,
                        Right = (uint)flags.m_Right,
                        SubRepSig = "",
                    };

                    bool hasUpgraded = EntityManager.HasComponent<Upgraded>(best);
                    CompositionFlags currentFlags = hasUpgraded
                        ? EntityManager.GetComponentData<Upgraded>(best).m_Flags
                        : default(CompositionFlags);

                    if (currentFlags == flags)
                    {
                        targets.RemoveAt(t); // already in this state - echo or replay
                        continue;
                    }

                    if (flags == default(CompositionFlags))
                    {
                        if (hasUpgraded) EntityManager.RemoveComponent<Upgraded>(best);
                    }
                    else
                    {
                        if (hasUpgraded) EntityManager.SetComponentData(best, new Upgraded { m_Flags = flags });
                        else EntityManager.AddComponentData(best, new Upgraded { m_Flags = flags });
                    }

                    // The game's commit strips the runtime traffic-light state so it is
                    // re-initialized from the new composition - mirror that.
                    if (EntityManager.HasComponent<TrafficLights>(best))
                        EntityManager.RemoveComponent<TrafficLights>(best);

                    EntityManager.AddComponent<Updated>(best);

                    // Node composition is selected while processing the connected edges, so
                    // re-update them like the game's own commit does.
                    if (EntityManager.HasBuffer<ConnectedEdge>(best))
                    {
                        DynamicBuffer<ConnectedEdge> connected = EntityManager.GetBuffer<ConnectedEdge>(best);
                        for (int c = 0; c < connected.Length; c++)
                            TagUpdated(connected[c].m_Edge);
                    }

                    targets.RemoveAt(t);
                    applied++;
                }
            }
            finally
            {
                entities.Dispose();
            }
            return applied;
        }

        // ---------------------------------------------------------------- helpers

        private void TagUpdated(Entity entity)
        {
            if (entity == Entity.Null || !EntityManager.Exists(entity)) return;
            if (EntityManager.HasComponent<Deleted>(entity) || EntityManager.HasComponent<Temp>(entity)) return;
            EntityManager.AddComponent<Updated>(entity);
        }

        private NetUpgradeCommand.SubRep[] ReadSubReplacements(Entity entity)
        {
            if (!EntityManager.HasBuffer<SubReplacement>(entity)) return new NetUpgradeCommand.SubRep[0];

            DynamicBuffer<SubReplacement> buffer = EntityManager.GetBuffer<SubReplacement>(entity);
            var list = new List<NetUpgradeCommand.SubRep>(buffer.Length);
            for (int i = 0; i < buffer.Length && list.Count < NetUpgradeCommand.MaxSubReplacements; i++)
            {
                string name = _prefabSystem.GetPrefabName(buffer[i].m_Prefab);
                if (string.IsNullOrEmpty(name)) continue;
                list.Add(new NetUpgradeCommand.SubRep
                {
                    PrefabName = name,
                    Type = (byte)buffer[i].m_Type,
                    Side = (sbyte)buffer[i].m_Side,
                    AgeMask = (byte)buffer[i].m_AgeMask,
                });
            }
            return list.ToArray();
        }

        private void WriteSubReplacements(Entity entity, NetUpgradeCommand.SubRep[] subs)
        {
            var resolved = new List<SubReplacement>(subs.Length);
            for (int i = 0; i < subs.Length; i++)
            {
                Entity prefab;
                if (!_prefabIndex.TryResolve(subs[i].PrefabName, out prefab)) continue;
                resolved.Add(new SubReplacement
                {
                    m_Prefab = prefab,
                    m_Type = (SubReplacementType)subs[i].Type,
                    m_Side = (SubReplacementSide)subs[i].Side,
                    m_AgeMask = (global::Game.Tools.AgeMask)subs[i].AgeMask,
                });
            }

            if (resolved.Count == 0)
            {
                if (EntityManager.HasBuffer<SubReplacement>(entity)) EntityManager.RemoveComponent<SubReplacement>(entity);
                return;
            }

            DynamicBuffer<SubReplacement> buffer = EntityManager.HasBuffer<SubReplacement>(entity)
                ? EntityManager.GetBuffer<SubReplacement>(entity)
                : EntityManager.AddBuffer<SubReplacement>(entity);
            buffer.Clear();
            for (int i = 0; i < resolved.Count; i++) buffer.Add(resolved[i]);
        }

        private static string SubRepSig(NetUpgradeCommand.SubRep[] subs)
        {
            if (subs == null || subs.Length == 0) return "";
            var sb = new StringBuilder(subs.Length * 24);
            for (int i = 0; i < subs.Length; i++)
                sb.Append(subs[i].PrefabName).Append(',').Append(subs[i].Type).Append(',')
                  .Append(subs[i].Side).Append(',').Append(subs[i].AgeMask).Append(';');
            return sb.ToString();
        }

        private static string Quant(float3 p) =>
            (long)math.round(p.x * 2f) + "|" + (long)math.round(p.y * 2f) + "|" + (long)math.round(p.z * 2f);

        /// <summary>
        /// Orientation-independent, prefab-free edge identity: the endpoints in a canonical
        /// order (0.5 m buckets). Survives in-place direction flips and road-type replacements,
        /// both of which keep the segment but would invalidate a name- or order-keyed cache.
        /// </summary>
        private static string EdgeKey(float3 a, float3 d)
        {
            bool swap = a.x > d.x || (a.x == d.x && (a.z > d.z || (a.z == d.z && a.y > d.y)));
            return swap ? "netupg|" + Quant(d) + "|" + Quant(a)
                        : "netupg|" + Quant(a) + "|" + Quant(d);
        }

        private static string NodeKey(float3 p) => "netupgn|" + Quant(p);
    }
}
