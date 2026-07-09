using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Buildings;
using Game.Common;
using Game.Net;
using Game.Objects;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Game.Sync.Commands;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    public partial class DeleteSyncSystem
    {
        private void RealizeObjectDeletes(List<ObjectDeleteCommand> commands, long now)
        {
            // Resolve prefab names once; an unknown prefab (Entity.Null) still allows the
            // building fallback below to match a levelled growable.
            var targets = new List<(Entity prefab, float3 pos, string name)>();
            for (int i = 0; i < commands.Count; i++)
            {
                Entity prefab;
                _prefabIndex.TryResolve(commands[i].PrefabName, out prefab);
                targets.Add((prefab, new float3(commands[i].PosX, commands[i].PosY, commands[i].PosZ), commands[i].PrefabName));
            }
            if (targets.Count == 0) return;

            float radiusSq = ObjectMatchRadius * ObjectMatchRadius;
            int deleted = 0, unmatched = 0;

            NativeArray<Entity> entities = _liveObjects.ToEntityArray(Allocator.Temp);
            int n = entities.Length;
            var positions = new NativeArray<float3>(n, Allocator.Temp);
            var prefabs = new NativeArray<Entity>(n, Allocator.Temp);
            var taken = new HashSet<Entity>();
            try
            {
                for (int i = 0; i < n; i++)
                {
                    positions[i] = EntityManager.GetComponentData<Transform>(entities[i]).m_Position;
                    prefabs[i] = EntityManager.GetComponentData<PrefabRef>(entities[i]).m_Prefab;
                }

                for (int t = 0; t < targets.Count; t++)
                {
                    // The cross-prefab fallback exists for ONE case: a growable that levelled up
                    // (same lot, new prefab name). It must never widen any other delete into a
                    // building — that is how a stray sim-side delete near a hospital erased the
                    // hospital on this machine and, via the echo below, on the sender's too.
                    bool growableCmd = targets[t].prefab != Entity.Null
                        && EntityManager.HasComponent<BuildingData>(targets[t].prefab)
                        && EntityManager.HasComponent<SpawnableObjectData>(targets[t].prefab);

                    Entity best = Entity.Null;
                    Entity bestPrefab = Entity.Null;
                    float bestDistSq = radiusSq;
                    bool bestExact = false;

                    for (int i = 0; i < n; i++)
                    {
                        Entity e = entities[i];
                        if (taken.Contains(e)) continue;

                        float d = math.distancesq(targets[t].pos, positions[i]);
                        if (d > radiusSq) continue;

                        bool exact = targets[t].prefab != Entity.Null && prefabs[i] == targets[t].prefab;
                        if (!exact && !(growableCmd
                            && EntityManager.HasComponent<Building>(e)
                            && EntityManager.HasComponent<SpawnableObjectData>(prefabs[i]))) continue;

                        // Prefer an exact prefab match; within the same category prefer the nearest.
                        bool better = best == Entity.Null
                            || (exact && !bestExact)
                            || (exact == bestExact && d < bestDistSq);
                        if (better) { best = e; bestPrefab = prefabs[i]; bestDistSq = d; bestExact = exact; }
                    }

                    if (best != Entity.Null)
                    {
                        // Mark with the VICTIM's prefab name — that is the key our own capture
                        // derives from the entity next frame. Marking the command's name instead
                        // left a cross-prefab victim unguarded, so its delete was re-broadcast
                        // and tore down the sender's (different-named) original as well.
                        string victimName = bestExact ? targets[t].name : _prefabSystem.GetPrefabName(bestPrefab);
                        if (string.IsNullOrEmpty(victimName)) victimName = targets[t].name;
                        _guard.Mark(DeleteKey(victimName, EntityManager.GetComponentData<Transform>(best).m_Position), now);
                        EntityManager.AddComponent<Deleted>(best);
                        taken.Add(best);
                        deleted++;
                    }
                    else unmatched++;
                }
            }
            finally
            {
                positions.Dispose();
                prefabs.Dispose();
                entities.Dispose();
            }

            if (deleted > 0 || unmatched > 0)
                Mod.Verbose("[MP] DeleteSync: removed " + deleted + " object(s); " + unmatched +
                             " without a local match.");
        }

        // Endpoint-to-curve match tolerance (metres, XZ). The two cities' roads share the same XZ
        // path but may be split into different edges and drift a little in terrain height, so a few
        // metres in XZ reliably says "this edge lies on the bulldozed segment" without ever reaching
        // a parallel road (a lane is wider than this).
        private const float EdgeMatchCurveTol = 4f;

        // Max height difference (metres) for that match. Roads stack: a bridge can run directly above
        // the bulldozed ground road on the same XZ line — a different LEVEL that must never match.
        // Terrain and curves are both synced, so genuine height drift stays far below this, while
        // stacked levels differ by a full elevation step.
        private const float EdgeMatchCurveTolY = 4f;

        private void RealizeEdgeDeletes(List<NetDeleteCommand> commands, long now)
        {
            var targets = new List<(Entity prefab, Bezier4x3 curve, string name, NetDeleteCommand cmd)>();
            for (int i = 0; i < commands.Count; i++)
            {
                Entity prefab;
                if (_prefabIndex.TryResolve(commands[i].PrefabName, out prefab))
                    targets.Add((prefab, CurveOf(commands[i]), commands[i].PrefabName, commands[i]));
            }
            if (targets.Count == 0) return;

            // Match phase first (no structural changes), then build the delete-definitions in one go.
            // Coverage is against the UNION of the batch's same-prefab curves: one bulldoze can map to
            // several local sub-edges AND — when this machine is LESS subdivided — one local edge can
            // span several of the sender's deleted edges, so each sample point only needs to sit on
            // SOME deleted curve. The midpoint sample keeps a U-shaped edge whose two ENDS happen to
            // rest on the span (a loop) from being torn down.
            var matched = new bool[targets.Count];
            var matchedEdges = new List<(Entity edge, string name, Bezier4x3 curve)>();
            NativeArray<Entity> entities = _liveEdges.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity candidatePrefab = EntityManager.GetComponentData<PrefabRef>(entities[i]).m_Prefab;
                    Bezier4x3 live = EntityManager.GetComponentData<Curve>(entities[i]).m_Bezier;

                    string name;
                    if (!CoveredByBatch(live, candidatePrefab, targets, matched, out name)) continue;
                    matchedEdges.Add((entities[i], name, live));
                }
            }
            finally
            {
                entities.Dispose();
            }

            int deleted = 0;
            if (matchedEdges.Count > 0)
            {
                // Make the frame safe for our definitions while a build tool is out (wipes the
                // player's preview for one frame — see NetSyncSystem.PrepareDefinitionFrame), then
                // build one real bulldoze delete-definition per matched edge: the game's
                // ApplyNetSystem commits it, tearing down the edge's props/lanes, restoring the
                // terrain and recombining nodes. A raw Deleted tag left "lanterns" and sunken road.
                _netSync.PrepareDefinitionFrame();
                for (int i = 0; i < matchedEdges.Count; i++)
                {
                    if (!CreateEdgeDeleteDef(matchedEdges[i].edge)) continue; // gone/invalid this frame
                    _guard.Mark(DeleteKey(matchedEdges[i].name, matchedEdges[i].curve.a), now);
                    deleted++;
                }
            }

            // Hand the just-created delete-definitions to NetSync's ApplyTool commit; they become
            // Temp+Delete edges at this frame's Modification and commit next frame (with any tool
            // out — the commit overrides its applyMode). If the apply window expires without Temps,
            // the matched commands replay: the original edges are still alive, so the re-match
            // recreates the same delete-definitions next cycle.
            if (deleted > 0)
            {
                var armed = new List<NetDeleteCommand>();
                for (int t = 0; t < targets.Count; t++)
                    if (matched[t]) armed.Add(targets[t].cmd);
                _netSync.ArmNetCommit(delegate
                {
                    _replayEdgeDeletes.AddRange(armed);
                });
            }

            int unmatched = 0;
            for (int t = 0; t < matched.Length; t++) if (!matched[t]) unmatched++;
            if (deleted > 0 || unmatched > 0)
            {
                Mod.Verbose("[MP] DeleteSync: bulldozing " + deleted + " road segment(s); " + unmatched +
                             " bulldoze(s) matched no local edge (already gone, or geometry diverged).");
            }
        }

        /// <summary>
        /// Build bulldoze delete-definition for <paramref name="edge"/>: non-Permanent
        /// <see cref="CreationDefinition"/> with <see cref="CreationFlags.Delete"/> and <see cref="NetCourse"/>.
        /// Returns false if edge missing or lacks geometry.
        /// </summary>
        private bool CreateEdgeDeleteDef(Entity edge)
        {
            if (!EntityManager.Exists(edge) || EntityManager.HasComponent<Deleted>(edge)) return false;
            if (!EntityManager.HasComponent<Curve>(edge) || !EntityManager.HasComponent<Edge>(edge)) return false;
            try
            {
                Bezier4x3 curve = EntityManager.GetComponentData<Curve>(edge).m_Bezier;
                Edge ends = EntityManager.GetComponentData<Edge>(edge);
                Entity def = EntityManager.CreateEntity();
                EntityManager.AddComponentData(def, new CreationDefinition
                {
                    m_Original = edge,
                    m_Flags = CreationFlags.Delete,
                });
                EntityManager.AddComponentData(def, new NetCourse
                {
                    m_Curve = curve,
                    m_Length = MathUtils.Length(curve),
                    m_FixedIndex = -1,
                    m_StartPosition = new CoursePos
                    {
                        m_Entity = ends.m_Start,
                        m_Position = curve.a,
                        m_Rotation = NetUtils.GetNodeRotation(MathUtils.StartTangent(curve)),
                        m_CourseDelta = 0f,
                    },
                    m_EndPosition = new CoursePos
                    {
                        m_Entity = ends.m_End,
                        m_Position = curve.d,
                        m_Rotation = NetUtils.GetNodeRotation(MathUtils.EndTangent(curve)),
                        m_CourseDelta = 1f,
                    },
                });
                EntityManager.AddComponent<Updated>(def);
                // Self-cleanup: the definition is consumed this frame (Updated) and swept at frame
                // end (Deleted) — same recipe as the build path's courses. Without it stale
                // definitions linger until a build tool's own destroy pass happens to run.
                EntityManager.AddComponent<Deleted>(def);
                return true;
            }
            catch (System.Exception ex)
            {
                Mod.log.Warn("[MP] DeleteSync: failed to build edge delete-definition: " + ex.Message);
                return false;
            }
        }

        // Reassemble the bulldozed segment's curve from the wire command.
        private static Bezier4x3 CurveOf(NetDeleteCommand cmd) => new Bezier4x3
        {
            a = new float3(cmd.Ax, cmd.Ay, cmd.Az),
            b = new float3(cmd.Bx, cmd.By, cmd.Bz),
            c = new float3(cmd.Cx, cmd.Cy, cmd.Cz),
            d = new float3(cmd.Dx, cmd.Dy, cmd.Dz),
        };

        /// <summary>
        /// True when <paramref name="live"/> endpoints and midpoint lie within <see cref="EdgeMatchCurveTol"/>
        /// (XZ) and <see cref="EdgeMatchCurveTolY"/> (Y) of same-prefab batch curves. Flags matches in
        /// <paramref name="matched"/>, returns prefab name in <paramref name="name"/>.
        /// </summary>
        private static bool CoveredByBatch(Bezier4x3 live, Entity livePrefab,
            List<(Entity prefab, Bezier4x3 curve, string name, NetDeleteCommand cmd)> targets,
            bool[] matched, out string name)
        {
            name = null;
            int hitA = FindCoveringTarget(live.a, livePrefab, targets);
            if (hitA < 0) return false;
            int hitM = FindCoveringTarget(MathUtils.Position(live, 0.5f), livePrefab, targets);
            if (hitM < 0) return false;
            int hitD = FindCoveringTarget(live.d, livePrefab, targets);
            if (hitD < 0) return false;
            matched[hitA] = true;
            matched[hitM] = true;
            matched[hitD] = true;
            name = targets[hitA].name;
            return true;
        }

        private static int FindCoveringTarget(float3 p,
            Entity livePrefab, List<(Entity prefab, Bezier4x3 curve, string name, NetDeleteCommand cmd)> targets)
        {
            for (int t = 0; t < targets.Count; t++)
            {
                if (targets[t].prefab != livePrefab) continue;
                float tt;
                if (MathUtils.Distance(targets[t].curve.xz, p.xz, out tt) > EdgeMatchCurveTol) continue;
                if (math.abs(MathUtils.Position(targets[t].curve, tt).y - p.y) > EdgeMatchCurveTolY) continue;
                return t;
            }
            return -1;
        }
    }
}
