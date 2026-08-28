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
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.Infrastructure;

namespace CS2MultiplayerMod.Game.Sync.Systems.Net
{
    // A mixed net operation - one gesture that places, deletes and replaces network at once. It
    // has to be realized atomically: applying the placements without the deletions leaves the
    // city with both the old road and the new one.
    //
    // This file holds the types, the cycle, and the preflight that decodes the items and checks
    // they can all be satisfied. Matching what the operation mutates is in MixedOperationMatch.cs
    // and building the transaction is in MixedOperationBuild.cs.
    public partial class NetSyncSystem
    {
        private const float MixedMutationTolXZ = 4f;
        private const float MixedMutationTolY = 4f;

        private sealed class PreparedMixedPlacement
        {
            public int ItemIndex;
            public NetPlacementCommand Command;
            public Entity Prefab;
            public Bezier4x3 Curve;
            public float MeasuredLength;
            public bool Point;
            public bool AlreadyBuilt;
            public bool StartExternal;
            public bool EndExternal;
            public Entity StartTarget;
            public Entity EndTarget;
            public float StartT;
            public float EndT;
            public int StartKind;
            public int EndKind;
        }

        private sealed class MixedDeleteTarget
        {
            public int ItemIndex;
            public NetDeleteCommand Command;
            public Entity Prefab;
            public string PrefabName;
            public Bezier4x3 Curve;
        }

        private sealed class MixedReplaceTarget
        {
            public int ItemIndex;
            public NetReplaceCommand Command;
            public Entity NewPrefab;
            public Bezier4x3 OldCurve;
            public Bezier4x3 NewCurve;
            public bool Flipped;
        }

        private struct MixedDeleteAction
        {
            public int ItemIndex;
            public Entity Edge;
            public string PrefabName;
            public Bezier4x3 LiveCurve;
        }

        private struct MixedReplaceAction
        {
            public int ItemIndex;
            public Entity Edge;
            public Entity NewPrefab;
            public Bezier4x3 LiveCurve;
            public Bezier4x3 Course;
            public bool Invert;
            public int TargetIndex;
        }

        private struct MixedMutationClaim
        {
            public ushort CommandId;
            public int TargetIndex;
        }

        private void RealizeMixedNetOperation(MultiplayerSession session,
            SimulationCommandMessage source, NetToolOperationCommand operation, long now)
        {
            if (source.OriginPlayerId == session.LocalPlayerId) return;

            var key = new NetOperationKey
            {
                Origin = source.OriginPlayerId,
                Operation = operation.OperationId,
            };
            if (_completedNetOperations.Contains(key, now))
            {
                Diagnostics.FlightRecorder.Note("net mixed operation duplicate suppressed op=" +
                                                  operation.OperationId);
                return;
            }

            _rzCycleCourses = operation.Items.Length;
            NodePool nodes = default(NodePool), ownedNodes = default(NodePool);
            EdgePool edges = default(EdgePool), ownedEdges = default(EdgePool);
            TerrainHeightData heightData = default(TerrainHeightData);
            WaterSurfaceData<SurfaceWater> waterData = default(WaterSurfaceData<SurfaceWater>);
            bool haveSnapshot = false;
            var placements = new Dictionary<int, PreparedMixedPlacement>();
            var deletes = new List<MixedDeleteTarget>();
            var replacements = new List<MixedReplaceTarget>();
            var deleteActions = new List<MixedDeleteAction>();
            var replaceActions = new List<MixedReplaceAction>();
            string failure = null;
            bool deterministicFailure = false;
            bool definitionsArmed = false;
            bool allowMergedNodeSplit = RelaxedResolveAllowed(key, now);

            try
            {
                TakeNetSnapshot(out nodes, out edges, out ownedNodes, out ownedEdges);
                TakeSurfaceSnapshot(ref heightData, ref waterData);
                haveSnapshot = true;

                if (!DecodeAndPreflightMixedItems(operation, ref nodes, ref edges,
                        ref ownedNodes, ref ownedEdges, ref heightData, ref waterData,
                        allowMergedNodeSplit, placements, deletes, replacements, out failure,
                        out deterministicFailure))
                {
                    HandleMixedPreflightFailure(source, operation, key, now, failure,
                        deterministicFailure);
                    return;
                }

                if (!MatchMixedMutations(ref edges, deletes, replacements,
                        deleteActions, replaceActions, out failure, out deterministicFailure))
                {
                    HandleMixedPreflightFailure(source, operation, key, now, failure,
                        deterministicFailure);
                    return;
                }

                var replacedOriginalEdges = new HashSet<Entity>();
                for (int i = 0; i < replaceActions.Count; i++)
                    replacedOriginalEdges.Add(replaceActions[i].Edge);
                foreach (KeyValuePair<int, PreparedMixedPlacement> pair in placements)
                {
                    PreparedMixedPlacement placement = pair.Value;
                    if (placement.AlreadyBuilt) continue;
                    if ((placement.StartExternal && placement.StartKind == KindSplit &&
                         replacedOriginalEdges.Contains(placement.StartTarget)) ||
                        (placement.EndExternal && placement.EndKind == KindSplit &&
                         replacedOriginalEdges.Contains(placement.EndTarget)))
                    {
                        HandleMixedPreflightFailure(source, operation, key, now,
                            "a placement split targets an edge replaced by the same operation",
                            true);
                        return;
                    }
                }

                ClearOperationHold(key, UnresolvedMixedTargetReason,
                    MixedOperationSubject(operation.OperationId, key.Origin),
                    "the whole mixed operation preflighted on a later attempt");
                _armedNetOperations.Remember(key, now, ArmedOperationWindowMs);
                definitionsArmed = BuildAndArmMixedOperation(source, operation, key, now,
                    ref nodes, ref edges, ref ownedNodes, ref ownedEdges,
                    ref heightData, ref waterData, allowMergedNodeSplit,
                    placements, deleteActions, replaceActions);
            }
            finally
            {
                // Native course generation can complete jobs against these source arrays after the
                // method returns. The ordinary placement path transfers their disposal to the armed
                // commit; keep the mixed path conservative and complete dependencies before release.
                if (haveSnapshot && definitionsArmed) Dependency.Complete();
                if (haveSnapshot)
                {
                    nodes.Dispose();
                    edges.Dispose();
                    ownedNodes.Dispose();
                    ownedEdges.Dispose();
                }
            }
        }

