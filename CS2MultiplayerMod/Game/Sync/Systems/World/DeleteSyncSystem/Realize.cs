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
using CS2MultiplayerMod.Game.Sync.Infrastructure;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    public partial class DeleteSyncSystem
    {
        private void RealizeObjectDeletes(List<(ObjectDeleteCommand cmd, long deadline)> commands, long now)
        {
            // Resolve prefab names once, restricted to object prefabs: net, area and stamp
            // collections can expose the same display name, and resolving to one of those left
            // every comparison below unable to match anything the tree could return.
            var targets = new List<(Entity prefab, float3 pos, string name)>();
            for (int i = 0; i < commands.Count; i++)
            {
                Entity prefab;
                _prefabIndex.TryResolve(commands[i].cmd.PrefabName, IsObjectPrefab, out prefab);
                targets.Add((prefab, new float3(commands[i].cmd.PosX, commands[i].cmd.PosY, commands[i].cmd.PosZ),
                    commands[i].cmd.PrefabName));
            }
            if (targets.Count == 0) return;

            float radiusSq = ObjectMatchRadius * ObjectMatchRadius;
            int deleted = 0, deletedOwned = 0, waiting = 0, expired = 0;

            // Candidates come from the game's object search tree (see ObjectSearch), which covers
            // Object+Static and drops Deleted entries — exactly the top-level objects and owned
            // upgrades this match used to walk the whole object domain to find.
            var candidates = new NativeList<Entity>(64, Allocator.Temp);
            var taken = new HashSet<Entity>();
            try
            {
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

                    _objectSearch.CollectNear(targets[t].pos, ObjectMatchRadius, candidates);

                    for (int i = 0; i < candidates.Length; i++)
                    {
                        Entity e = candidates[i];
                        if (taken.Contains(e)) continue;
                        if (!IsDeleteCandidate(e)) continue;

                        float3 position = EntityManager.GetComponentData<Transform>(e).m_Position;
                        float d = math.distancesq(targets[t].pos, position);
                        if (d > radiusSq) continue;

                        Entity candidatePrefab = EntityManager.GetComponentData<PrefabRef>(e).m_Prefab;
                        bool exact = targets[t].prefab != Entity.Null &&
                                     candidatePrefab == targets[t].prefab;
                        if (!exact && !(growableCmd
                            && EntityManager.HasComponent<Building>(e)
                            && EntityManager.HasComponent<SpawnableObjectData>(candidatePrefab))) continue;

                        // Prefer an exact prefab match; within the same category prefer the nearest.
                        bool better = best == Entity.Null
                            || (exact && !bestExact)
                            || (exact == bestExact && d < bestDistSq);
                        if (better) { best = e; bestPrefab = candidatePrefab; bestDistSq = d; bestExact = exact; }
                    }

                    if (best != Entity.Null)
                    {
                        List<Entity> ownedDeleteGraph;
                        string invalidReason;
                        if (!TryCollectObjectDeleteGraph(best, out ownedDeleteGraph,
                                out invalidReason))
                        {
                            if (now < commands[t].deadline)
                            {
                                if (_objectRetry.Count >= MaxPendingDeletes)
                                {
                                    _objectRetry.Clear();
                                    SyncInbox.RequestResync(CS2MultiplayerMod.Game.Diagnostics.ResyncReport
                                        .Create("object delete retry queue overflow", "delete",
                                            CS2MultiplayerMod.Game.Diagnostics.ResyncEvidence.StreamLoss)
                                        .About("object delete retry queue")
                                        .Tried("nothing - the queue was full and was cleared"));
                                }
                                else _objectRetry.Add(commands[t]);
                                waiting++;
                            }
                            else
                            {
                                expired++;
                                SyncInbox.RequestResync(CS2MultiplayerMod.Game.Diagnostics.ResyncReport
                                    .Create("object delete graph validation failed", "delete",
                                        CS2MultiplayerMod.Game.Diagnostics.ResyncEvidence.Contradiction)
                                    .About("object delete graph")
                                    .Tried("nothing - the ownership graph under this object cannot be torn down safely"));
                                Mod.log.Warn("[MP] DeleteSync: rejected stale building graph: " +
                                             invalidReason + ".");
                            }
                            continue;
                        }
                        // Mark with the VICTIM's prefab name — that is the key our own capture
                        // derives from the entity next frame. Marking the command's name instead
                        // left a cross-prefab victim unguarded, so its delete was re-broadcast
                        // and tore down the sender's (different-named) original as well.
                        string victimName = bestExact ? targets[t].name : _prefabSystem.GetPrefabName(bestPrefab);
                        if (string.IsNullOrEmpty(victimName)) victimName = targets[t].name;
                        _guard.Mark(DeleteKey(victimName, EntityManager.GetComponentData<Transform>(best).m_Position), now);

                        // Read the parent before the delete: removing a roundabout island or a turn
                        // sign only drops its effect if the parent re-selects its composition now.
                        Entity attachParent = NetAttachment.GetNetParent(EntityManager, best);

                        // Object-shaped service extensions are not removed merely because their
                        // building receives Deleted. Delete owned descendants deepest-first so the
                        // normal reference and sub-element systems can remove every upgrade,
                        // extension network, and area without leaving an orphan behind.
                        for (int i = ownedDeleteGraph.Count - 1; i >= 0; i--)
                            EntityManager.AddComponent<Deleted>(ownedDeleteGraph[i]);
                        EntityManager.AddComponent<Deleted>(best);
                        if (attachParent != Entity.Null) NetAttachment.TagParentUpdated(EntityManager, attachParent);
                        taken.Add(best);
                        deletedOwned += ownedDeleteGraph.Count;
                        deleted++;
                    }
                    else if (now < commands[t].deadline)
                    {
                        // Its build may simply not have realized here yet — wait for it.
                        if (_objectRetry.Count >= MaxPendingDeletes) _objectRetry.RemoveAt(0);
                        _objectRetry.Add(commands[t]);
                        waiting++;
                    }
                    else
                    {
                        expired++;
                        // Name the target: a delete that never finds a victim means the two cities
                        // disagree about what stands here, and the prefab says which kind.
                        Mod.log.Warn("[MP] DeleteSync: no local match for '" + targets[t].name +
                                     "' at " + targets[t].pos + " within " + ObjectMatchRadius +
                                     "m (" + candidates.Length + " object(s) in range, prefab " +
                                     (targets[t].prefab == Entity.Null ? "unknown here" : "resolved") +
                                     "); dropping this delete.");
                    }
                }
            }
            finally
            {
                candidates.Dispose();
            }

            if (deleted > 0 || waiting > 0 || expired > 0)
                Mod.Verbose("[MP] DeleteSync: removed " + deleted + " object root(s) and " +
                             deletedOwned + " owned upgrade/subobject(s); " + waiting +
                             " awaiting a local match, " + expired + " gave up (already gone, or geometry diverged).");
            // Same reasoning as the road case: a demolition that found nothing to demolish leaves
            // this city holding a building the other player has already removed.
            if (expired > 0)
                Diagnostics.SyncLog.ProdWarn(
                    "Build sync: " + expired + " demolished object(s) had no match here and were " +
                    "dropped after " + (DeleteRetryWindowMs / 1000) + " s. Those objects still " +
                    "stand in this city and no longer stand in the other player's.");
        }

        private bool TryCollectObjectDeleteGraph(Entity root, out List<Entity> ownedObjects,
            out string reason)
        {
            ownedObjects = new List<Entity>();
            var visited = new HashSet<Entity>();
            var pending = new List<Entity> { root };

            while (pending.Count > 0)
            {
                int last = pending.Count - 1;
                Entity owner = pending[last];
                pending.RemoveAt(last);
                if (!visited.Add(owner)) continue;
                if (!EntityManager.Exists(owner) || EntityManager.HasComponent<Deleted>(owner) ||
                    EntityManager.HasComponent<Temp>(owner))
                {
                    reason = owner == root
                        ? "root is no longer live"
                        : "owned object is no longer live";
                    return false;
                }
                if (owner != root) ownedObjects.Add(owner);

                if (EntityManager.HasBuffer<InstalledUpgrade>(owner))
                {
                    DynamicBuffer<InstalledUpgrade> upgrades =
                        EntityManager.GetBuffer<InstalledUpgrade>(owner, isReadOnly: true);
                    for (int i = 0; i < upgrades.Length; i++)
                    {
                        Entity child = upgrades[i].m_Upgrade;
                        if (!ValidateOwnedDeleteElement(owner, child, out reason)) return false;
                        pending.Add(child);
                    }
                }
                if (EntityManager.HasBuffer<global::Game.Objects.SubObject>(owner))
                {
                    DynamicBuffer<global::Game.Objects.SubObject> children =
                        EntityManager.GetBuffer<global::Game.Objects.SubObject>(owner,
                            isReadOnly: true);
                    for (int i = 0; i < children.Length; i++)
                    {
                        Entity child = children[i].m_SubObject;
                        if (!ValidateOwnedDeleteElement(owner, child, out reason)) return false;
                        pending.Add(child);
                    }
                }
                if (EntityManager.HasBuffer<global::Game.Net.SubNet>(owner))
                {
                    DynamicBuffer<global::Game.Net.SubNet> children =
                        EntityManager.GetBuffer<global::Game.Net.SubNet>(owner,
                            isReadOnly: true);
                    for (int i = 0; i < children.Length; i++)
                        if (!ValidateOwnedDeleteElement(owner, children[i].m_SubNet,
                                out reason)) return false;
                }
                if (EntityManager.HasBuffer<global::Game.Areas.SubArea>(owner))
                {
                    DynamicBuffer<global::Game.Areas.SubArea> children =
                        EntityManager.GetBuffer<global::Game.Areas.SubArea>(owner,
                            isReadOnly: true);
                    for (int i = 0; i < children.Length; i++)
                        if (!ValidateOwnedDeleteElement(owner, children[i].m_Area,
                                out reason)) return false;
                }
            }

            reason = null;
            return true;
        }

        /// <summary>
        /// The candidate pools the match runs against: live top-level objects, plus owned service
        /// upgrades so a removal aimed at one can reach that owned entity (the cross-prefab growable
        /// fallback cannot, because an upgrade is not a spawnable building).
        /// </summary>
        private bool IsDeleteCandidate(Entity entity)
        {
            if (!EntityManager.Exists(entity)) return false;
            if (EntityManager.HasComponent<Deleted>(entity) ||
                EntityManager.HasComponent<Temp>(entity)) return false;
            if (!EntityManager.HasComponent<Transform>(entity) ||
                !EntityManager.HasComponent<PrefabRef>(entity)) return false;
            if (!EntityManager.HasComponent<Owner>(entity)) return true;
            return EntityManager.HasComponent<global::Game.Buildings.ServiceUpgrade>(entity) ||
                   EntityManager.HasComponent<Extension>(entity);
        }

        /// <summary>Restricts a name lookup to the object collection. See RealizeObjectDeletes.</summary>
        private bool IsObjectPrefab(Entity prefab)
        {
            return EntityManager.HasComponent<ObjectData>(prefab);
        }

        private bool ValidateOwnedDeleteElement(Entity expectedOwner, Entity child, out string reason)
        {
            reason = null;
            if (child == Entity.Null || !EntityManager.Exists(child) ||
                EntityManager.HasComponent<Deleted>(child) || EntityManager.HasComponent<Temp>(child))
            {
                reason = "owned buffer contains a stale entity";
                return false;
            }
            if (!EntityManager.HasComponent<Owner>(child) ||
                EntityManager.GetComponentData<Owner>(child).m_Owner != expectedOwner)
            {
                reason = "owned buffer and Owner component disagree";
                return false;
            }
            return true;
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

        private void RealizeEdgeDeletes(List<(NetDeleteCommand cmd, long deadline)> commands, long now)
        {
            var targets = new List<(Entity prefab, Bezier4x3 curve, string name, NetDeleteCommand cmd, long deadline)>();
            for (int i = 0; i < commands.Count; i++)
            {
                Entity prefab;
                if (_prefabIndex.TryResolve(commands[i].cmd.PrefabName, out prefab))
                    targets.Add((prefab, CurveOf(commands[i].cmd), commands[i].cmd.PrefabName,
                        commands[i].cmd, commands[i].deadline));
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
                // Reserve the default-tool definition frame, then build one real bulldoze
                // delete-definition per matched edge: the game's
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
                }, "delete n=" + deleted);
            }

            int waiting = 0, expired = 0;
            for (int t = 0; t < matched.Length; t++)
            {
                if (matched[t]) continue;
                if (now < targets[t].deadline)
                {
                    // Its build may simply not have realized here yet — wait for it.
                    if (_edgeRetry.Count >= MaxPendingDeletes) _edgeRetry.RemoveAt(0);
                    _edgeRetry.Add((targets[t].cmd, targets[t].deadline));
                    waiting++;
                }
                else expired++;
            }
            if (deleted > 0 || waiting > 0 || expired > 0)
            {
                Mod.Verbose("[MP] DeleteSync: bulldozing " + deleted + " road segment(s); " + waiting +
                             " awaiting a local match, " + expired +
                             " gave up (already gone, or geometry diverged).");
            }
            // A bulldoze that never found its road is a road the other player no longer has and
            // this one still does - a silent divergence, and one that surfaces later as somebody
            // else's edit failing to resolve. It was only ever visible with verbose logging on,
            // which is exactly the switch nobody has set during the session that needs explaining.
            // Production level, always.
            if (expired > 0)
                Diagnostics.SyncLog.ProdWarn(
                    "Road sync: " + expired + " bulldozed road segment(s) had no match here and " +
                    "were dropped after " + (DeleteRetryWindowMs / 1000) + " s. Those roads still " +
                    "stand in this city and no longer stand in the other player's.");
        }

        /// <summary>
        /// Build bulldoze delete-definition for <paramref name="edge"/>: non-Permanent
        /// <see cref="CreationDefinition"/> with <see cref="CreationFlags.Delete"/> and <see cref="NetCourse"/>.
        /// Returns false if edge missing or lacks geometry.
        /// </summary>
        private bool CreateEdgeDeleteDef(Entity edge)
        {
            return CreateEdgeDeleteDefEntity(edge) != Entity.Null;
        }

        /// <summary>Add a bulldoze definition to a caller-owned atomic net transaction.</summary>
        internal Entity CreateAtomicEdgeDeleteDef(Entity edge, string prefabName,
            Bezier4x3 liveCurve, long now)
        {
            return CreateEdgeDeleteDefEntity(edge);
        }

        internal void MarkAtomicEdgeDelete(string prefabName, Bezier4x3 liveCurve, long now) =>
            _guard.Mark(DeleteKey(prefabName, liveCurve.a), now);

        private Entity CreateEdgeDeleteDefEntity(Entity edge)
        {
            if (!EntityManager.Exists(edge) || EntityManager.HasComponent<Deleted>(edge)) return Entity.Null;
            if (!EntityManager.HasComponent<Curve>(edge) || !EntityManager.HasComponent<Edge>(edge)) return Entity.Null;
            Entity def = Entity.Null;
            bool completed = false;
            try
            {
                Bezier4x3 curve = EntityManager.GetComponentData<Curve>(edge).m_Bezier;
                Edge ends = EntityManager.GetComponentData<Edge>(edge);
                // A net of repeating fixed elements (dam, fixed roundabout piece) identifies which
                // piece an edge is by this index. Reporting -1 for one names no piece.
                int fixedIndex = EntityManager.HasComponent<global::Game.Net.Fixed>(edge)
                    ? EntityManager.GetComponentData<global::Game.Net.Fixed>(edge).m_Index
                    : -1;
                def = EntityManager.CreateEntity();
                EntityManager.AddComponentData(def, new CreationDefinition
                {
                    m_Original = edge,
                    m_Flags = CreationFlags.Delete,
                });
                EntityManager.AddComponentData(def, new NetCourse
                {
                    m_Curve = curve,
                    m_Length = MathUtils.Length(curve),
                    m_FixedIndex = fixedIndex,
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
                completed = true;
                return def;
            }
            catch (System.Exception ex)
            {
                Mod.log.Warn("[MP] DeleteSync: failed to build edge delete-definition: " + ex.Message);
                return Entity.Null;
            }
            finally
            {
                if (!completed && def != Entity.Null && EntityManager.Exists(def))
                    EntityManager.DestroyEntity(def);
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
            List<(Entity prefab, Bezier4x3 curve, string name, NetDeleteCommand cmd, long deadline)> targets,
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
            Entity livePrefab, List<(Entity prefab, Bezier4x3 curve, string name, NetDeleteCommand cmd, long deadline)> targets)
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
