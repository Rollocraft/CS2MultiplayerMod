using System.Collections.Generic;
using Colossal.Mathematics;
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
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.Infrastructure;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    public partial class NetReplaceSyncSystem
    {
        /// <summary>Called by <see cref="SyncRealizeSystem"/> during ToolUpdate (see there for why).</summary>
        public void RealizePending()
        {
            if (RealizeGate.TerrainBacklog) return;

            MultiplayerService service = Mod.Service;
            if (service == null) return;

            MultiplayerSession session = service.Session;
            if (!service.GameplaySyncReady) return;

            // One net batch per commit (a build and replace of one edge together can crash ApplyNetSystem),
            // and a replacement waits behind a placement still waiting on its road.
            if (RealizeGate.NetMutationHeld || _netSync == null || !_netSync.CanBuildDefinitions)
            {
                // Locked-out time does not count against the window.
                ExtendPendingReplaceWindows(service.NowMs);
                return;
            }
            _lastReplaceRealizeMs = service.NowMs;

            long now = service.NowMs;
            List<(NetReplaceCommand cmd, long deadline)> work = null;

            // Handed back by NetSync after a wiped commit; a fresh deadline bounds the replay.
            if (_replayCommands.Count > 0)
            {
                work = new List<(NetReplaceCommand, long)>();
                for (int i = 0; i < _replayCommands.Count; i++)
                    work.Add((_replayCommands[i], now + RetryWindowMs));
                _replayCommands.Clear();
            }

            // Retries keep their original deadline, or an unmatchable one would rescan forever.
            if (_retry.Count > 0)
            {
                int expired = 0;
                NetReplaceCommand firstExpired = null;
                if (work == null) work = new List<(NetReplaceCommand, long)>();
                for (int i = 0; i < _retry.Count; i++)
                {
                    if (_retry[i].deadline > now) work.Add((_retry[i].command, _retry[i].deadline));
                    else
                    {
                        if (firstExpired == null) firstExpired = _retry[i].command;
                        expired++;
                    }
                }
                _retry.Clear();
                if (expired > 0)
                {
                    SyncLog.Warn(LogTopic.Nets, "NetReplaceSync: " + expired +
                        " road replacement target(s) did not resolve within " +
                        (RetryWindowMs / 1000) +
                        " s; dropping them and requesting authoritative world recovery.");
                    SyncInbox.RequestResync(Diagnostics.ResyncReport
                        .Create("road replacement target did not resolve", "net",
                            Diagnostics.ResyncEvidence.MissingTarget)
                        .About("'" + firstExpired.PrefabName + "' over the span at (" +
                               firstExpired.OldAx.ToString("F1") + "," +
                               firstExpired.OldAz.ToString("F1") + ")")
                        .Tried("rescanned the city's roads for the replaced span every cycle for " +
                               (RetryWindowMs / 1000) + " s")
                        .Fact("replacements that found no road here", expired));
                }
            }

            while (_incoming.TryDequeue(out SimulationCommandMessage message))
            {
                if (message.OriginPlayerId == session.LocalPlayerId) continue;
                try
                {
                    (work ?? (work = new List<(NetReplaceCommand, long)>()))
                        .Add((NetReplaceCommand.Decode(message.Body), now + RetryWindowMs));
                }
                catch (System.Exception ex) { SyncLog.Warn(LogTopic.Nets, "NetReplaceSync: dropping malformed command: " + ex.Message); }
            }

            if (work != null && work.Count > 0) Apply(work, now);
        }

        private static bool SameCurveNear(Bezier4x3 left, Bezier4x3 right)
        {
            const float toleranceSq = 0.25f * 0.25f;
            return (math.distancesq(left.a, right.a) <= toleranceSq &&
                    math.distancesq(left.b, right.b) <= toleranceSq &&
                    math.distancesq(left.c, right.c) <= toleranceSq &&
                    math.distancesq(left.d, right.d) <= toleranceSq) ||
                   (math.distancesq(left.a, right.d) <= toleranceSq &&
                    math.distancesq(left.b, right.c) <= toleranceSq &&
                    math.distancesq(left.c, right.b) <= toleranceSq &&
                    math.distancesq(left.d, right.a) <= toleranceSq);
        }

        private void ExtendPendingReplaceWindows(long now)
        {
            long frozenMs = _lastReplaceRealizeMs == 0 ? 0 : now - _lastReplaceRealizeMs;
            _lastReplaceRealizeMs = now;
            if (frozenMs <= 0) return;
            for (int i = 0; i < _retry.Count; i++)
                _retry[i] = (_retry[i].command, _retry[i].deadline + frozenMs);
        }

        private void Apply(List<(NetReplaceCommand cmd, long deadline)> commands, long now)
        {
            var targets = new List<(Entity newPrefab, Bezier4x3 oldCurve, Bezier4x3 newCurve, bool flipped,
                NetReplaceCommand cmd, long deadline)>();
            for (int i = 0; i < commands.Count; i++)
            {
                if (_prefabIndex.TryResolve(commands[i].cmd.PrefabName, out Entity newPrefab))
                {
                    Bezier4x3 oldCurve = OldCurveOf(commands[i].cmd);
                    Bezier4x3 newCurve = CurveOf(commands[i].cmd);
                    targets.Add((newPrefab, oldCurve, newCurve, RunsOpposite(oldCurve, newCurve),
                        commands[i].cmd, commands[i].deadline));
                }
                else
                    SyncLog.Warn(LogTopic.Nets, "NetReplaceSync realize: unknown prefab '" +
                        commands[i].cmd.PrefabName + "'; skipping.");
            }
            if (targets.Count == 0) return;

            int replaced = 0;
            var found = new bool[targets.Count];      // the segment exists locally (so don't retry it)
            var exactMatches = new int[targets.Count]; // a direct edit requires one unique road
            var defCreated = new bool[targets.Count]; // a replacement definition was armed for it
            // Match first, then build all definitions after one definition-frame reservation.
            var pending = new List<(Entity live, Bezier4x3 liveCurve, Bezier4x3 course, bool invert, int t)>();
            NativeArray<Entity> entities = _liveEdges.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity live = entities[i];
                    Entity curPrefab = EntityManager.GetComponentData<PrefabRef>(live).m_Prefab;
                    Bezier4x3 liveCurve = EntityManager.GetComponentData<Curve>(live).m_Bezier;

                    for (int t = 0; t < targets.Count; t++)
                    {
                        // Against the old curve and prefab; one span can map to several local sub-edges.
                        if (!(targets[t].cmd.ExactGeometry
                                ? SameCurveNear(liveCurve, targets[t].oldCurve)
                                : BothEndsOnCurve(liveCurve, targets[t].oldCurve)))
                        {
                            // Already on the new curve with the new prefab: done.
                            if (curPrefab == targets[t].newPrefab &&
                                (targets[t].cmd.ExactGeometry
                                    ? SameCurveNear(liveCurve, targets[t].newCurve)
                                    : BothEndsOnCurve(liveCurve, targets[t].newCurve)))
                            {
                                found[t] = true;
                                if (targets[t].cmd.ExactGeometry) exactMatches[t]++;
                                break;
                            }
                            continue;
                        }
                        found[t] = true;
                        if (targets[t].cmd.ExactGeometry) exactMatches[t]++;

                        // Endpoint order on the old curve gives direction and sub-span. Invert when exactly one of "runs
                        // against the old curve" and "the replacement flipped it" holds.
                        MathUtils.Distance(targets[t].oldCurve.xz, liveCurve.a.xz, out float tA);
                        MathUtils.Distance(targets[t].oldCurve.xz, liveCurve.d.xz, out float tD);
                        bool invert = (tD < tA) != targets[t].flipped;

                        // This sub-span carried onto the new curve; neighbours share cut points and nodes.
                        float lo = math.min(tA, tD), hi = math.max(tA, tD);
                        Bezier4x3 course = targets[t].cmd.ExactGeometry
                            ? targets[t].newCurve
                            : targets[t].flipped
                                ? MathUtils.Cut(targets[t].newCurve, new float2(1f - hi, 1f - lo))
                                : MathUtils.Cut(targets[t].newCurve, new float2(lo, hi));

                        if (curPrefab == targets[t].newPrefab && !invert &&
                            BothEndsOnCurve(liveCurve, targets[t].newCurve)) break;

                        pending.Add((live, liveCurve, course, invert, t));
                        break;
                    }
                }
            }
            finally
            {
                entities.Dispose();
            }

            // Two nearly identical curves are ambiguous; never modify both or pick by order.
            for (int t = 0; t < targets.Count; t++)
            {
                if (!targets[t].cmd.ExactGeometry || exactMatches[t] <= 1) continue;
                found[t] = false;
                SyncLog.Warn(LogTopic.Nets, "NetReplaceSync: ambiguous direct geometry target; " +
                    "holding the edit instead of choosing a road.");
            }
            pending.RemoveAll(item => targets[item.t].cmd.ExactGeometry &&
                                      exactMatches[item.t] > 1);

            if (pending.Count > 0)
            {
                _netSync.PrepareDefinitionFrame();
                for (int i = 0; i < pending.Count; i++)
                {
                    (Entity live, Bezier4x3 liveCurve, Bezier4x3 course, bool invert, int t) = pending[i];
                    if (!CreateReplaceDef(live, targets[t].newPrefab, invert, course)) continue; // gone/invalid this frame
                    // Post-commit state, so the commit's Updated tag is not echoed.
                    _edgeBaseline[live] = new EdgeBaseline
                    {
                        Prefab = targets[t].newPrefab,
                        Curve = course,
                    };
                    replaced++;
                    defCreated[t] = true;
                    bool moved = math.distance(liveCurve.a.xz, course.a.xz) > 0.5f ||
                                 math.distance(liveCurve.d.xz, course.d.xz) > 0.5f;
                }
            }

            // Committed through NetSync; if the window expires the originals are untouched and the commands replay.
            if (replaced > 0)
            {
                var armed = new List<NetReplaceCommand>();
                for (int t = 0; t < targets.Count; t++)
                    if (defCreated[t]) armed.Add(targets[t].cmd);
                _netSync.ArmNetCommit(delegate
                {
                    _replayCommands.AddRange(armed);
                }, "replace n=" + replaced);
            }

            // Likely racing its own placement: retry until the original deadline.
            int retried = 0;
            for (int t = 0; t < targets.Count; t++)
                if (!found[t]) { _retry.Add((targets[t].cmd, targets[t].deadline)); retried++; }

            if (replaced > 0 || retried > 0)
                SyncLog.Detail(LogTopic.Nets, "NetReplaceSync: replaced " + replaced +
                    " road segment(s)" +
                    (retried > 0 ? ", " + retried + " waiting for their segment" : "") + ".");
        }

        /// <summary>
        /// One replacement definition, as the net tool's <c>CreateReplacement</c> emits: original = the edge,
        /// new prefab, Align | SubElevation, and a NetCourse on the sender's committed sub-span, which also
        /// moves the edge. With <paramref name="invert"/> the tool's flip recipe is mirrored. ApplyNetSystem
        /// rewrites the edge in place, keeping its upgrades. False if the edge or its geometry is gone.
        /// </summary>
        private bool CreateReplaceDef(Entity edge, Entity newPrefab, bool invert, Bezier4x3 curve) =>
            CreateReplaceDefEntity(edge, newPrefab, invert, curve) != Entity.Null;

        /// <summary>Add a replacement definition to a caller-owned atomic net transaction.</summary>
        internal Entity CreateAtomicReplaceDef(Entity edge, Entity newPrefab, bool invert,
            Bezier4x3 curve) => CreateReplaceDefEntity(edge, newPrefab, invert, curve);

        internal void AdoptAtomicReplacement(Entity edge, Entity newPrefab, Bezier4x3 curve) =>
            _edgeBaseline[edge] = new EdgeBaseline { Prefab = newPrefab, Curve = curve };

        private Entity CreateReplaceDefEntity(Entity edge, Entity newPrefab, bool invert,
            Bezier4x3 curve)
        {
            if (!EntityManager.Exists(edge) || EntityManager.HasComponent<Deleted>(edge)) return Entity.Null;
            if (!EntityManager.HasComponent<Curve>(edge) || !EntityManager.HasComponent<Edge>(edge)) return Entity.Null;
            Entity def = Entity.Null;
            bool completed = false;
            try
            {
                Edge ends = EntityManager.GetComponentData<Edge>(edge);
                Entity startNode = ends.m_Start;
                Entity endNode = ends.m_End;
                CreationFlags flags = CreationFlags.Align | CreationFlags.SubElevation;
                if (invert)
                {
                    flags |= CreationFlags.Invert;
                    startNode = ends.m_End;
                    endNode = ends.m_Start;
                }
                // Committed elevations per end, or an elevated net rewrites as ground and terraforms.
                float2 startElevation = NodeElevation(startNode);
                float2 endElevation = NodeElevation(endNode);
                def = EntityManager.CreateEntity();
                EntityManager.AddComponentData(def, new CreationDefinition
                {
                    m_Original = edge,
                    m_Prefab = newPrefab,
                    m_Flags = flags,
                });
                EntityManager.AddComponentData(def, new NetCourse
                {
                    m_Curve = curve,
                    m_Length = MathUtils.Length(curve),
                    m_FixedIndex = -1,
                    m_StartPosition = new CoursePos
                    {
                        m_Entity = startNode,
                        m_Position = curve.a,
                        m_Rotation = NetUtils.GetNodeRotation(MathUtils.StartTangent(curve)),
                        m_Elevation = startElevation,
                        m_CourseDelta = 0f,
                        m_Flags = CoursePosFlags.IsFirst,
                    },
                    m_EndPosition = new CoursePos
                    {
                        m_Entity = endNode,
                        m_Position = curve.d,
                        m_Rotation = NetUtils.GetNodeRotation(MathUtils.EndTangent(curve)),
                        m_Elevation = endElevation,
                        m_CourseDelta = 1f,
                        m_Flags = CoursePosFlags.IsLast,
                    },
                });
                EntityManager.AddComponent<Updated>(def);
                // Consumed this frame, swept at frame end.
                EntityManager.AddComponent<Deleted>(def);
                completed = true;
                return def;
            }
            catch (System.Exception ex)
            {
                SyncLog.Warn(LogTopic.Nets,
                    "NetReplaceSync: failed to build replacement definition: " + ex.Message);
                return Entity.Null;
            }
            finally
            {
                if (!completed && def != Entity.Null && EntityManager.Exists(def))
                    EntityManager.DestroyEntity(def);
            }
        }

        /// <summary>The committed elevation of <paramref name="node"/> (0 for a ground node).</summary>
        private float2 NodeElevation(Entity node)
        {
            return EntityManager.HasComponent<global::Game.Net.Elevation>(node)
                ? EntityManager.GetComponentData<global::Game.Net.Elevation>(node).m_Elevation
                : default(float2);
        }

        // Reassemble the segment's COMMITTED (post-replacement) curve from the wire command.
        private static Bezier4x3 CurveOf(NetReplaceCommand cmd) => new Bezier4x3
        {
            a = new float3(cmd.Ax, cmd.Ay, cmd.Az),
            b = new float3(cmd.Bx, cmd.By, cmd.Bz),
            c = new float3(cmd.Cx, cmd.Cy, cmd.Cz),
            d = new float3(cmd.Dx, cmd.Dy, cmd.Dz),
        };

        // Reassemble the segment's BASELINE (pre-replacement) curve — where the receiver's edges lie.
        private static Bezier4x3 OldCurveOf(NetReplaceCommand cmd) => new Bezier4x3
        {
            a = new float3(cmd.OldAx, cmd.OldAy, cmd.OldAz),
            b = new float3(cmd.OldBx, cmd.OldBy, cmd.OldBz),
            c = new float3(cmd.OldCx, cmd.OldCy, cmd.OldCz),
            d = new float3(cmd.OldDx, cmd.OldDy, cmd.OldDz),
        };

        /// <summary>The crossed endpoint pairing is closer: a flip. A width shift moves both ends equally.</summary>
        private static bool RunsOpposite(Bezier4x3 oldCurve, Bezier4x3 newCurve)
        {
            float straight = math.distance(newCurve.a.xz, oldCurve.a.xz) + math.distance(newCurve.d.xz, oldCurve.d.xz);
            float crossed = math.distance(newCurve.a.xz, oldCurve.d.xz) + math.distance(newCurve.d.xz, oldCurve.a.xz);
            return crossed < straight;
        }

        /// <summary>
        /// Both ends of <paramref name="edge"/> lie on <paramref name="replaced"/> within the XZ and height
        /// tolerances: a sub-segment, not a stacked level.
        /// </summary>
        private static bool BothEndsOnCurve(Bezier4x3 edge, Bezier4x3 replaced)
        {
            if (MathUtils.Distance(replaced.xz, edge.a.xz, out float t1) > EdgeMatchCurveTol) return false;
            if (MathUtils.Distance(replaced.xz, edge.d.xz, out float t2) > EdgeMatchCurveTol) return false;
            return math.abs(MathUtils.Position(replaced, t1).y - edge.a.y) <= EdgeMatchCurveTolY
                && math.abs(MathUtils.Position(replaced, t2).y - edge.d.y) <= EdgeMatchCurveTolY;
        }
    }
}
