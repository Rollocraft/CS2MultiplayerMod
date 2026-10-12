using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Simulation;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.Infrastructure;

namespace CS2MultiplayerMod.Game.Sync.Systems.Net
{
    public partial class NetSyncSystem
    {
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
            var batchNewNodes = new NetBatchNodes();
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
                    if (deleteByItem.TryGetValue(itemIndex, out List<MixedDeleteAction> itemDeletes))
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

                    if (replaceByItem.TryGetValue(itemIndex, out List<MixedReplaceAction> itemReplacements))
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

                    if (!placements.TryGetValue(itemIndex, out PreparedMixedPlacement prepared) ||
                        prepared.AlreadyBuilt)
                        continue;

                    NetPlacementCommand command = prepared.Command;
                    NetPrefabInfo info = NetInfoOf(prepared.Prefab);
                    Entity startSnap;
                    Entity endSnap;
                    float startT, endT;
                    CoursePos? startShared = null, endShared = null;
                    int startKind, endKind;
                    bool ignoredSurface;
                    // The ordinary resolver for Free ends too, so a course beside a replacement shares its end node.
                    if (command.Start.Kind == NetEndpointTargetKind.Infer)
                        startSnap = ClassifyEndpointWithLocalSurface(prepared.Prefab,
                            new float3(command.Start.PosX, command.Start.PosY, command.Start.PosZ),
                            new float2(command.Start.ElevationLeft, command.Start.ElevationRight),
                            command.Start.Flags, info, ref nodes, ref edges, ref ownedNodes, batchNewNodes, batchEdges,
                            ref heightData, ref waterData, out startT, out startKind, out startShared);
                    else if (!TryResolveNativeEndpointWithLocalSurface(prepared.Prefab,
                                 command.Start, info, ref nodes, ref edges, ref ownedNodes,
                                 ref ownedEdges, ref heightData, ref waterData,
                                 allowMergedNodeSplit,
                                 out startSnap, out startT, out startKind, out ignoredSurface))
                        throw new System.InvalidOperationException(
                            "a preflighted start target changed before definition creation");

                    if (command.End.Kind == NetEndpointTargetKind.Infer)
                        endSnap = ClassifyEndpointWithLocalSurface(prepared.Prefab,
                            new float3(command.End.PosX, command.End.PosY, command.End.PosZ),
                            new float2(command.End.ElevationLeft, command.End.ElevationRight),
                            command.End.Flags, info, ref nodes, ref edges, ref ownedNodes, batchNewNodes, batchEdges,
                            ref heightData, ref waterData, out endT, out endKind, out endShared);
                    else if (!TryResolveNativeEndpointWithLocalSurface(prepared.Prefab,
                                 command.End, info, ref nodes, ref edges, ref ownedNodes,
                                 ref ownedEdges, ref heightData, ref waterData,
                                 allowMergedNodeSplit,
                                 out endSnap, out endT, out endKind, out ignoredSurface))
                        throw new System.InvalidOperationException(
                            "a preflighted end target changed before definition creation");

                    // Claim inferred endpoints too, so two source targets cannot collapse onto one edge.
                    if (!TryClaimSplitTarget(command.Start, startSnap, startKind) ||
                        !TryClaimSplitTarget(command.End, endSnap, endKind))
                        throw new System.InvalidOperationException(
                            "different placement targets collapse onto one local edge");
                    if ((startKind == KindSplit && replacedOriginalEdges.Contains(startSnap)) ||
                        (endKind == KindSplit && replacedOriginalEdges.Contains(endSnap)))
                        throw new System.InvalidOperationException(
                            "an inferred placement split targets an edge replaced by the same operation");

                    float2 startElevation = EndElevation(prepared.Prefab, startSnap, startKind,
                        prepared.Curve.a,
                        new float2(command.Start.ElevationLeft, command.Start.ElevationRight),
                        command.Start.Flags,
                        ref heightData, ref waterData, out float startCorrection);
                    float2 endElevation = EndElevation(prepared.Prefab, endSnap, endKind,
                        prepared.Curve.d,
                        new float2(command.End.ElevationLeft, command.End.ElevationRight),
                        command.End.Flags,
                        ref heightData, ref waterData, out float endCorrection);
                    TallySurfaceCorrection(startCorrection, endCorrection);

                    Entity placementDefinition = CreateNativeCourse(prepared.Prefab, command,
                        prepared.Curve, startSnap, startT, startKind, startElevation,
                        endSnap, endT, endKind, endElevation, startShared, endShared);
                    created.Add(placementDefinition);
                    RegisterBatchNodes(placementDefinition, info, startKind, endKind, batchNewNodes);
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
                    SyncLog.Trace(LogTopic.Nets, "net mixed operation already present op=" +
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
                    SyncLog.Trace(LogTopic.Nets, "net mixed operation committed/drained op=" +
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
                SyncLog.Warn(LogTopic.Nets, "NetSync: mixed operation " + operation.OperationId +
                    (commitArmed ? " failed after its atomic commit was armed: " : " rolled back before generation: ") +
                    ex.Message + "; requesting world recovery.");
                SyncInbox.RequestResync(CS2MultiplayerMod.Game.Diagnostics.ResyncReport
                    .Create("mixed net operation could not be generated atomically", "net",
                        CS2MultiplayerMod.Game.Diagnostics.ResyncEvidence.Contradiction)
                    .About("mixed operation generation")
                    .Tried("nothing - the operation threw while being generated and cannot be rebuilt identically"));
                return commitArmed;
            }
            finally
            {
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
            SyncLog.Trace(LogTopic.Nets, "net mixed operation rejected/resync op=" +
                operation.OperationId);
        }
    }
}
