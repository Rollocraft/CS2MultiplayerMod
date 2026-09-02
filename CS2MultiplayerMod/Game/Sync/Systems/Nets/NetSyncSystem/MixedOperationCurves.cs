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
    // The curve arithmetic the matching leans on: whether a source curve is covered by the edges
    // standing there now, which way an edge runs along it, and reading a curve back out of each
    // kind of command.
    public partial class NetSyncSystem
    {
        private static Dictionary<int, List<MixedDeleteAction>> GroupMixedDeletes(
            List<MixedDeleteAction> actions)
        {
            var result = new Dictionary<int, List<MixedDeleteAction>>();
            for (int i = 0; i < actions.Count; i++)
            {
                List<MixedDeleteAction> list;
                if (!result.TryGetValue(actions[i].ItemIndex, out list))
                {
                    list = new List<MixedDeleteAction>();
                    result[actions[i].ItemIndex] = list;
                }
                list.Add(actions[i]);
            }
            return result;
        }

        private static Dictionary<int, List<MixedReplaceAction>> GroupMixedReplacements(
            List<MixedReplaceAction> actions)
        {
            var result = new Dictionary<int, List<MixedReplaceAction>>();
            for (int i = 0; i < actions.Count; i++)
            {
                List<MixedReplaceAction> list;
                if (!result.TryGetValue(actions[i].ItemIndex, out list))
                {
                    list = new List<MixedReplaceAction>();
                    result[actions[i].ItemIndex] = list;
                }
                list.Add(actions[i]);
            }
            return result;
        }

        private static int FindMixedDeleteCover(float3 point, Entity prefab,
            List<MixedDeleteTarget> targets)
        {
            for (int i = 0; i < targets.Count; i++)
            {
                if (targets[i].Prefab != prefab) continue;
                float t;
                if (MathUtils.Distance(targets[i].Curve.xz, point.xz, out t) >
                    MixedMutationTolXZ) continue;
                if (math.abs(MathUtils.Position(targets[i].Curve, t).y - point.y) <=
                    MixedMutationTolY) return i;
            }
            return -1;
        }

        private bool MixedCurveCoveredByEdges(Bezier4x3 sourceCurve, Entity requiredPrefab,
            List<Entity> edges, List<Bezier4x3> curves)
        {
            for (int sample = 0; sample <= 4; sample++)
            {
                float3 point = MathUtils.Position(sourceCurve, sample / 4f);
                bool covered = false;
                for (int i = 0; i < curves.Count; i++)
                {
                    if (requiredPrefab != Entity.Null &&
                        EntityManager.GetComponentData<PrefabRef>(edges[i]).m_Prefab != requiredPrefab)
                        continue;
                    if (MixedPointOnCurve(point, curves[i]))
                    {
                        covered = true;
                        break;
                    }
                }
                if (!covered) return false;
            }
            return true;
        }

        private bool MixedCurveCoveredByPrefab(Bezier4x3 sourceCurve, Entity prefab,
            ref EdgePool edges)
        {
            for (int sample = 0; sample <= 4; sample++)
            {
                float3 point = MathUtils.Position(sourceCurve, sample / 4f);
                bool covered = false;
                for (int i = 0; i < edges.Entities.Length; i++)
                {
                    if (EntityManager.GetComponentData<PrefabRef>(edges.Entities[i]).m_Prefab != prefab)
                        continue;
                    if (MixedPointOnCurve(point, edges.Curves[i].m_Bezier))
                    {
                        covered = true;
                        break;
                    }
                }
                if (!covered) return false;
            }
            return true;
        }

        private static bool MixedPointOnCurve(float3 point, Bezier4x3 curve)
        {
            float t;
            if (MathUtils.Distance(curve.xz, point.xz, out t) > MixedMutationTolXZ)
                return false;
            return math.abs(MathUtils.Position(curve, t).y - point.y) <= MixedMutationTolY;
        }

        private static bool MixedBothEndsOnCurve(Bezier4x3 edge, Bezier4x3 source)
        {
            return MixedPointOnCurve(edge.a, source) && MixedPointOnCurve(edge.d, source);
        }

        private static bool MixedRunsForwardOnCurve(Bezier4x3 edge, Bezier4x3 source)
        {
            if (!MixedBothEndsOnCurve(edge, source)) return false;
            float startT, endT;
            MathUtils.Distance(source.xz, edge.a.xz, out startT);
            MathUtils.Distance(source.xz, edge.d.xz, out endT);
            return endT >= startT;
        }

        private static bool MixedRunsOpposite(Bezier4x3 oldCurve, Bezier4x3 newCurve)
        {
            float straight = math.distance(newCurve.a.xz, oldCurve.a.xz) +
                             math.distance(newCurve.d.xz, oldCurve.d.xz);
            float crossed = math.distance(newCurve.a.xz, oldCurve.d.xz) +
                            math.distance(newCurve.d.xz, oldCurve.a.xz);
            return crossed < straight;
        }

        private static Bezier4x3 PlacementCurveOf(NetPlacementCommand command) => new Bezier4x3
        {
            a = new float3(command.Ax, command.Ay, command.Az),
            b = new float3(command.Bx, command.By, command.Bz),
            c = new float3(command.Cx, command.Cy, command.Cz),
            d = new float3(command.Dx, command.Dy, command.Dz),
        };

        private static Bezier4x3 DeleteCurveOf(NetDeleteCommand command) => new Bezier4x3
        {
            a = new float3(command.Ax, command.Ay, command.Az),
            b = new float3(command.Bx, command.By, command.Bz),
            c = new float3(command.Cx, command.Cy, command.Cz),
            d = new float3(command.Dx, command.Dy, command.Dz),
        };

        private static Bezier4x3 ReplacementCurveOf(NetReplaceCommand command) => new Bezier4x3
        {
            a = new float3(command.Ax, command.Ay, command.Az),
            b = new float3(command.Bx, command.By, command.Bz),
            c = new float3(command.Cx, command.Cy, command.Cz),
            d = new float3(command.Dx, command.Dy, command.Dz),
        };

        private static Bezier4x3 ReplacementOldCurveOf(NetReplaceCommand command) => new Bezier4x3
        {
            a = new float3(command.OldAx, command.OldAy, command.OldAz),
            b = new float3(command.OldBx, command.OldBy, command.OldBz),
            c = new float3(command.OldCx, command.OldCy, command.OldCz),
            d = new float3(command.OldDx, command.OldDy, command.OldDz),
        };
    }
}
