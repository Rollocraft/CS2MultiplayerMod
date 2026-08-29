using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Game.Sync.Commands;

namespace CS2MultiplayerMod.Game.Sync.Systems.Net
{
    // Matching the operation's mutations against local edges, so a delete or replace lands on the
    // piece of network the sender meant.
    public partial class NetSyncSystem
    {
        private bool MatchMixedMutations(ref EdgePool edges,
            List<MixedDeleteTarget> deletes, List<MixedReplaceTarget> replacements,
            List<MixedDeleteAction> deleteActions,
            List<MixedReplaceAction> replaceActions,
            out string failure, out bool deterministicFailure)
        {
            failure = null;
            deterministicFailure = false;
            var claims = new Dictionary<Entity, MixedMutationClaim>();
            var deleteMatchedEdges = new List<Entity>();
            var deleteMatchedCurves = new List<Bezier4x3>();

            // Delete matching keeps the established union semantics: one coarser local edge may span
            // several source deletion curves, but every endpoint and midpoint must be covered.
            for (int e = 0; e < edges.Entities.Length; e++)
            {
                Entity edge = edges.Entities[e];
                Entity prefab = EntityManager.GetComponentData<PrefabRef>(edge).m_Prefab;
                Bezier4x3 live = edges.Curves[e].m_Bezier;
                int a = FindMixedDeleteCover(live.a, prefab, deletes);
                int m = FindMixedDeleteCover(MathUtils.Position(live, 0.5f), prefab, deletes);
                int d = FindMixedDeleteCover(live.d, prefab, deletes);
                if (a < 0 || m < 0 || d < 0) continue;

                int target = math.min(a, math.min(m, d));
                deleteActions.Add(new MixedDeleteAction
                {
                    ItemIndex = deletes[target].ItemIndex,
                    Edge = edge,
                    PrefabName = deletes[target].PrefabName,
                    LiveCurve = live,
                });
                deleteMatchedEdges.Add(edge);
                deleteMatchedCurves.Add(live);
                claims[edge] = new MixedMutationClaim
                {
                    CommandId = NetDeleteCommand.Id,
                    TargetIndex = target,
                };
            }

            for (int i = 0; i < deletes.Count; i++)
            {
                if (!MixedCurveCoveredByEdges(deletes[i].Curve, deletes[i].Prefab,
                        deleteMatchedEdges, deleteMatchedCurves))
                {
                    failure = "a road deletion target is not present in the local topology";
                    return false;
                }
            }

            for (int t = 0; t < replacements.Count; t++)
            {
                MixedReplaceTarget target = replacements[t];
                var matchedEntities = new List<Entity>();
                var matchedCurves = new List<Bezier4x3>();
                var candidateActions = new List<MixedReplaceAction>();
                for (int e = 0; e < edges.Entities.Length; e++)
                {
                    Entity edge = edges.Entities[e];
                    Entity currentPrefab = EntityManager.GetComponentData<PrefabRef>(edge).m_Prefab;
                    Bezier4x3 live = edges.Curves[e].m_Bezier;
                    bool alreadyNew = currentPrefab == target.NewPrefab &&
                                      MixedRunsForwardOnCurve(live, target.NewCurve);
                    if (alreadyNew) continue;
                    if (!MixedBothEndsOnCurve(live, target.OldCurve)) continue;

                    MixedMutationClaim claim;
                    if (claims.TryGetValue(edge, out claim))
                    {
                        failure = claim.CommandId == NetDeleteCommand.Id
                            ? "one local edge is claimed by both delete and replacement members"
                            : "different replacement spans collapse onto one local edge";
                        deterministicFailure = true;
                        return false;
                    }

                    float ta, td;
                    MathUtils.Distance(target.OldCurve.xz, live.a.xz, out ta);
                    MathUtils.Distance(target.OldCurve.xz, live.d.xz, out td);
                    bool invert = (td < ta) != target.Flipped;
                    float lo = math.min(ta, td), hi = math.max(ta, td);
                    Bezier4x3 course = target.Flipped
                        ? MathUtils.Cut(target.NewCurve, new float2(1f - hi, 1f - lo))
                        : MathUtils.Cut(target.NewCurve, new float2(lo, hi));
                    candidateActions.Add(new MixedReplaceAction
                    {
                        ItemIndex = target.ItemIndex,
                        Edge = edge,
                        NewPrefab = target.NewPrefab,
                        LiveCurve = live,
                        Course = course,
                        Invert = invert,
                        TargetIndex = t,
                    });
                    matchedEntities.Add(edge);
                    matchedCurves.Add(live);
                }

                bool oldCovered = MixedCurveCoveredByEdges(target.OldCurve, Entity.Null,
                    matchedEntities, matchedCurves);
                if (!oldCovered)
                {
                    // A replay that committed but whose completion callback has not yet been observed
                    // may already expose the final geometry. Treat only full new-span coverage as done.
                    if (MixedCurveCoveredByPrefab(target.NewCurve, target.NewPrefab, ref edges))
                        continue;
                    failure = "a road replacement target is not present in the local topology";
                    return false;
                }

                if (candidateActions.Count == 0)
                {
                    failure = "a road replacement target resolved without a mutable local edge";
                    return false;
                }

                for (int i = 0; i < candidateActions.Count; i++)
                {
                    MixedReplaceAction action = candidateActions[i];
                    claims[action.Edge] = new MixedMutationClaim
                    {
                        CommandId = NetReplaceCommand.Id,
                        TargetIndex = t,
                    };
                    replaceActions.Add(action);
                }
            }
            return true;
        }
    }
}
