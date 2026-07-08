using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Sync.Commands;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    // Realize (client) side of NetReplaceSyncSystem: for each remote replacement, find every local edge
    // lying on the replaced curve and rebuild it with the new prefab via the game's own replacement
    // definition, committed on NetSync's ApplyTool pipeline (same as the bulldoze path).
    public partial class NetReplaceSyncSystem
    {
        /// <summary>Called by <see cref="SyncRealizeSystem"/> during ToolUpdate (see there for why).</summary>
        public void RealizePending()
        {
            MultiplayerService service = Mod.Service;
            if (service == null) return;

            MultiplayerSession session = service.Session;
            if (!service.GameplaySyncReady) return;

            // The net pipeline commits ONE batch at a time (shared with build + bulldoze); a build and a
            // replace of the same edge in one ApplyTool pass can make ApplyNetSystem deref a stale edge
            // and native-crash. While a batch is in flight (or on the frame the player's own gesture
            // applies), leave incoming commands and retries queued for the next cycle — RealizePending
            // runs after DeleteSync, so a delete armed this frame defers us. A build tool merely being
            // out no longer defers: the def-frame hijack (NetSync.PrepareDefinitionFrame) makes the
            // commit safe with any tool active.
            if (_netSync == null || !_netSync.CanBuildDefinitions) return;

            long now = service.NowMs;
            List<(NetReplaceCommand cmd, long deadline)> work = null;

            // Replacements handed back by NetSync (their armed commit was wiped before it could
            // run) replay first. They get a fresh retry window: their edge existed when the
            // commit was armed, but a concurrent delete can have removed it since — the deadline
            // keeps such a replay from retrying forever.
            if (_replayCommands.Count > 0)
            {
                work = new List<(NetReplaceCommand, long)>();
                for (int i = 0; i < _replayCommands.Count; i++)
                    work.Add((_replayCommands[i], now + RetryWindowMs));
                _replayCommands.Clear();
            }

            // Then retries (older), then fresh arrivals. Each retry keeps its ORIGINAL
            // deadline — re-stamping it on every cycle would make the window reset forever
            // and an unmatchable replacement would rescan all live edges every frame for good.
            if (_retry.Count > 0)
            {
                if (work == null) work = new List<(NetReplaceCommand, long)>();
                for (int i = 0; i < _retry.Count; i++)
                {
                    if (_retry[i].deadline > now) work.Add((_retry[i].command, _retry[i].deadline));
                }
                _retry.Clear();
            }

            SimulationCommandMessage message;
            while (_incoming.TryDequeue(out message))
            {
                if (message.OriginPlayerId == session.LocalPlayerId) continue;
                try
                {
                    (work ?? (work = new List<(NetReplaceCommand, long)>()))
                        .Add((NetReplaceCommand.Decode(message.Body), now + RetryWindowMs));
                }
                catch (System.Exception ex) { Mod.log.Warn("[MP] NetReplaceSync: dropping malformed command: " + ex.Message); }
            }

            if (work != null && work.Count > 0) Apply(work, now);
        }

        private void Apply(List<(NetReplaceCommand cmd, long deadline)> commands, long now)
        {
            var targets = new List<(Entity newPrefab, Bezier4x3 oldCurve, Bezier4x3 newCurve, bool flipped,
                NetReplaceCommand cmd, long deadline)>();
            for (int i = 0; i < commands.Count; i++)
            {
                Entity newPrefab;
                if (_prefabIndex.TryResolve(commands[i].cmd.PrefabName, out newPrefab))
                {
                    Bezier4x3 oldCurve = OldCurveOf(commands[i].cmd);
                    Bezier4x3 newCurve = CurveOf(commands[i].cmd);
                    targets.Add((newPrefab, oldCurve, newCurve, RunsOpposite(oldCurve, newCurve),
                        commands[i].cmd, commands[i].deadline));
                }
                else
                    Mod.log.Warn("[MP] NetReplaceSync realize: unknown prefab '" + commands[i].cmd.PrefabName + "'; skipping.");
            }
            if (targets.Count == 0) return;

            int replaced = 0;
            var found = new bool[targets.Count];      // the segment exists locally (so don't retry it)
            var defCreated = new bool[targets.Count]; // a replacement definition was armed for it
            // Match phase first (no structural changes), then build the definitions in one go — the
            // def-frame hijack that makes this safe while a build tool is out wipes preview Temps,
            // and must run once, before the first definition.
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
                        // Match by geometry only, against the OLD curve — the receiver's edge still
                        // carries the pre-replacement type AND still lies on the pre-replacement line;
                        // one replaced span can map to several local sub-edges (subdivided differently).
                        if (!BothEndsOnCurve(liveCurve, targets[t].oldCurve))
                        {
                            // Replay after a successful commit: the edge already moved onto the NEW
                            // curve with the new prefab — the work is done, don't retry the command.
                            if (curPrefab == targets[t].newPrefab && BothEndsOnCurve(liveCurve, targets[t].newCurve))
                            { found[t] = true; break; }
                            continue;
                        }
                        found[t] = true;

                        // Project both local endpoints onto the OLD curve (where this edge lies): their
                        // order gives the edge's direction relative to the old span, their range the
                        // sub-span it covers. The edge must end up running in the NEW curve's committed
                        // direction, so it inverts when exactly one of "runs against the old curve" and
                        // "the replacement flipped the direction" holds (one-ways, in-place flips).
                        float tA, tD;
                        MathUtils.Distance(targets[t].oldCurve.xz, liveCurve.a.xz, out tA);
                        MathUtils.Distance(targets[t].oldCurve.xz, liveCurve.d.xz, out tD);
                        bool invert = (tD < tA) != targets[t].flipped;

                        // The geometry to commit: this edge's sub-span carried over to the NEW curve
                        // (mirrored when the flip reversed the parameterisation), oriented in the final
                        // direction. Adjacent sub-edges share cut points, so their nodes stay shared.
                        float lo = math.min(tA, tD), hi = math.max(tA, tD);
                        Bezier4x3 course = targets[t].flipped
                            ? MathUtils.Cut(targets[t].newCurve, new float2(1f - hi, 1f - lo))
                            : MathUtils.Cut(targets[t].newCurve, new float2(lo, hi));

                        // Already the target type, direction and position — an echo, a replay, or a
                        // sub-edge done earlier. The segment exists (found), nothing to do for it.
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

            if (pending.Count > 0)
            {
                _netSync.PrepareDefinitionFrame();
                for (int i = 0; i < pending.Count; i++)
                {
                    (Entity live, Bezier4x3 liveCurve, Bezier4x3 course, bool invert, int t) = pending[i];
                    if (!CreateReplaceDef(live, targets[t].newPrefab, invert, course)) continue; // gone/invalid this frame
                    // Advance the baseline to the post-commit state (new prefab, new geometry) so the
                    // Updated tag this replacement raises when it commits is not re-detected and
                    // echoed back.
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

            // Hand the just-created replacement definitions to NetSync's ApplyTool commit; they become
            // Temp edges at this frame's Modification and commit next frame (with any tool out — the
            // commit overrides its applyMode). If the apply window expires without Temps, the commands
            // replay: the original edges are untouched, so the re-match recreates the same definitions
            // next cycle (the baseline advance above is idempotent on replay).
            if (replaced > 0)
            {
                var armed = new List<NetReplaceCommand>();
                for (int t = 0; t < targets.Count; t++)
                    if (defCreated[t]) armed.Add(targets[t].cmd);
                _netSync.ArmNetCommit(delegate
                {
                    _replayCommands.AddRange(armed);
                });
            }

            // Whatever segment isn't present locally yet probably races its own placement —
            // retry until its ORIGINAL deadline (never re-stamped, see RealizePending).
            int retried = 0;
            for (int t = 0; t < targets.Count; t++)
                if (!found[t]) { _retry.Add((targets[t].cmd, targets[t].deadline)); retried++; }

            if (replaced > 0 || retried > 0)
                Mod.Verbose("[MP] NetReplaceSync: replaced " + replaced + " road segment(s)" +
                             (retried > 0 ? ", " + retried + " waiting for their segment" : "") + ".");
        }

        /// <summary>
        /// Build one replacement definition for <paramref name="edge"/>: a NON-Permanent
        /// <see cref="CreationDefinition"/> (<c>m_Original</c> = the edge, <c>m_Prefab</c> = the new
        /// prefab, <see cref="CreationFlags.Align"/> | <see cref="CreationFlags.SubElevation"/>) plus a
        /// <see cref="NetCourse"/> on <paramref name="curve"/> - exactly what the net tool's
        /// <c>CreateReplacement</c> emits. The curve is the sender's committed geometry (its sub-span
        /// for this edge), oriented in the final direction, so the in-place update also MOVES the edge
        /// when the replacement shifted the centerline - the course keeps the edge's own node entities,
        /// which the commit drags to the new positions like the tool does. With
        /// <paramref name="invert"/> the tool's flip recipe is mirrored too:
        /// <see cref="CreationFlags.Invert"/> set and the course's node references swapped (the curve
        /// already runs the final way), which also flips asymmetric upgrades and compositions natively.
        /// GenerateEdgesSystem turns it into a Temp edge (TempFlags.Modify) and ApplyNetSystem (driven
        /// by NetSync's commit) rewrites the edge in place - new lanes/composition/direction/geometry,
        /// existing upgrades preserved. Returns false (skipping) if the edge vanished or lacks its
        /// geometry.
        /// </summary>
        private bool CreateReplaceDef(Entity edge, Entity newPrefab, bool invert, Bezier4x3 curve)
        {
            if (!EntityManager.Exists(edge) || EntityManager.HasComponent<Deleted>(edge)) return false;
            if (!EntityManager.HasComponent<Curve>(edge) || !EntityManager.HasComponent<Edge>(edge)) return false;
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
                // Carry the edge's committed elevations into the course (per end, after any swap) —
                // without them the in-place rewrite of an elevated/underground net (power line, pipe,
                // bridge) would commit as a GROUND net at that Y and terraform the ground to meet it.
                float2 startElevation = NodeElevation(startNode);
                float2 endElevation = NodeElevation(endNode);
                Entity def = EntityManager.CreateEntity();
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
                // Self-cleanup: consumed this frame (Updated), swept at frame end (Deleted) — same
                // recipe as the build path's courses; stale definitions must not linger.
                EntityManager.AddComponent<Deleted>(def);
                return true;
            }
            catch (System.Exception ex)
            {
                Mod.log.Warn("[MP] NetReplaceSync: failed to build replacement definition: " + ex.Message);
                return false;
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

        /// <summary>
        /// True when the committed curve runs OPPOSITE to the baseline (an in-place direction flip):
        /// the crossed endpoint pairing is the closer one. A width-shifted (but not flipped)
        /// replacement moves both endpoints sideways by the same small amount, so the straight
        /// pairing stays clearly closer.
        /// </summary>
        private static bool RunsOpposite(Bezier4x3 oldCurve, Bezier4x3 newCurve)
        {
            float straight = math.distance(newCurve.a.xz, oldCurve.a.xz) + math.distance(newCurve.d.xz, oldCurve.d.xz);
            float crossed = math.distance(newCurve.a.xz, oldCurve.d.xz) + math.distance(newCurve.d.xz, oldCurve.a.xz);
            return crossed < straight;
        }

        /// <summary>
        /// True when both ends of <paramref name="edge"/> lie within <see cref="EdgeMatchCurveTol"/>
        /// (XZ) of <paramref name="replaced"/>'s curve at a matching height (<see
        /// cref="EdgeMatchCurveTolY"/>) - i.e. the edge is a sub-segment of the replaced span, not a
        /// bridge/tunnel stacked above or below it on the same line. Ranked in XZ so ordinary
        /// terrain-height drift between the two cities never breaks a match.
        /// </summary>
        private static bool BothEndsOnCurve(Bezier4x3 edge, Bezier4x3 replaced)
        {
            float t1, t2;
            if (MathUtils.Distance(replaced.xz, edge.a.xz, out t1) > EdgeMatchCurveTol) return false;
            if (MathUtils.Distance(replaced.xz, edge.d.xz, out t2) > EdgeMatchCurveTol) return false;
            return math.abs(MathUtils.Position(replaced, t1).y - edge.a.y) <= EdgeMatchCurveTolY
                && math.abs(MathUtils.Position(replaced, t2).y - edge.d.y) <= EdgeMatchCurveTolY;
        }
    }
}