        private bool DecodeAndPreflightMixedItems(NetToolOperationCommand operation,
            ref NodePool nodes, ref EdgePool edges, ref NodePool ownedNodes,
            ref EdgePool ownedEdges, ref TerrainHeightData heightData,
            ref WaterSurfaceData<SurfaceWater> waterData, bool allowMergedNodeSplit,
            Dictionary<int, PreparedMixedPlacement> placements,
            List<MixedDeleteTarget> deletes, List<MixedReplaceTarget> replacements,
            out string failure, out bool deterministicFailure)
        {
            failure = null;
            deterministicFailure = false;
            _batchSplitClaims.Clear();

            for (int i = 0; i < operation.Items.Length; i++)
            {
                NetToolOperationItem item = operation.Items[i];
                if (item.CommandId == NetDeleteCommand.Id)
                {
                    NetDeleteCommand command = NetDeleteCommand.Decode(item.Body);
                    Entity prefab;
                    if (!_prefabIndex.TryResolve(command.PrefabName, out prefab))
                    {
                        failure = "delete prefab '" + command.PrefabName + "' is unavailable";
                        deterministicFailure = true;
                        return false;
                    }
                    deletes.Add(new MixedDeleteTarget
                    {
                        ItemIndex = i,
                        Command = command,
                        Prefab = prefab,
                        PrefabName = command.PrefabName,
                        Curve = DeleteCurveOf(command),
                    });
                    continue;
                }

                if (item.CommandId == NetReplaceCommand.Id)
                {
                    NetReplaceCommand command = NetReplaceCommand.Decode(item.Body);
                    Entity prefab;
                    if (!_prefabIndex.TryResolve(command.PrefabName, out prefab) ||
                        !EntityManager.HasComponent<NetData>(prefab) ||
                        !EntityManager.HasComponent<NetGeometryData>(prefab))
                    {
                        failure = "replacement prefab '" + command.PrefabName + "' is unavailable";
                        deterministicFailure = true;
                        return false;
                    }
                    Bezier4x3 oldCurve = ReplacementOldCurveOf(command);
                    Bezier4x3 newCurve = ReplacementCurveOf(command);
                    replacements.Add(new MixedReplaceTarget
                    {
                        ItemIndex = i,
                        Command = command,
                        NewPrefab = prefab,
                        OldCurve = oldCurve,
                        NewCurve = newCurve,
                        Flipped = MixedRunsOpposite(oldCurve, newCurve),
                    });
                    continue;
                }

                NetPlacementCommand placement = NetPlacementCommand.Decode(item.Body);
                Entity placedPrefab;
                if (!_prefabIndex.TryResolve(placement.PrefabName, out placedPrefab) ||
                    !EntityManager.HasComponent<NetData>(placedPrefab) ||
                    !EntityManager.HasComponent<NetGeometryData>(placedPrefab))
                {
                    failure = "placement prefab '" + placement.PrefabName + "' is unavailable";
                    deterministicFailure = true;
                    return false;
                }
                if (!string.IsNullOrEmpty(placement.SubPrefabName))
                {
                    Entity subPrefab;
                    if (!_prefabIndex.TryResolve(placement.SubPrefabName, out subPrefab) ||
                        !EntityManager.HasComponent<NetLaneData>(subPrefab))
                    {
                        failure = "lane prefab '" + placement.SubPrefabName + "' is unavailable";
                        deterministicFailure = true;
                        return false;
                    }
                }

                Bezier4x3 curve = PlacementCurveOf(placement);
                float measuredLength = MathUtils.Length(curve);
                const uint pointFlags = (uint)(CoursePosFlags.IsFirst | CoursePosFlags.IsLast);
                bool point = measuredLength < NetPlacementCommand.MinCourseLength &&
                             (placement.Start.Flags & pointFlags) == pointFlags &&
                             (placement.End.Flags & pointFlags) == pointFlags;
                if (!math.isfinite(measuredLength) ||
                    (measuredLength < NetPlacementCommand.MinCourseLength && !point) ||
                    math.abs(placement.Length - measuredLength) >
                    math.max(0.05f, measuredLength * 0.01f))
                {
                    failure = "placement course geometry is invalid";
                    deterministicFailure = true;
                    return false;
                }

                const CreationFlags allowed = CreationFlags.Invert | CreationFlags.Align |
                    CreationFlags.Hidden | CreationFlags.Optional | CreationFlags.Lowered |
                    CreationFlags.Native | CreationFlags.Construction | CreationFlags.SubElevation;
                if ((((CreationFlags)placement.CreationFlags) & ~allowed) != 0)
                {
                    failure = "placement contains unsafe creation flags";
                    deterministicFailure = true;
                    return false;
                }

                var prepared = new PreparedMixedPlacement
                {
                    ItemIndex = i,
                    Command = placement,
                    Prefab = placedPrefab,
                    Curve = curve,
                    MeasuredLength = measuredLength,
                    Point = point,
                    StartExternal = HasExternalNativeTarget(placement.Start.Kind),
                    EndExternal = HasExternalNativeTarget(placement.End.Kind),
                    StartKind = KindFree,
                    EndKind = KindFree,
                };
                NetPrefabInfo info = NetInfoOf(placedPrefab);
                bool ignoredSurface;
                bool startResolved = !prepared.StartExternal ||
                    TryResolveNativeEndpointWithLocalSurface(placedPrefab, placement.Start, info,
                        ref nodes, ref edges, ref ownedNodes, ref ownedEdges,
                        ref heightData, ref waterData, allowMergedNodeSplit,
                        out prepared.StartTarget,
                        out prepared.StartT, out prepared.StartKind, out ignoredSurface);
                bool endResolved = !prepared.EndExternal ||
                    TryResolveNativeEndpointWithLocalSurface(placedPrefab, placement.End, info,
                        ref nodes, ref edges, ref ownedNodes, ref ownedEdges,
                        ref heightData, ref waterData, allowMergedNodeSplit,
                        out prepared.EndTarget,
                        out prepared.EndT, out prepared.EndKind, out ignoredSurface);
                if (!startResolved || !endResolved)
                {
                    failure = "a placement endpoint target is not present (" +
                              DescribeUnresolvedEndpoint(placement, startResolved) + ")";
                    return false;
                }

                bool topologyNeedsReplay =
                    (prepared.StartExternal && prepared.StartKind == KindSplit) ||
                    (prepared.EndExternal && prepared.EndKind == KindSplit);
                prepared.AlreadyBuilt = !point && SpanAlreadyBuilt(placedPrefab, curve, ref edges) &&
                                        !topologyNeedsReplay;
                if (!prepared.AlreadyBuilt)
                {
                    if ((prepared.StartExternal &&
                         !TryClaimSplitTarget(placement.Start, prepared.StartTarget,
                             prepared.StartKind)) ||
                        (prepared.EndExternal &&
                         !TryClaimSplitTarget(placement.End, prepared.EndTarget,
                             prepared.EndKind)))
                    {
                        failure = "different placement targets collapse onto one local edge";
                        deterministicFailure = true;
                        return false;
                    }
                }
                placements[i] = prepared;
            }
            return true;
        }
    }
}
