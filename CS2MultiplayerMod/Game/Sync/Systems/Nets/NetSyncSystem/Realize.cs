using System.Collections.Generic;
using Colossal.Collections;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Simulation;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Infrastructure;
using CS2MultiplayerMod.Game.Sync.Commands;
namespace CS2MultiplayerMod.Game.Sync.Systems.Net
{
    // Client realize: drain placement commands, resolve native targets (or classify fallback
    // geometry), and route every course through one serialized Temp+ApplyTool transaction.
    public partial class NetSyncSystem
    {
        private const long OperationAssemblyWindowMs = 3000;

        private struct NetOperationKey : System.IEquatable<NetOperationKey>
        {
            public int Origin;
            public long Operation;

            public bool Equals(NetOperationKey other) =>
                Origin == other.Origin && Operation == other.Operation;

            public override bool Equals(object obj) =>
                obj is NetOperationKey && Equals((NetOperationKey)obj);

            public override int GetHashCode()
            {
                unchecked { return (Origin * 397) ^ Operation.GetHashCode(); }
            }
        }

        private readonly Dictionary<NetOperationKey, long> _operationAssemblyDeadlines =
            new Dictionary<NetOperationKey, long>();

        /// <summary>
        /// What an unresolved operation waits for. <see cref="NativeOperationHold.Relaxed"/> is stored
        /// separately so a second window granted by the arbiter keeps the relaxed matches the first unlocked.
        /// </summary>
        private struct NativeOperationHold
        {
            public long DeadlineMs;
            public bool Relaxed;
            public int Windows;
        }

        private readonly Dictionary<NetOperationKey, NativeOperationHold> _nativeOperationHolds =
            new Dictionary<NetOperationKey, NativeOperationHold>();
        private readonly Dictionary<NetOperationKey, int> _operationBuildFailures =
            new Dictionary<NetOperationKey, int>();

        // Operations armed at least once: a partially present operation recovers a lost commit;
        // seen for the first time, it means the worlds disagree.
        private readonly CS2MultiplayerMod.Core.Sync.OperationReplayWindow<NetOperationKey>
            _armedNetOperations =
                new CS2MultiplayerMod.Core.Sync.OperationReplayWindow<NetOperationKey>();
        private const long ArmedOperationWindowMs = 60000;

        // Local edges this batch will split and the source edge that claimed each (TryClaimSplitTarget).
        private readonly Dictionary<Entity, Bezier4x3> _batchSplitClaims =
            new Dictionary<Entity, Bezier4x3>();

        private struct PreparedNativeCourse
        {
            public NetPlacementCommand Command;
            public Entity Prefab;
            public Bezier4x3 Curve;
            public float MeasuredLength;
            public bool Point;
            public bool AlreadyBuilt;
        }

        private struct LiveEdgeSearchSnapshot
        {
            public NativeQuadTree<Entity, QuadTreeBoundsXZ> Tree;
            public ComponentLookup<Curve> Curves;
            public ComponentLookup<global::Game.Prefabs.PrefabRef> Prefabs;
            public ComponentLookup<global::Game.Common.Owner> Owners;
            public ComponentLookup<Temp> Temps;
            public ComponentLookup<global::Game.Common.Deleted> Deleted;
        }

        private struct SpanCoverageIterator :
            INativeQuadTreeIterator<Entity, QuadTreeBoundsXZ>,
            IUnsafeQuadTreeIterator<Entity, QuadTreeBoundsXZ>
        {
            public Bounds3 Bounds;
            public float3 Point;
            public Entity Prefab;
            public ComponentLookup<Curve> Curves;
            public ComponentLookup<global::Game.Prefabs.PrefabRef> Prefabs;
            public ComponentLookup<global::Game.Common.Owner> Owners;
            public ComponentLookup<Temp> Temps;
            public ComponentLookup<global::Game.Common.Deleted> Deleted;
            public bool Covered;

            public bool Intersect(QuadTreeBoundsXZ bounds) => !Covered && MathUtils.Intersect(bounds.m_Bounds, Bounds);

            public void Iterate(QuadTreeBoundsXZ bounds, Entity entity)
            {
                if (Covered || !MathUtils.Intersect(bounds.m_Bounds, Bounds) ||
                    !Curves.HasComponent(entity) || !Prefabs.HasComponent(entity) ||
                    Owners.HasComponent(entity) || Temps.HasComponent(entity) ||
                    Deleted.HasComponent(entity) || Prefabs[entity].m_Prefab != Prefab) return;

                Bezier4x3 curve = Curves[entity].m_Bezier;
                if (MathUtils.Distance(curve.xz, Point.xz, out float t) > SplitMatch.TolXZ) return;
                Covered = math.abs(MathUtils.Position(curve, t).y - Point.y) <= SplitMatch.TolY;
            }
        }

