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
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.Infrastructure;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    public partial class DeleteSyncSystem
    {
        private void RealizeObjectDeletes(List<(ObjectDeleteCommand cmd, long deadline)> commands, long now)
        {
            // Object prefabs only: net, area and stamp collections can share a display name.
            var targets = new List<(Entity prefab, float3 pos, string name)>();
            for (int i = 0; i < commands.Count; i++)
            {
                _prefabIndex.TryResolve(commands[i].cmd.PrefabName, IsObjectPrefab, out Entity prefab);
                targets.Add((prefab, new float3(commands[i].cmd.PosX, commands[i].cmd.PosY, commands[i].cmd.PosZ),
                    commands[i].cmd.PrefabName));
            }
            if (targets.Count == 0) return;

            float radiusSq = ObjectMatchRadius * ObjectMatchRadius;
            int deleted = 0, deletedOwned = 0, waiting = 0, expired = 0;

            // The object search tree covers Object+Static and drops Deleted entries.
            var candidates = new NativeList<Entity>(64, Allocator.Temp);
            var taken = new HashSet<Entity>();
            try
            {
                for (int t = 0; t < targets.Count; t++)
                {
                    // Cross-prefab only for a growable that levelled up; never widen a delete into another building.
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
                        if (!TryCollectObjectDeleteGraph(best, out List<Entity> ownedDeleteGraph,
                                out string invalidReason))
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
                                SyncLog.Warn(LogTopic.Buildings,
                                    "DeleteSync: rejected stale building graph: " + invalidReason +
                                    ".");
                            }
                            continue;
                        }
                        // Guard with the victim's name: that is the key our own capture derives next frame.
                        string victimName = bestExact ? targets[t].name : _prefabSystem.GetPrefabName(bestPrefab);
                        if (string.IsNullOrEmpty(victimName)) victimName = targets[t].name;
                        _guard.Mark(DeleteKey(victimName, EntityManager.GetComponentData<Transform>(best).m_Position), now);

                        // Read before deleting: the parent must re-select its composition to drop the effect.
                        Entity attachParent = NetAttachment.GetNetParent(EntityManager, best);

                        // Extensions survive their building's Deleted; delete owned descendants deepest-first.
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
                        SyncLog.Warn(LogTopic.Buildings, "DeleteSync: no local match for '" +
                            targets[t].name + "' at " + targets[t].pos + " within " +
                            ObjectMatchRadius + "m (" + candidates.Length +
                            " object(s) in range, prefab " +
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
                SyncLog.Detail(LogTopic.Buildings, "DeleteSync: removed " + deleted +
                    " object root(s) and " + deletedOwned + " owned upgrade/subobject(s); " +
                    waiting + " awaiting a local match, " + expired +
                    " gave up (already gone, or geometry diverged).");
            if (expired > 0)
                Diagnostics.SyncLog.Warn(LogTopic.Buildings, "Build sync: " + expired +
                    " demolished object(s) had no match here and were " + "dropped after " +
                    (DeleteRetryWindowMs / 1000) + " s. Those objects still " +
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

        /// <summary>Live top-level objects plus owned service upgrades.</summary>
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
        private bool IsObjectPrefab(Entity prefab) => EntityManager.HasComponent<ObjectData>(prefab);

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

        // XZ tolerance: roads may be split differently on each machine, but a lane is wider than this.
        private const float EdgeMatchCurveTol = 4f;

        // Y tolerance: a bridge over the bulldozed road differs by a full elevation step.
        private const float EdgeMatchCurveTolY = 4f;

        private void RealizeEdgeDeletes(List<(NetDeleteCommand cmd, long deadline)> commands, long now)
        {
            var targets = new List<(Entity prefab, Bezier4x3 curve, string name, NetDeleteCommand cmd, long deadline)>();
            for (int i = 0; i < commands.Count; i++)
            {
                if (_prefabIndex.TryResolve(commands[i].cmd.PrefabName, out Entity prefab))
                    targets.Add((prefab, CurveOf(commands[i].cmd), commands[i].cmd.PrefabName,
                        commands[i].cmd, commands[i].deadline));
            }
            if (targets.Count == 0) return;

            // Match first, then build all delete definitions. Coverage is against the union of the batch's
            // same-prefab curves, since subdivision differs per machine. The midpoint sample protects loops.
            var matched = new bool[targets.Count];
            var matchedEdges = new List<(Entity edge, string name, Bezier4x3 curve)>();
            NativeArray<Entity> entities = _liveEdges.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity candidatePrefab = EntityManager.GetComponentData<PrefabRef>(entities[i]).m_Prefab;
                    Bezier4x3 live = EntityManager.GetComponentData<Curve>(entities[i]).m_Bezier;

                    if (!CoveredByBatch(live, candidatePrefab, targets, matched, out string name)) continue;
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
                // A real delete definition: ApplyNetSystem removes props and lanes, restores terrain and
                // recombines nodes, which a raw Deleted tag does not.
                _netSync.PrepareDefinitionFrame();
                for (int i = 0; i < matchedEdges.Count; i++)
                {
                    if (!CreateEdgeDeleteDef(matchedEdges[i].edge)) continue; // gone/invalid this frame
                    _guard.Mark(DeleteKey(matchedEdges[i].name, matchedEdges[i].curve.a), now);
                    deleted++;
                }
            }

            // Committed through NetSync's ApplyTool pass. If the window expires the originals are still
            // alive, so the commands replay and re-match.
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
                SyncLog.Detail(LogTopic.Buildings, "DeleteSync: bulldozing " + deleted +
                    " road segment(s); " + waiting + " awaiting a local match, " + expired +
                    " gave up (already gone, or geometry diverged).");
            }
            // Always logged: an unmatched bulldoze is a divergence that surfaces later elsewhere.
            if (expired > 0)
                Diagnostics.SyncLog.Warn(LogTopic.Buildings, "Road sync: " + expired +
                    " bulldozed road segment(s) had no match here and " + "were dropped after " +
                    (DeleteRetryWindowMs / 1000) + " s. Those roads still " +
                    "stand in this city and no longer stand in the other player's.");
        }

        /// <summary>Bulldoze delete definition for <paramref name="edge"/>; false if it has no geometry.</summary>
        private bool CreateEdgeDeleteDef(Entity edge) => CreateEdgeDeleteDefEntity(edge) != Entity.Null;

        /// <summary>Add a bulldoze definition to a caller-owned atomic net transaction.</summary>
        internal Entity CreateAtomicEdgeDeleteDef(Entity edge, string prefabName,
            Bezier4x3 liveCurve, long now) => CreateEdgeDeleteDefEntity(edge);

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
                // Which piece of a repeating fixed-element net (dam, fixed roundabout) this edge is.
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
                // Consumed this frame, swept at frame end, like the build path's courses.
                EntityManager.AddComponent<Deleted>(def);
                completed = true;
                return def;
            }
            catch (System.Exception ex)
            {
                SyncLog.Warn(LogTopic.Buildings,
                    "DeleteSync: failed to build edge delete-definition: " + ex.Message);
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
        /// Endpoints and midpoint of <paramref name="live"/> lie on same-prefab batch curves within the
        /// match tolerances; flags <paramref name="matched"/>.
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
                if (MathUtils.Distance(targets[t].curve.xz, p.xz, out float tt) > EdgeMatchCurveTol) continue;
                if (math.abs(MathUtils.Position(targets[t].curve, tt).y - p.y) > EdgeMatchCurveTolY) continue;
                return t;
            }
            return -1;
        }
    }
}
