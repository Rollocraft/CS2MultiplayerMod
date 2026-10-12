using System.Collections.Generic;
using System.Text;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.Infrastructure;
namespace CS2MultiplayerMod.Game.Sync.Systems
{
    public partial class NetUpgradeSyncSystem
    {
        private void Apply(List<NetUpgradeCommand> commands, long now)
        {
            // Commands carry the full state, so the last per target wins.
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

                if (!_prefabIndex.TryResolve(c.PrefabName, out Entity prefab)) continue;
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
                SyncLog.Detail(LogTopic.Nets, "NetUpgradeSync: applied " + applied +
                    " road upgrade(s)" +
                    (edgeTargets.Count + nodeTargets.Count > 0 ? ", " + (edgeTargets.Count + nodeTargets.Count) + " waiting for their segment" : "") +
                    ".");
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

                        // Our edge runs the other way: the game's invert recipe.
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

                        // Record the local state so our capture does not echo it.
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
                            // The game never stores zero flags; removing the last upgrade strips the components.
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
                        // End compositions are chosen per node; re-update them as the game's commit does.
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

                        // A junction node's prefab can differ per machine; position decides when no exact match is near.
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

                    // The game's commit strips runtime traffic-light state; mirror it.
                    if (EntityManager.HasComponent<TrafficLights>(best))
                        EntityManager.RemoveComponent<TrafficLights>(best);

                    // Snapshot connected edges first: adding Updated invalidates the live buffer.
                    NetAttachment.TagParentUpdated(EntityManager, best);

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
                if (!_prefabIndex.TryResolve(subs[i].PrefabName, out Entity prefab)) continue;
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

        /// <summary>Canonical endpoint key: survives direction flips and road-type replacements.</summary>
        private static string EdgeKey(float3 a, float3 d)
        {
            bool swap = a.x > d.x || (a.x == d.x && (a.z > d.z || (a.z == d.z && a.y > d.y)));
            return swap ? "netupg|" + Quant(d) + "|" + Quant(a)
                        : "netupg|" + Quant(a) + "|" + Quant(d);
        }

        private static string NodeKey(float3 p) => "netupgn|" + Quant(p);
    }
}