        private struct RealizedCourse
        {
            public Entity Prefab;
            public string PrefabName;
            public Bezier4x3 Curve;
            public float Length;
            public bool Charge;
            public Entity StartSnap;
            public Entity EndSnap;
            public float StartT;
            public float EndT;
            public int StartKind;
            public int EndKind;
        }

        private void RealizeIncoming(MultiplayerSession session, long now)
        {
            PruneCompletedNetOperations(now);
            if (_incoming.IsEmpty && _remoteDeferred.Count == 0) return;

            // One Temp batch at a time (the next course must see the previous batch's nodes), and never on
            // the frame a local Apply commits.
            if (!CanBuildDefinitions) return;

            // Keep a multi-course operation intact: applying a prefix lets node reduction deform the rest.
            if (!TryTakeCompleteOperation(session, now, out List<SimulationCommandMessage> work,
                out bool nativeOperation,
                    out NetToolOperationCommand mixedOperation)) return;
            if (mixedOperation != null)
            {
                RealizeMixedNetOperation(session, work[0], mixedOperation, now);
                return;
            }

            NetOperationKey completedKey = default(NetOperationKey);
            bool hasCompletedKey = false;
            if (nativeOperation && work.Count > 0)
            {
                NetPlacementCommand completedHeader = NetPlacementCommand.Decode(work[0].Body);
                completedKey = new NetOperationKey
                {
                    Origin = work[0].OriginPlayerId,
                    Operation = completedHeader.OperationId,
                };
                if (_completedNetOperations.Contains(completedKey, now))
                {
                    SyncLog.Trace(LogTopic.Nets, "net operation duplicate suppressed op=" +
                        completedHeader.OperationId);
                    return;
                }
                hasCompletedKey = true;
            }

            int maxBatch = work.Count;
            _rzCycleCourses = work.Count;

            NodePool nodes = default(NodePool), ownedNodes = default(NodePool);
            EdgePool edges = default(EdgePool), ownedEdges = default(EdgePool);
            LiveEdgeSearchSnapshot liveEdgeSearch = default(LiveEdgeSearchSnapshot);
            TerrainHeightData heightData = default;
            WaterSurfaceData<SurfaceWater> waterData = default;
            bool haveSnapshot = false;
            int built = 0;
            bool splitUsed = false;
            // Only after the retry window: a node merely mid-commit comes back on its own.
            bool allowMergedNodeSplit = false;
            PreparedNativeCourse[] preparedNative = nativeOperation
                ? new PreparedNativeCourse[work.Count]
                : null;
            var createdDefinitions = new List<Entity>(work.Count);
            var realizedCourses = new List<RealizedCourse>(work.Count);
            bool abortWholeOperation = false;
            bool abortAliasedSplit = false;
            string abortReason = null;
            long constructionCost = 0;
            int chargedCourses = 0;

            // Source messages kept until the commit runs, re-enqueued if the armed batch is wiped.
            List<SimulationCommandMessage> retained = null;

            // Nodes/edges this batch will create: an endpoint on a pending node merges; one tapping a
            // pending edge waits for the next cycle.
            var batchNewNodes = new NetBatchNodes();
            var batchEdges = new NativeList<Bezier4x3>(maxBatch, Allocator.Temp);
            try
            {
                if (nativeOperation)
                {
                    // Resolve every external target first; committing a prefix would break the junction shape.
                    TakeNetSnapshot(out nodes, out edges, out ownedNodes, out ownedEdges);
                    TakeSurfaceSnapshot(ref heightData, ref waterData);
                    haveSnapshot = true;

                    // Read-only quadtree snapshot for idempotence, instead of scanning the city per course.
                    liveEdgeSearch = new LiveEdgeSearchSnapshot
                    {
                        Tree = _netSearchSystem.GetNetSearchTree(readOnly: true,
                            out JobHandle searchDependencies),
                        Curves = GetComponentLookup<Curve>(isReadOnly: true),
                        Prefabs = GetComponentLookup<global::Game.Prefabs.PrefabRef>(isReadOnly: true),
                        Owners = GetComponentLookup<global::Game.Common.Owner>(isReadOnly: true),
                        Temps = GetComponentLookup<Temp>(isReadOnly: true),
                        Deleted = GetComponentLookup<global::Game.Common.Deleted>(isReadOnly: true),
                    };
                    searchDependencies.Complete();

                    NetPlacementCommand operationHeader = NetPlacementCommand.Decode(work[0].Body);
                    var operationRetryKey = new NetOperationKey
                    {
                        Origin = work[0].OriginPlayerId,
                        Operation = operationHeader.OperationId,
                    };
                    bool unresolvedOperationTarget = false;
                    bool aliasedSplitTarget = false;
                    int alreadyBuiltCourses = 0;
                    string unresolvedDetail = null;
                    NetPlacementCommand unresolvedCommand = null;
                    bool unresolvedStartResolved = false;
                    allowMergedNodeSplit = RelaxedResolveAllowed(operationRetryKey, now);
                    _batchSplitClaims.Clear();

                    for (int i = 0; i < work.Count; i++)
                    {
                        NetPlacementCommand command;
                        try { command = NetPlacementCommand.Decode(work[i].Body); }
                        catch (System.Exception ex)
                        {
                            SyncLog.Warn(LogTopic.Nets,
                                "NetSync: native operation became malformed during preflight: " +
                                ex.Message + "; dropping whole operation.");
                            return;
                        }

                        if (!_prefabIndex.TryResolve(command.PrefabName, out Entity prefab) ||
                            !EntityManager.HasComponent<global::Game.Prefabs.NetData>(prefab) ||
                            !EntityManager.HasComponent<global::Game.Prefabs.NetGeometryData>(prefab))
                        {
                            SyncLog.Warn(LogTopic.Nets,
                                "NetSync: native operation references unavailable net prefab '" +
                                command.PrefabName + "'; dropping whole operation.");
                            return;
                        }
                        if (!string.IsNullOrEmpty(command.SubPrefabName))
                        {
                            if (!_prefabIndex.TryResolve(command.SubPrefabName, out Entity subPrefab) ||
                                !EntityManager.HasComponent<global::Game.Prefabs.NetLaneData>(subPrefab))
                            {
                                SyncLog.Warn(LogTopic.Nets,
                                    "NetSync: native operation references unavailable lane prefab '" +
                                    command.SubPrefabName + "'; dropping whole operation.");
                                return;
                            }
                        }

                        var curve = new Bezier4x3
                        {
                            a = new float3(command.Ax, command.Ay, command.Az),
                            b = new float3(command.Bx, command.By, command.Bz),
                            c = new float3(command.Cx, command.Cy, command.Cz),
                            d = new float3(command.Dx, command.Dy, command.Dz),
                        };
                        float measuredLength = MathUtils.Length(curve);
                        const uint pointFlags = (uint)(global::Game.Tools.CoursePosFlags.IsFirst |
                                                       global::Game.Tools.CoursePosFlags.IsLast);
                        bool nativePoint = measuredLength < NetPlacementCommand.MinCourseLength &&
                                           (command.Start.Flags & pointFlags) == pointFlags &&
                                           (command.End.Flags & pointFlags) == pointFlags;
                        if (!math.isfinite(measuredLength) ||
                            (measuredLength < NetPlacementCommand.MinCourseLength && !nativePoint))
                        {
                            SyncLog.Warn(LogTopic.Nets, "NetSync: native operation " +
                                command.OperationId +
                                " contains a degenerate course; dropping the whole operation.");
                            ReportRefusedNativeOperation(command, i, work.Count,
                                "a course with no usable length",
                                "the transmitted curve measures " +
                                measuredLength.ToString("F3") + " m");
                            return;
                        }

                        // Length is generator state, not a checksum: sanity-bound it, then replay the source value.
                        if (!NativeCourseLengthPolicy.IsPlausible(command.Length, measuredLength, nativePoint))
                        {
                            SyncLog.Warn(LogTopic.Nets, "NetSync: native operation " +
                                command.OperationId +
                                " has an implausible course length; dropping the whole operation.");
                            ReportRefusedNativeOperation(command, i, work.Count,
                                "an implausible native course length",
                                "sent " + command.Length.ToString("F3") + " m, curve measures " +
                                measuredLength.ToString("F3") + " m");
                            return;
                        }

                        const CreationFlags allowedNativeFlags = CreationFlags.Invert |
                            CreationFlags.Align | CreationFlags.Hidden | CreationFlags.Optional |
                            CreationFlags.Lowered | CreationFlags.Native |
                            CreationFlags.Construction | CreationFlags.SubElevation;
                        if ((((CreationFlags)command.CreationFlags) & ~allowedNativeFlags) != 0)
                        {
                            SyncLog.Warn(LogTopic.Nets, "NetSync: native operation " +
                                command.OperationId +
                                " contains an unsafe creation mode; dropping the whole operation.");
                            SyncInbox.RequestResync(Diagnostics.ResyncReport
                                .Create("unsafe native net creation flags", "net",
                                    Diagnostics.ResyncEvidence.StreamLoss)
                                .About("op " + command.OperationId)
                                .Tried("nothing - the operation was refused before it could be built")
                                .Fact("creation flags on the wire", command.CreationFlags));
                            return;
                        }
                        // Exact generator elevations can exceed the UI range; the decoder already bounds them.

                        NetPrefabInfo placedInfo = NetInfoOf(prefab);
                        bool startExternal = HasExternalNativeTarget(command.Start.Kind);
                        bool endExternal = HasExternalNativeTarget(command.End.Kind);
                        bool startResolved = true, endResolved = true;
                        Entity startTarget = Entity.Null, endTarget = Entity.Null;
                        float startT = 0f, endT = 0f;
                        int startKind = KindFree, endKind = KindFree;
                        bool usedLocalSurface;
                        if (startExternal)
                        {
                            startResolved = TryResolveNativeEndpointWithLocalSurface(prefab,
                                command.Start, placedInfo,
                                ref nodes, ref edges, ref ownedNodes, ref ownedEdges,
                                ref heightData, ref waterData, allowMergedNodeSplit,
                                out startTarget, out startT, out startKind,
                                out usedLocalSurface);
                        }
                        if (endExternal)
                        {
                            endResolved = TryResolveNativeEndpointWithLocalSurface(prefab,
                                command.End, placedInfo,
                                ref nodes, ref edges, ref ownedNodes, ref ownedEdges,
                                ref heightData, ref waterData, allowMergedNodeSplit,
                                out endTarget, out endT, out endKind,
                                out usedLocalSurface);
                        }

                        bool resolved = startResolved && endResolved;
                        // By the resolved kind: a node target that matched a merged edge still replays its split.
                        bool topologyNeedsReplay =
                            (startExternal && startResolved && startKind == KindSplit) ||
                            (endExternal && endResolved && endKind == KindSplit);
                        bool geometryAlreadyBuilt = !nativePoint &&
                                                    SpanAlreadyBuilt(prefab, curve, ref liveEdgeSearch);

                        // Geometry alone is not enough: an edge-targeted endpoint also makes a split node the next
                        // operation may target. External node targets must resolve too.
                        bool alreadyBuilt = geometryAlreadyBuilt && resolved && !topologyNeedsReplay;
                        if (alreadyBuilt) alreadyBuiltCourses++;
                        preparedNative[i] = new PreparedNativeCourse
                        {
                            Command = command,
                            Prefab = prefab,
                            Curve = curve,
                            MeasuredLength = measuredLength,
                            Point = nativePoint,
                            AlreadyBuilt = alreadyBuilt,
                        };

                        if (!alreadyBuilt)
                        {
                            if (startExternal && startResolved &&
                                !TryClaimSplitTarget(command.Start, startTarget, startKind))
                                aliasedSplitTarget = true;
                            if (endExternal && endResolved &&
                                !TryClaimSplitTarget(command.End, endTarget, endKind))
                                aliasedSplitTarget = true;
                        }
                        if (!resolved)
                        {
                            unresolvedOperationTarget = true;
                            if (unresolvedDetail == null)
                            {
                                unresolvedDetail = DescribeUnresolvedEndpoint(command, startResolved);
                                unresolvedCommand = command;
                                unresolvedStartResolved = startResolved;
                            }
                        }

                        if (geometryAlreadyBuilt && topologyNeedsReplay)
                            SyncLog.Trace(LogTopic.Nets, "net native topology replay op=" +
                                command.OperationId + " course=" + command.CourseIndex);

                        if (alreadyBuilt) continue;
                    }

                    if (alreadyBuiltCourses == work.Count)
                    {
                        ClearOperationHold(operationRetryKey, UnresolvedNativeTargetReason,
                            NativeOperationSubject(operationHeader.OperationId,
                                operationRetryKey.Origin),
                            "the operation turned out to be already present");
                        _operationBuildFailures.Remove(operationRetryKey);
                        SyncLog.Trace(LogTopic.Nets, "net native op already present=" +
                            operationHeader.OperationId + " courses=" + work.Count);
                        _completedNetOperations.Remember(operationRetryKey, now, 60000);
                        return;
                    }
                    if (alreadyBuiltCourses > 0)
                        SyncLog.Trace(LogTopic.Nets, "net native op reconcile existing=" +
                            alreadyBuiltCourses + "/" + work.Count);

                    if (unresolvedOperationTarget)
                    {
                        if (HoldUnresolvedOperation(operationRetryKey, now,
                                operationHeader.OperationId, unresolvedDetail, out int windows))
                        {
                            // Wait behind work that can still progress, which may build the missing target.
                            RequeueStalledOperation(work);
                            return;
                        }

                        // Window up: describe what is missing and let the arbiter decide before any reload.
                        NetEndpointIntent failedEndpoint = unresolvedCommand == null
                            ? default(NetEndpointIntent)
                            : unresolvedStartResolved ? unresolvedCommand.End : unresolvedCommand.Start;
                        Diagnostics.ResyncReport report = Diagnostics.ResyncReport
                            .Create(UnresolvedNativeTargetReason, "net",
                                Diagnostics.ResyncEvidence.MissingTarget)
                            .About(NativeOperationSubject(operationHeader.OperationId,
                                operationRetryKey.Origin))
                            .Tried("re-resolved the endpoint every frame for " +
                                   (NativeTargetRetryWindowMs / 1000) + " s across " + windows +
                                   " window(s), including the relaxed node/edge fallbacks")
                            .Fact("the other player built", operationHeader.PrefabName)
                            .Fact("courses in the operation", work.Count)
                            .Fact("courses already present here", alreadyBuiltCourses)
                            .Fact("endpoint that would not resolve", unresolvedDetail)
                            .Fact("what is actually here",
                                DescribeLocalAnchorNeighbourhood(failedEndpoint, ref nodes, ref edges))
                            .Fact("net operations still queued", _remoteDeferred.Count + _incoming.Count);

                        if (SyncInbox.Settle(report) == Diagnostics.ResyncVerdict.Held)
                        {
                            // Held: the arbiter froze the feeders that could remove the target, so retry against a still world.
                            ExtendUnresolvedOperation(operationRetryKey, now);
                            RequeueStalledOperation(work);
                            return;
                        }

                        _nativeOperationHolds.Remove(operationRetryKey);
                        SyncLog.Trace(LogTopic.Nets, "net native operation rejected/resync op=" +
                            operationHeader.OperationId + " " + unresolvedDetail);
                        return;
                    }
                    if (allowMergedNodeSplit)
                        SyncLog.Trace(LogTopic.Nets, "net native node target recovered op=" +
                            operationHeader.OperationId);
                    ClearOperationHold(operationRetryKey, UnresolvedNativeTargetReason,
                        NativeOperationSubject(operationHeader.OperationId, operationRetryKey.Origin),
                        "every endpoint resolved on a later attempt");

                    // Two targets collapsed onto one local edge: unsafe to commit and unrepairable here.
                    if (aliasedSplitTarget)
                    {
                        _operationBuildFailures.Remove(operationRetryKey);
                        SyncLog.Trace(LogTopic.Nets, "net native op aliased split target op=" +
                            operationHeader.OperationId + " courses=" + work.Count);
                        SyncInbox.RequestResync(Diagnostics.ResyncReport
                            .Create("net split target aliased by local divergence", "net",
                                Diagnostics.ResyncEvidence.Contradiction)
                            .About("op " + operationHeader.OperationId + " from player " +
                                   work[0].OriginPlayerId)
                            .Tried("nothing - committing the batch anyway would hand the game two " +
                                   "junctions cut from one road, which it dereferences without a check")
                            .Fact("what disagrees",
                                "two roads the other player split separately are one road here, so " +
                                "an earlier split never arrived")
                            .Fact("the other player built", operationHeader.PrefabName)
                            .Fact("courses in the operation", work.Count));
                        return;
                    }

                    // Partly present on first sight: the worlds disagree. Still commit atomically, but record it.
                    if (alreadyBuiltCourses > 0 && !_armedNetOperations.Contains(operationRetryKey, now))
                        SyncLog.Trace(LogTopic.Nets, "net native op partial on first sight op=" +
                            operationHeader.OperationId + " present=" + alreadyBuiltCourses + "/" +
                            work.Count);
                    _armedNetOperations.Remember(operationRetryKey, now, ArmedOperationWindowMs);
                }

                for (int i = 0; i < work.Count; i++)
                {
                    SimulationCommandMessage message = work[i];
                    if (message.OriginPlayerId == session.LocalPlayerId)
                    {
                        continue;
                    }

                    NetPlacementCommand command;
                    Entity prefab;
                    Bezier4x3 bezier;
                    float measuredLength;
                    bool nativePoint;
                    if (nativeOperation)
                    {
                        PreparedNativeCourse prepared = preparedNative[i];
                        if (prepared.AlreadyBuilt) continue;
                        command = prepared.Command;
                        prefab = prepared.Prefab;
                        bezier = prepared.Curve;
                        measuredLength = prepared.MeasuredLength;
                        nativePoint = prepared.Point;
                    }
                    else
                    {
                        if (!CommandDecode.TryDecode(message, NetPlacementCommand.Decode, LogTopic.Nets,
                                "NetSync", out command))
                            continue;

                        if (!_prefabIndex.TryResolve(command.PrefabName, out prefab) ||
                            !EntityManager.HasComponent<global::Game.Prefabs.NetData>(prefab) ||
                            !EntityManager.HasComponent<global::Game.Prefabs.NetGeometryData>(prefab))
                        {
                            SyncLog.Warn(LogTopic.Nets, "NetSync realize: unavailable net prefab '" +
                                command.PrefabName + "' from player " + message.OriginPlayerId +
                                "; skipping.");
                            continue;
                        }

                        bezier = new Bezier4x3
                        {
                            a = new float3(command.Ax, command.Ay, command.Az),
                            b = new float3(command.Bx, command.By, command.Bz),
                            c = new float3(command.Cx, command.Cy, command.Cz),
                            d = new float3(command.Dx, command.Dy, command.Dz),
                        };
                        measuredLength = MathUtils.Length(bezier);
                        nativePoint = false;
                        if (!math.isfinite(measuredLength) ||
                            measuredLength < NetPlacementCommand.MinCourseLength)
                        {
                            SyncLog.Warn(LogTopic.Nets,
                                "NetSync realize: degenerate fallback course for '" +
                                command.PrefabName + "'; skipping.");
                            continue;
                        }
                        // Geometry-only fallback has no exact native length, so derive it locally.
                        command.Length = measuredLength;
                    }

                    float3 a = bezier.a;
                    float3 d = bezier.d;

                    if (!haveSnapshot)
                    {
                        TakeNetSnapshot(out nodes, out edges, out ownedNodes, out ownedEdges);
                        TakeSurfaceSnapshot(ref heightData, ref waterData);
                        haveSnapshot = true;
                    }

                    // Skip a span already present as same-prefab geometry, e.g. a node-reduction echo. Tolerances are
                    // ~1 m and height-matched, so a parallel or re-elevated span is never skipped.
                    if (!nativeOperation && SpanAlreadyBuilt(prefab, bezier, ref edges))
                    {
                        if (command.HasNativeCourse)
                            _nativeTargetDeadlines.Remove(NativeRetryKey(message, command));
                        continue;
                    }

                    NetPrefabInfo placedInfo = NetInfoOf(prefab);
                    float2 sourceStartElevation = new float2(command.Start.ElevationLeft,
                        command.Start.ElevationRight);
                    float2 sourceEndElevation = new float2(command.End.ElevationLeft,
                        command.End.ElevationRight);
                    int startKind, endKind;
                    float startT, endT;
                    Entity startSnap, endSnap;
                    CoursePos? startShared = null, endShared = null;
                    bool nativeTargetsResolved = true;
                    bool startUsedLocalSurface = false, endUsedLocalSurface = false;

                    if (command.HasNativeCourse)
                    {
                        if (command.Start.Kind == NetEndpointTargetKind.Infer)
                            startSnap = ClassifyEndpointWithLocalSurface(prefab,
                                new float3(command.Start.PosX, command.Start.PosY, command.Start.PosZ),
                                sourceStartElevation, command.Start.Flags, placedInfo, ref nodes, ref edges,
                                ref ownedNodes, batchNewNodes, batchEdges,
                                ref heightData, ref waterData,
                                out startT, out startKind, out startShared);
                        else
                            nativeTargetsResolved &= TryResolveNativeEndpointWithLocalSurface(prefab,
                                command.Start, placedInfo,
                                ref nodes, ref edges, ref ownedNodes, ref ownedEdges,
                                ref heightData, ref waterData, allowMergedNodeSplit,
                                out startSnap, out startT, out startKind,
                                out startUsedLocalSurface);

                        if (command.End.Kind == NetEndpointTargetKind.Infer)
                            endSnap = ClassifyEndpointWithLocalSurface(prefab,
                                new float3(command.End.PosX, command.End.PosY, command.End.PosZ),
                                sourceEndElevation, command.End.Flags, placedInfo, ref nodes, ref edges,
                                ref ownedNodes, batchNewNodes, batchEdges,
                                ref heightData, ref waterData,
                                out endT, out endKind, out endShared);
                        else
                            nativeTargetsResolved &= TryResolveNativeEndpointWithLocalSurface(prefab,
                                command.End, placedInfo,
                                ref nodes, ref edges, ref ownedNodes, ref ownedEdges,
                                ref heightData, ref waterData, allowMergedNodeSplit,
                                out endSnap, out endT, out endKind,
                                out endUsedLocalSurface);

                        if (startUsedLocalSurface) _rzLocalSurfaceMatches++;
                        if (endUsedLocalSurface) _rzLocalSurfaceMatches++;

                        // Endpoints left for local inference skipped the preflight; claim their split targets here.
                        if (nativeOperation &&
                            (!TryClaimSplitTarget(command.Start, startSnap, startKind) ||
                             !TryClaimSplitTarget(command.End, endSnap, endKind)))
                        {
                            abortWholeOperation = true;
                            abortAliasedSplit = true;
                            abortReason = "two courses resolved onto the same existing edge";
                            break;
                        }

                        NativeTargetRetryKey retryKey = NativeRetryKey(message, command);
                        if (!nativeTargetsResolved)
                        {
                            // A target vanished since the preflight: retry the whole operation rather than leave a prefix.
                            _nativeTargetDeadlines.Remove(retryKey);
                            abortWholeOperation = true;
                            abortReason = "a native target changed after operation preflight";
                            break;
                        }
                        else
                        {
                            _nativeTargetDeadlines.Remove(retryKey);
                        }
                    }
                    else
                    {
                        if (command.HasNativeCourse)
                            _nativeTargetDeadlines.Remove(NativeRetryKey(message, command));
                        startSnap = ClassifyEndpointWithLocalSurface(prefab, a,
                            sourceStartElevation, command.Start.Flags, placedInfo, ref nodes, ref edges,
                            ref ownedNodes, batchNewNodes, batchEdges,
                            ref heightData, ref waterData,
                            out startT, out startKind, out startShared);
                        endSnap = ClassifyEndpointWithLocalSurface(prefab, d,
                            sourceEndElevation, command.End.Flags, placedInfo, ref nodes, ref edges,
                            ref ownedNodes, batchNewNodes, batchEdges,
                            ref heightData, ref waterData,
                            out endT, out endKind, out endShared);
                    }

                    // Fixed-height ends keep the captured elevation; free-height ends follow the local surface.
                    float2 startElevation = EndElevation(prefab, startSnap, startKind, a,
                        sourceStartElevation, command.Start.Flags,
                        ref heightData, ref waterData, out float startCorrection);
                    float2 endElevation = EndElevation(prefab, endSnap, endKind, d,
                        sourceEndElevation, command.End.Flags,
                        ref heightData, ref waterData, out float endCorrection);
                    TallySurfaceCorrection(startCorrection, endCorrection);

                    // A native operation stays together; geometry-only fallback commands stay serialized.
                    bool defer = !nativeOperation &&
                                 (startKind == KindDeferBatchEdge || endKind == KindDeferBatchEdge);
                    bool splittingCourse = startKind == KindSplit || endKind == KindSplit;
                    // A body crossing an existing edge splits it like an endpoint tap does.
                    if (!nativeOperation && !defer && !splittingCourse)
                        splittingCourse = BodyTouchesExistingEdge(bezier, placedInfo, ref edges);
                    // One existing-edge-splitting course per batch: two in one ApplyTool pass can make
                    // ApplyNetSystem dereference a stale edge and crash natively.
                    if (!defer && splittingCourse && splitUsed && !nativeOperation) defer = true;

                    if (defer)
                    {
                        // Requeue the rest, in order, after this frame's edges become queryable.
                        RequeueFrom(work, i);
                        break;
                    }

                    try
                    {
                        // Always Temp/apply, so late contacts and splits are handled natively.
                        if (built == 0) PrepareDefinitionFrame();
                        Entity definition;
                        if (command.HasNativeCourse)
                            definition = CreateNativeCourse(prefab, command, bezier,
                                startSnap, startT, startKind, startElevation,
                                endSnap, endT, endKind, endElevation, startShared, endShared);
                        else
                            definition = CreateCourse(prefab, bezier, command.Length,
                                startSnap, startT, startKind, endSnap, endT, endKind,
                                startElevation, endElevation, command.PinProfile, startShared, endShared);
                        createdDefinitions.Add(definition);
                        built++;
                        (retained ?? (retained = new List<SimulationCommandMessage>())).Add(message);
                        if (splittingCourse) splitUsed = true;
                        RegisterBatchNodes(definition, placedInfo, startKind, endKind, batchNewNodes);
                        if (!nativePoint) batchEdges.Add(bezier);
                        realizedCourses.Add(new RealizedCourse
                        {
                            Prefab = prefab,
                            PrefabName = command.PrefabName,
                            Curve = bezier,
                            Length = command.Length,
                            Charge = !command.HasNativeCourse ||
                                     ((((global::Game.Tools.CoursePosFlags)command.Start.Flags |
                                        (global::Game.Tools.CoursePosFlags)command.End.Flags) &
                                       global::Game.Tools.CoursePosFlags.DontCreate) == 0),
                            StartSnap = startSnap,
                            EndSnap = endSnap,
                            StartT = startT,
                            EndT = endT,
                            StartKind = startKind,
                            EndKind = endKind,
                        });
                    }
                    catch (System.Exception ex)
                    {
                        if (nativeOperation)
                        {
                            abortWholeOperation = true;
                            abortReason = "course " + command.CourseIndex + " definition failed (" +
                                          ex.GetType().Name + ")";
                            break;
                        }
                        SyncLog.Error(LogTopic.Nets, "NetSync realize FAILED for '" +
                            command.PrefabName + "': " + ex);
                    }
                }

                if (abortWholeOperation)
                {
                    for (int i = 0; i < createdDefinitions.Count; i++)
                    {
                        Entity definition = createdDefinitions[i];
                        if (EntityManager.Exists(definition)) EntityManager.DestroyEntity(definition);
                    }
                    built = 0;
                    retained = null;
                    NetPlacementCommand header = preparedNative[0].Command;
                    var failureKey = new NetOperationKey
                    {
                        Origin = work[0].OriginPlayerId,
                        Operation = header.OperationId,
                    };
                    _operationBuildFailures.TryGetValue(failureKey, out int failures);
                    failures++;
                    // Aliasing is deterministic; retrying cannot help.
                    bool retry = failures <= 3 && !abortAliasedSplit;
                    if (abortAliasedSplit) SyncInbox.RequestResync(CS2MultiplayerMod.Game.Diagnostics.ResyncReport
                        .Create("net split target aliased by local divergence", "net",
                            CS2MultiplayerMod.Game.Diagnostics.ResyncEvidence.Contradiction)
                        .About("aliased split target")
                        .Tried("nothing - two roads the other player split separately are one road here"));
                    if (retry)
                    {
                        _operationBuildFailures[failureKey] = failures;
                        RequeueAtFront(work);
                    }
                    else
                    {
                        _operationBuildFailures.Remove(failureKey);
                    }
                    ReleaseTrackedTemps(_isolatedLocalTemps);
                    ForceActiveToolUpdate();
                    string outcome;
                    if (retry) outcome = "; retrying the whole operation (" + failures + "/3).";
                    else if (abortAliasedSplit) outcome = "; dropped and requested world recovery.";
                    else outcome = "; dropped after 3 retries.";
                    SyncLog.Warn(LogTopic.Nets, "NetSync: native operation " + header.OperationId +
                        " rolled back before generation - " + abortReason + outcome);
                    return;
                }

                if (nativeOperation)
                {
                    NetPlacementCommand header = preparedNative[0].Command;
                    _operationBuildFailures.Remove(new NetOperationKey
                    {
                        Origin = work[0].OriginPlayerId,
                        Operation = header.OperationId,
                    });
                }

                // Charge only once every definition exists, so a failed course cannot leave a partial charge.
                try
                {
                    for (int i = 0; i < realizedCourses.Count; i++)
                    {
                        RealizedCourse realized = realizedCourses[i];
                        if (!realized.Charge) continue;
                        constructionCost += ConstructionCharger.CalculateNetCost(
                            EntityManager, realized.Prefab, realized.Length);
                        chargedCourses++;
                    }
                }
                catch (System.Exception ex)
                {
                    constructionCost = 0;
                    chargedCourses = 0;
                    SyncLog.Warn(LogTopic.Nets, "NetSync: could not calculate remote net charge: " +
                        ex.Message);
                }
                // Echo guards only after every definition exists.
                for (int i = 0; i < realizedCourses.Count; i++)
                {
                    RealizedCourse realized = realizedCourses[i];
                    MarkRealizeGuards(realized.PrefabName, realized.Curve.a, realized.Curve.d,
                        realized.StartSnap, realized.StartKind, realized.StartT,
                        realized.EndSnap, realized.EndKind, realized.EndT, now);
                    RecordRealizedSpan(realized.Curve);
                    _rzSegments++;
                    TallyEnd(realized.StartKind);
                    TallyEnd(realized.EndKind);
                }
            }
            finally
            {
                if (haveSnapshot)
                {
                    nodes.Dispose();
                    edges.Dispose();
                    ownedNodes.Dispose();
                    ownedEdges.Dispose();
                }
                batchEdges.Dispose();
            }

            if (built == 0 && _isolatedLocalTemps.Count > 0)
            {
                ReleaseTrackedTemps(_isolatedLocalTemps);
                ForceActiveToolUpdate();
            }

            // Definitions become Temp edges this Modification; the next quiet frame applies them.
            if (built > 0)
            {
                _pendingApply = true;
                _pendingTransactionKind = RemoteToolTransactionKind.Net;
                _armTick = System.Environment.TickCount;
                _pendingNetConstructionCharge = constructionCost;
                _pendingNetConstructionChargeCourses = chargedCourses;
                // Replay the complete source operation if this commit is lost; fragments cannot satisfy CourseCount.
                List<SimulationCommandMessage> batchSources = nativeOperation
                    ? new List<SimulationCommandMessage>(work)
                    : retained;
                _onCommitLost = delegate
                {
                    RequeueAtFront(batchSources);
                };
                if (hasCompletedKey)
                {
                    NetOperationKey completionKey = completedKey;
                    _onCommitComplete = delegate
                    {
                        long completedNow = Mod.Service != null ? Mod.Service.NowMs : now;
                        _completedNetOperations.Remember(completionKey, completedNow, 60000);
                        SyncLog.Trace(LogTopic.Nets, "net operation committed/drained op=" +
                            completionKey.Operation);
                    };
                }
                SyncLog.Trace(LogTopic.Nets, "net build batch armed n=" + built +
                    (splitUsed ? " +split" : ""));
            }
        }

        /// <summary>
        /// A native operation refused before any course was built never comes again, so the hole needs a
        /// resync; the offending course travels with the request.
        /// </summary>
        private static void ReportRefusedNativeOperation(NetPlacementCommand command,
            int courseIndex, int courseCount, string what, string measurement)
        {
            SyncInbox.RequestResync(Diagnostics.ResyncReport
                .Create("native net operation refused", "net",
                    Diagnostics.ResyncEvidence.StreamLoss)
                .About("op " + command.OperationId + " course " + courseIndex + "/" + courseCount)
                .Tried("nothing - the operation was refused before it could be built")
                .Fact("what was refused", what)
                .Fact("net prefab", command.PrefabName)
                .Fact("measurement", measurement)
                .Fact("straight profile pinned", command.PinProfile));
        }
    }
}
