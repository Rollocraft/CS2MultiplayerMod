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
    /// <summary>
    /// Receiver for one heterogeneous net-tool Apply. Every target is resolved against the same
    /// live topology before the first definition is created, then delete, replacement, and placement
    /// definitions enter one Temp-backed ApplyTool transaction. A mismatch therefore retries or
    /// recovers the whole gesture instead of leaving a destructive prefix behind.
    /// </summary>
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

        private bool BuildAndArmMixedOperation(SimulationCommandMessage source,
            NetToolOperationCommand operation, NetOperationKey key, long now,
            ref NodePool nodes, ref EdgePool edges, ref NodePool ownedNodes,
            ref EdgePool ownedEdges,
            ref TerrainHeightData heightData, ref WaterSurfaceData<SurfaceWater> waterData,
            bool allowMergedNodeSplit,
            Dictionary<int, PreparedMixedPlacement> placements,
            List<MixedDeleteAction> deleteActions, List<MixedReplaceAction> replaceActions)
        {
            var deleteByItem = GroupMixedDeletes(deleteActions);
            var replaceByItem = GroupMixedReplacements(replaceActions);
            var created = new List<Entity>();
            var realized = new List<RealizedCourse>();
            var replacedOriginalEdges = new HashSet<Entity>();
            for (int i = 0; i < replaceActions.Count; i++)
                replacedOriginalEdges.Add(replaceActions[i].Edge);
            var batchNewNodes = new NativeList<float3>(operation.Items.Length, Allocator.Temp);
            var batchEdges = new NativeList<Bezier4x3>(operation.Items.Length, Allocator.Temp);
            bool commitArmed = false;
            long constructionCost = 0;
            int chargedCourses = 0;
            global::CS2MultiplayerMod.Game.Sync.Systems.DeleteSyncSystem deleteSync =
                World.GetOrCreateSystemManaged<
                    global::CS2MultiplayerMod.Game.Sync.Systems.DeleteSyncSystem>();
            global::CS2MultiplayerMod.Game.Sync.Systems.NetReplaceSyncSystem replaceSync =
                World.GetOrCreateSystemManaged<
                    global::CS2MultiplayerMod.Game.Sync.Systems.NetReplaceSyncSystem>();

            try
            {
                PrepareDefinitionFrame();
                for (int itemIndex = 0; itemIndex < operation.Items.Length; itemIndex++)
                {
                    List<MixedDeleteAction> itemDeletes;
                    if (deleteByItem.TryGetValue(itemIndex, out itemDeletes))
                    {
                        for (int i = 0; i < itemDeletes.Count; i++)
                        {
                            MixedDeleteAction action = itemDeletes[i];
                            Entity definition = deleteSync.CreateAtomicEdgeDeleteDef(action.Edge,
                                action.PrefabName, action.LiveCurve, now);
                            if (definition == Entity.Null)
                                throw new System.InvalidOperationException(
                                    "a preflighted delete target vanished");
                            created.Add(definition);
                        }
                    }

                    List<MixedReplaceAction> itemReplacements;
                    if (replaceByItem.TryGetValue(itemIndex, out itemReplacements))
                    {
                        for (int i = 0; i < itemReplacements.Count; i++)
                        {
                            MixedReplaceAction action = itemReplacements[i];
                            Entity definition = replaceSync.CreateAtomicReplaceDef(action.Edge,
                                action.NewPrefab, action.Invert, action.Course);
                            if (definition == Entity.Null)
                                throw new System.InvalidOperationException(
                                    "a preflighted replacement target vanished");
                            created.Add(definition);
                        }
                    }

                    PreparedMixedPlacement prepared;
                    if (!placements.TryGetValue(itemIndex, out prepared) || prepared.AlreadyBuilt)
                        continue;

                    NetPlacementCommand command = prepared.Command;
                    NetPrefabInfo info = NetInfoOf(prepared.Prefab);
                    Entity startSnap;
                    Entity endSnap;
                    float startT, endT;
                    int startKind, endKind;
                    bool ignoredSurface;
                    if (command.Start.Kind == NetEndpointTargetKind.Infer)
                        startSnap = ClassifyEndpointWithLocalSurface(prepared.Prefab,
                            prepared.Curve.a,
                            new float2(command.Start.ElevationLeft, command.Start.ElevationRight),
                            info, ref nodes, ref edges, ref ownedNodes, batchNewNodes, batchEdges,
                            ref heightData, ref waterData, out startT, out startKind);
                    else if (command.Start.Kind == NetEndpointTargetKind.Free)
                    {
                        startSnap = Entity.Null;
                        startT = 0f;
                        startKind = KindFree;
                    }
                    else if (!TryResolveNativeEndpointWithLocalSurface(prepared.Prefab,
                                 command.Start, info, ref nodes, ref edges, ref ownedNodes,
                                 ref ownedEdges, ref heightData, ref waterData,
                                 allowMergedNodeSplit,
                                 out startSnap, out startT, out startKind, out ignoredSurface))
                        throw new System.InvalidOperationException(
                            "a preflighted start target changed before definition creation");

                    if (command.End.Kind == NetEndpointTargetKind.Infer)
                        endSnap = ClassifyEndpointWithLocalSurface(prepared.Prefab,
                            prepared.Curve.d,
                            new float2(command.End.ElevationLeft, command.End.ElevationRight),
                            info, ref nodes, ref edges, ref ownedNodes, batchNewNodes, batchEdges,
                            ref heightData, ref waterData, out endT, out endKind);
                    else if (command.End.Kind == NetEndpointTargetKind.Free)
                    {
                        endSnap = Entity.Null;
                        endT = 0f;
                        endKind = KindFree;
                    }
                    else if (!TryResolveNativeEndpointWithLocalSurface(prepared.Prefab,
                                 command.End, info, ref nodes, ref edges, ref ownedNodes,
                                 ref ownedEdges, ref heightData, ref waterData,
                                 allowMergedNodeSplit,
                                 out endSnap, out endT, out endKind, out ignoredSurface))
                        throw new System.InvalidOperationException(
                            "a preflighted end target changed before definition creation");

                    // Infer endpoints are classified only while constructing the shared definition
                    // graph. Claim them here as well, so two distinct source targets cannot collapse
                    // onto one receiver edge after the operation-level preflight.
                    if (!TryClaimSplitTarget(command.Start, startSnap, startKind) ||
                        !TryClaimSplitTarget(command.End, endSnap, endKind))
                        throw new System.InvalidOperationException(
                            "different placement targets collapse onto one local edge");
                    if ((startKind == KindSplit && replacedOriginalEdges.Contains(startSnap)) ||
                        (endKind == KindSplit && replacedOriginalEdges.Contains(endSnap)))
                        throw new System.InvalidOperationException(
                            "an inferred placement split targets an edge replaced by the same operation");

                    float startCorrection, endCorrection;
                    float2 startElevation = EndElevation(prepared.Prefab, startSnap, startKind,
                        prepared.Curve.a,
                        new float2(command.Start.ElevationLeft, command.Start.ElevationRight),
                        command.Start.Flags,
                        ref heightData, ref waterData, out startCorrection);
                    float2 endElevation = EndElevation(prepared.Prefab, endSnap, endKind,
                        prepared.Curve.d,
                        new float2(command.End.ElevationLeft, command.End.ElevationRight),
                        command.End.Flags,
                        ref heightData, ref waterData, out endCorrection);
                    TallySurfaceCorrection(startCorrection, endCorrection);

                    Entity placementDefinition = CreateNativeCourse(prepared.Prefab, command,
                        prepared.Curve, startSnap, startT, startKind, startElevation,
                        endSnap, endT, endKind, endElevation);
                    created.Add(placementDefinition);
                    if (startKind == KindFree) batchNewNodes.Add(prepared.Curve.a);
                    if (endKind == KindFree) batchNewNodes.Add(prepared.Curve.d);
                    if (!prepared.Point) batchEdges.Add(prepared.Curve);
                    realized.Add(new RealizedCourse
                    {
                        Prefab = prepared.Prefab,
                        PrefabName = command.PrefabName,
                        Curve = prepared.Curve,
                        Length = command.Length,
                        Charge = ((((CoursePosFlags)command.Start.Flags |
                                    (CoursePosFlags)command.End.Flags) &
                                   CoursePosFlags.DontCreate) == 0),
                        StartSnap = startSnap,
                        EndSnap = endSnap,
                        StartT = startT,
                        EndT = endT,
                        StartKind = startKind,
                        EndKind = endKind,
                    });
                }

                if (created.Count == 0)
                {
                    CancelPreparedDefinitionFrame();
                    _completedNetOperations.Remember(key, now, 60000);
                    Diagnostics.FlightRecorder.Note("net mixed operation already present op=" +
                                                      operation.OperationId);
                    return false;
                }

                for (int i = 0; i < realized.Count; i++)
                {
                    if (!realized[i].Charge) continue;
                    constructionCost += ConstructionCharger.CalculateNetCost(EntityManager,
                        realized[i].Prefab, realized[i].Length);
                    chargedCourses++;
                }

                var replay = new List<SimulationCommandMessage>(1) { source };
                bool armed = ArmNetCommit(delegate { RequeueAtFront(replay); }, delegate
                {
                    long completedNow = Mod.Service != null ? Mod.Service.NowMs : now;
                    _completedNetOperations.Remember(key, completedNow, 60000);
                    Diagnostics.FlightRecorder.Note("net mixed operation committed/drained op=" +
                                                      operation.OperationId);
                }, "mixed operation n=" + created.Count);
                if (!armed)
                    throw new System.InvalidOperationException(
                        "the net transaction coordinator became busy while arming");
                commitArmed = true;

                _pendingNetConstructionCharge = constructionCost;
                _pendingNetConstructionChargeCourses = chargedCourses;
                for (int i = 0; i < deleteActions.Count; i++)
                    deleteSync.MarkAtomicEdgeDelete(deleteActions[i].PrefabName,
                        deleteActions[i].LiveCurve, now);
                for (int i = 0; i < replaceActions.Count; i++)
                    replaceSync.AdoptAtomicReplacement(replaceActions[i].Edge,
                        replaceActions[i].NewPrefab, replaceActions[i].Course);
                for (int i = 0; i < realized.Count; i++)
                {
                    RealizedCourse course = realized[i];
                    MarkRealizeGuards(course.PrefabName, course.Curve.a, course.Curve.d,
                        course.StartSnap, course.StartKind, course.StartT,
                        course.EndSnap, course.EndKind, course.EndT, now);
                    RecordRealizedSpan(course.Curve);
                    _rzSegments++;
                    TallyEnd(course.StartKind);
                    TallyEnd(course.EndKind);
                }
                return true;
            }
            catch (System.Exception ex)
            {
                if (!commitArmed)
                {
                    for (int i = 0; i < created.Count; i++)
                        if (EntityManager.Exists(created[i])) EntityManager.DestroyEntity(created[i]);
                    CancelPreparedDefinitionFrame();
                }
                _operationBuildFailures.Remove(key);
                Mod.log.Warn("[MP] NetSync: mixed operation " + operation.OperationId +
                             (commitArmed ? " failed after its atomic commit was armed: " :
                                 " rolled back before generation: ") + ex.Message +
                             "; requesting world recovery.");
                Diagnostics.FlightRecorder.Note((commitArmed ?
                    "net mixed operation post-arm failure/resync op=" :
                    "net mixed operation rollback/resync op=") + operation.OperationId);
                SyncInbox.RequestResync(CS2MultiplayerMod.Game.Diagnostics.ResyncReport
                    .Create("mixed net operation could not be generated atomically", "net",
                        CS2MultiplayerMod.Game.Diagnostics.ResyncEvidence.Contradiction)
                    .About("mixed operation generation")
                    .Tried("nothing - the operation threw while being generated and cannot be rebuilt identically"));
                return commitArmed;
            }
            finally
            {
                batchNewNodes.Dispose();
                batchEdges.Dispose();
            }
        }

        private void HandleMixedPreflightFailure(SimulationCommandMessage source,
            NetToolOperationCommand operation, NetOperationKey key, long now,
            string reason, bool deterministic)
        {
            int windows = 0;
            if (!deterministic)
            {
                if (HoldUnresolvedOperation(key, now, operation.OperationId, reason, out windows))
                {
                    // Behind other senders' work, not in front of it - see RequeueStalledOperation.
                    RequeueStalledOperation(new List<SimulationCommandMessage>(1) { source });
                    return;
                }
            }

            Diagnostics.ResyncReport report = Diagnostics.ResyncReport
                .Create(UnresolvedMixedTargetReason, "net",
                    deterministic
                        ? Diagnostics.ResyncEvidence.Contradiction
                        : Diagnostics.ResyncEvidence.MissingTarget)
                .About(MixedOperationSubject(operation.OperationId, key.Origin))
                .Tried(deterministic
                    ? "nothing - this rejection cannot change on a retry"
                    : "re-preflighted the whole operation every frame for " +
                      (NativeTargetRetryWindowMs / 1000) + " s across " + windows + " window(s)")
                .Fact("what could not be preflighted", reason)
                .Fact("items in the operation", operation.Items != null ? operation.Items.Length : 0);

            if (SyncInbox.Settle(report) == Diagnostics.ResyncVerdict.Held)
            {
                ExtendUnresolvedOperation(key, now);
                RequeueStalledOperation(new List<SimulationCommandMessage>(1) { source });
                return;
            }

            _nativeOperationHolds.Remove(key);
            Diagnostics.FlightRecorder.Note("net mixed operation rejected/resync op=" +
                                              operation.OperationId);
        }

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
