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
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;

using CS2MultiplayerMod.Game.Sync.Infrastructure;
using CS2MultiplayerMod.Game.Sync.Commands;
namespace CS2MultiplayerMod.Game.Sync.Systems.Net
{
    // Realize (client) side of NetSyncSystem: drain queued NetPlacementCommands into one working set,
    // resolve captured native targets (or classify fallback geometry), then route every course
    // through one serialized Temp+ApplyTool transaction. Dependent systems wait for its drain so
    // they never observe half-realized network geometry.
    //
    // This file holds the operation state and RealizeIncoming, the cycle itself. Assembling an
    // operation out of its messages is in RealizeOperation.cs, holding one whose targets have not
    // arrived is in RealizeHold.cs, and the span and endpoint geometry it leans on is in
    // RealizeSpan.cs.
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
        /// What an operation whose target has not appeared is still waiting for.
        ///
        /// <see cref="NativeOperationHold.Relaxed"/> is separate from the deadline on purpose. The
        /// resolver's last-resort matches unlock when the first window expires, and a second window
        /// granted by the resync arbiter must not take them away again - re-deriving "relaxed" from
        /// the deadline alone would do exactly that, and the operation would spend its extra window
        /// with strictly less resolving power than the one before it.
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

        // Operations whose Temp batch this machine has already armed at least once. Reconciling a
        // partially present operation is how a lost commit recovers; the SAME state on an operation
        // seen for the first time means the two worlds disagree about what is already built.
        private readonly CS2MultiplayerMod.Core.Sync.OperationReplayWindow<NetOperationKey>
            _armedNetOperations =
                new CS2MultiplayerMod.Core.Sync.OperationReplayWindow<NetOperationKey>();
        private const long ArmedOperationWindowMs = 60000;

        // Existing edges this batch's courses will split, keyed by the local edge and remembering
        // which source edge claimed it. See TryClaimSplitTarget.
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

            public bool Intersect(QuadTreeBoundsXZ bounds)
            {
                return !Covered && MathUtils.Intersect(bounds.m_Bounds, Bounds);
            }

            public void Iterate(QuadTreeBoundsXZ bounds, Entity entity)
            {
                if (Covered || !MathUtils.Intersect(bounds.m_Bounds, Bounds) ||
                    !Curves.HasComponent(entity) || !Prefabs.HasComponent(entity) ||
                    Owners.HasComponent(entity) || Temps.HasComponent(entity) ||
                    Deleted.HasComponent(entity) || Prefabs[entity].m_Prefab != Prefab) return;

                Bezier4x3 curve = Curves[entity].m_Bezier;
                float t;
                if (MathUtils.Distance(curve.xz, Point.xz, out t) > SplitMatch.TolXZ) return;
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

            // One Temp batch in flight at a time (a course built before the previous batch's
            // nodes/edges are query-able could not connect to them), and never on the frame the
            // player's own gesture applies. A selected tool is allowed while its preview is being
            // regenerated or cleared; only the single frame that commits a local Apply has priority.
            if (!CanBuildDefinitions) return;

            // One source Apply may emit several native courses. Keep that operation intact: a
            // junction or point-mode network object is not equivalent to a sequence of independent
            // clicks, and applying only a prefix lets intermediate node reduction deform the rest.
            List<SimulationCommandMessage> work;
            bool nativeOperation;
            NetToolOperationCommand mixedOperation;
            if (!TryTakeCompleteOperation(session, now, out work, out nativeOperation,
                    out mixedOperation)) return;
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
                    Diagnostics.FlightRecorder.Note("net operation duplicate suppressed op=" +
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
            // Enabled only once the retry window has passed (see the rejection path below): a node
            // that is merely mid-commit comes back on its own, and splitting an edge under it would
            // plant a junction the source never made.
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

            // Source messages of the courses the Temp batch builds, retained until the commit
            // actually runs: if the armed batch is wiped before committing (see _onCommitLost) they
            // are re-enqueued and the batch rebuilds instead of being lost.
            List<SimulationCommandMessage> retained = null;

            // New nodes / edges the Temp batch will create, so a later course can recognise (a) an
            // endpoint that coincides with one of our pending new nodes — it will MERGE, so it is not
            // a split — and (b) an endpoint that taps the middle of a pending batch edge, which must
            // wait until that edge is real (deferred to the next, post-commit cycle).
            var batchNewNodes = new NativeList<float3>(maxBatch, Allocator.Temp);
            var batchEdges = new NativeList<Bezier4x3>(maxBatch, Allocator.Temp);
            try
            {
                if (nativeOperation)
                {
                    // Resolve every external target before creating the first definition. If course
                    // N depends on geometry that has not arrived yet, committing courses 0..N-1 and
                    // retrying only the suffix would destroy the source operation's junction shape.
                    TakeNetSnapshot(out nodes, out edges, out ownedNodes, out ownedEdges);
                    TakeSurfaceSnapshot(ref heightData, ref waterData);
                    haveSnapshot = true;

                    // The game resolves nearby network geometry through its quadtree. Use the same
                    // read-only snapshot for per-course idempotence instead of scanning every edge in
                    // the city once for every grid cell.
                    JobHandle searchDependencies;
                    liveEdgeSearch = new LiveEdgeSearchSnapshot
                    {
                        Tree = _netSearchSystem.GetNetSearchTree(readOnly: true,
                            out searchDependencies),
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
                            Mod.log.Warn("[MP] NetSync: native operation became malformed during preflight: " +
                                         ex.Message + "; dropping whole operation.");
                            return;
                        }

                        Entity prefab;
                        if (!_prefabIndex.TryResolve(command.PrefabName, out prefab) ||
                            !EntityManager.HasComponent<global::Game.Prefabs.NetData>(prefab) ||
                            !EntityManager.HasComponent<global::Game.Prefabs.NetGeometryData>(prefab))
                        {
                            Mod.log.Warn("[MP] NetSync: native operation references unavailable net prefab '" +
                                         command.PrefabName + "'; dropping whole operation.");
                            return;
                        }
                        if (!string.IsNullOrEmpty(command.SubPrefabName))
                        {
                            Entity subPrefab;
                            if (!_prefabIndex.TryResolve(command.SubPrefabName, out subPrefab) ||
                                !EntityManager.HasComponent<global::Game.Prefabs.NetLaneData>(subPrefab))
                            {
                                Mod.log.Warn("[MP] NetSync: native operation references unavailable lane prefab '" +
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
                            Mod.log.Warn("[MP] NetSync: native operation " + command.OperationId +
                                         " contains a degenerate course; dropping the whole operation.");
                            return;
                        }

                        // Preserve the source NetCourse length exactly, but reject a forged or
                        // corrupt scalar that materially disagrees with the transmitted curve.
                        float lengthTolerance = math.max(0.05f, measuredLength * 0.01f);
                        if (math.abs(command.Length - measuredLength) > lengthTolerance)
                        {
                            Mod.log.Warn("[MP] NetSync: native operation " + command.OperationId +
                                         " has an inconsistent course length; dropping the whole operation.");
                            return;
                        }

                        const CreationFlags allowedNativeFlags = CreationFlags.Invert |
                            CreationFlags.Align | CreationFlags.Hidden | CreationFlags.Optional |
                            CreationFlags.Lowered | CreationFlags.Native |
                            CreationFlags.Construction | CreationFlags.SubElevation;
                        if ((((CreationFlags)command.CreationFlags) & ~allowedNativeFlags) != 0)
                        {
                            Mod.log.Warn("[MP] NetSync: native operation " + command.OperationId +
                                         " contains an unsafe creation mode; dropping the whole operation.");
                            SyncInbox.RequestResync(Diagnostics.ResyncReport
                                .Create("unsafe native net creation flags", "net",
                                    Diagnostics.ResyncEvidence.StreamLoss)
                                .About("op " + command.OperationId)
                                .Tried("nothing - the operation was refused before it could be built")
                                .Fact("creation flags on the wire", command.CreationFlags));
                            return;
                        }
                        // NetCourse elevations are exact native generator state, not values limited
                        // by PlaceableNetData's UI range. Snaps and underground transitions can
                        // legitimately exceed that range. The wire decoder already rejects every
                        // non-finite or globally implausible value, so preserve these values intact.

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
                        // Keyed on the RESOLVED kind, not the wire kind: a node target that matched a
                        // merged edge still has to replay that split, or the course is skipped as
                        // already-built and the junction never appears.
                        bool topologyNeedsReplay =
                            (startExternal && startResolved && startKind == KindSplit) ||
                            (endExternal && endResolved && endKind == KindSplit);
                        bool geometryAlreadyBuilt = !nativePoint &&
                                                    SpanAlreadyBuilt(prefab, curve, ref liveEdgeSearch);

                        // Geometry coverage alone is not enough for a native operation. An endpoint
                        // aimed at an edge also creates a split node. Skipping that course while the
                        // target is still an unsplit edge leaves the next operation's Node target
                        // unresolved even though the road pixels look identical on both machines.
                        // External node targets must resolve as well before this is a safe no-op.
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
                            Diagnostics.FlightRecorder.Note("net native topology replay op=" +
                                command.OperationId + " course=" + command.CourseIndex);

                        // A course whose geometry and endpoint topology are already present is this
                        // operation's idempotent portion. Remaining missing courses still reconcile
                        // atomically below.
                        if (alreadyBuilt) continue;
                    }

                    if (alreadyBuiltCourses == work.Count)
                    {
                        ClearOperationHold(operationRetryKey, UnresolvedNativeTargetReason,
                            NativeOperationSubject(operationHeader.OperationId,
                                operationRetryKey.Origin),
                            "the operation turned out to be already present");
                        _operationBuildFailures.Remove(operationRetryKey);
                        Diagnostics.FlightRecorder.Note("net native op already present=" +
                                                          operationHeader.OperationId +
                                                          " courses=" + work.Count);
                        _completedNetOperations.Remember(operationRetryKey, now, 60000);
                        return;
                    }
                    if (alreadyBuiltCourses > 0)
                        Diagnostics.FlightRecorder.Note("net native op reconcile existing=" +
                                                          alreadyBuiltCourses + "/" + work.Count);

                    if (unresolvedOperationTarget)
                    {
                        int windows;
                        if (HoldUnresolvedOperation(operationRetryKey, now,
                                operationHeader.OperationId, unresolvedDetail, out windows))
                        {
                            // Wait BEHIND work that can still make progress, not in front of it.
                            // Parking the whole queue here for the length of the window stopped
                            // every later operation from every player - including, in the logs this
                            // came from, the ones that would have built the target being waited for.
                            RequeueStalledOperation(work);
                            return;
                        }

                        // The window is up. Before spending a world reload on it, say exactly what
                        // is missing and what stands there instead, and let the arbiter decide: the
                        // same "missing" road has been observed to be one this machine's own delete
                        // feeder removed while the placement waited.
                        // Non-null whenever unresolvedOperationTarget is set - they are assigned
                        // together - but the endpoint description is worth having either way.
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
                            // Not settled: keep the edit. The arbiter has frozen the feeders that
                            // could remove the target, so this window is the first one that gets to
                            // look at a world that is standing still.
                            ExtendUnresolvedOperation(operationRetryKey, now);
                            RequeueStalledOperation(work);
                            return;
                        }

                        _nativeOperationHolds.Remove(operationRetryKey);
                        Diagnostics.FlightRecorder.Note("net native operation rejected/resync op=" +
                                                          operationHeader.OperationId + " " +
                                                          unresolvedDetail);
                        return;
                    }
                    if (allowMergedNodeSplit)
                        Diagnostics.FlightRecorder.Note("net native node target recovered op=" +
                                                          operationHeader.OperationId);
                    ClearOperationHold(operationRetryKey, UnresolvedNativeTargetReason,
                        NativeOperationSubject(operationHeader.OperationId, operationRetryKey.Origin),
                        "every endpoint resolved on a later attempt");

                    // Every target resolved, but two of them collapsed onto one local edge. There is
                    // no safe way to commit that batch and no way to repair it from here: the missing
                    // split belongs to work this machine never applied.
                    if (aliasedSplitTarget)
                    {
                        _operationBuildFailures.Remove(operationRetryKey);
                        Diagnostics.FlightRecorder.Note("net native op aliased split target op=" +
                                                          operationHeader.OperationId +
                                                          " courses=" + work.Count);
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

                    // A first-sight operation that is already partly present means the two worlds
                    // disagree about what is built. Reconciling still commits atomically, so let it
                    // through, but record it: the source applied a different course set than this
                    // batch will, and CourseSplitSystem resolves intersections from what it is given.
                    if (alreadyBuiltCourses > 0 && !_armedNetOperations.Contains(operationRetryKey, now))
                        Diagnostics.FlightRecorder.Note("net native op partial on first sight op=" +
                                                          operationHeader.OperationId + " present=" +
                                                          alreadyBuiltCourses + "/" + work.Count);
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
                        try { command = NetPlacementCommand.Decode(message.Body); }
                        catch (System.Exception ex)
                        {
                            Mod.log.Warn("[MP] NetSync: dropping malformed command: " + ex.Message);
                            continue;
                        }

                        if (!_prefabIndex.TryResolve(command.PrefabName, out prefab) ||
                            !EntityManager.HasComponent<global::Game.Prefabs.NetData>(prefab) ||
                            !EntityManager.HasComponent<global::Game.Prefabs.NetGeometryData>(prefab))
                        {
                            Mod.log.Warn("[MP] NetSync realize: unavailable net prefab '" +
                                         command.PrefabName + "' from player " +
                                         message.OriginPlayerId + "; skipping.");
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
                            Mod.log.Warn("[MP] NetSync realize: degenerate fallback course for '" +
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

                    // Idempotence: skip a span this machine already has as live same-prefab geometry.
                    // The game's node reduction can merge a committed span into a neighbour and
                    // re-surface it as a wider create on the other machine; without this check that
                    // echo would stack a duplicate road on top of the existing one (and ping-pong).
                    // The tolerances are SplitMatch-tight (~1 m), far below a parallel lane, and a
                    // span rebuilt at another elevation fails the height match — never wrongly skipped.
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
                    bool nativeTargetsResolved = true;
                    bool startUsedLocalSurface = false, endUsedLocalSurface = false;

                    if (command.HasNativeCourse)
                    {
                        if (command.Start.Kind == NetEndpointTargetKind.Infer)
                            startSnap = ClassifyEndpointWithLocalSurface(prefab, a,
                                sourceStartElevation, placedInfo, ref nodes, ref edges,
                                ref ownedNodes, batchNewNodes, batchEdges,
                                ref heightData, ref waterData,
                                out startT, out startKind);
                        else
                            nativeTargetsResolved &= TryResolveNativeEndpointWithLocalSurface(prefab,
                                command.Start, placedInfo,
                                ref nodes, ref edges, ref ownedNodes, ref ownedEdges,
                                ref heightData, ref waterData, allowMergedNodeSplit,
                                out startSnap, out startT, out startKind,
                                out startUsedLocalSurface);

                        if (command.End.Kind == NetEndpointTargetKind.Infer)
                            endSnap = ClassifyEndpointWithLocalSurface(prefab, d,
                                sourceEndElevation, placedInfo, ref nodes, ref edges,
                                ref ownedNodes, batchNewNodes, batchEdges,
                                ref heightData, ref waterData,
                                out endT, out endKind);
                        else
                            nativeTargetsResolved &= TryResolveNativeEndpointWithLocalSurface(prefab,
                                command.End, placedInfo,
                                ref nodes, ref edges, ref ownedNodes, ref ownedEdges,
                                ref heightData, ref waterData, allowMergedNodeSplit,
                                out endSnap, out endT, out endKind,
                                out endUsedLocalSurface);

                        if (startUsedLocalSurface) _rzLocalSurfaceMatches++;
                        if (endUsedLocalSurface) _rzLocalSurfaceMatches++;

                        // Endpoints the source left for local inference never went through the
                        // operation preflight, so claim every native split target here too.
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
                            // The operation-level preflight resolved every external target against
                            // this same snapshot. If one vanished now, do not leave an already-built
                            // prefix behind; retry the complete source operation on a fresh frame.
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
                            sourceStartElevation, placedInfo, ref nodes, ref edges,
                            ref ownedNodes, batchNewNodes, batchEdges,
                            ref heightData, ref waterData,
                            out startT, out startKind);
                        endSnap = ClassifyEndpointWithLocalSurface(prefab, d,
                            sourceEndElevation, placedInfo, ref nodes, ref edges,
                            ref ownedNodes, batchNewNodes, batchEdges,
                            ref heightData, ref waterData,
                            out endT, out endKind);
                    }

                    // Fixed-height ends retain the captured elevation/profile choice. Free-height
                    // ends are adjusted against this machine's surface (see EndElevation).
                    float startCorrection, endCorrection;
                    float2 startElevation = EndElevation(prefab, startSnap, startKind, a,
                        sourceStartElevation, command.Start.Flags,
                        ref heightData, ref waterData, out startCorrection);
                    float2 endElevation = EndElevation(prefab, endSnap, endKind, d,
                        sourceEndElevation, command.End.Flags,
                        ref heightData, ref waterData, out endCorrection);
                    TallySurfaceCorrection(startCorrection, endCorrection);

                    // A captured native operation is the exact set the source applied together, so
                    // its courses stay together even when one references geometry another course in
                    // that same operation creates. Geometry-only fallback commands remain serialized.
                    bool defer = !nativeOperation &&
                                 (startKind == KindDeferBatchEdge || endKind == KindDeferBatchEdge);
                    bool splittingCourse = startKind == KindSplit || endKind == KindSplit;
                    // A course whose BODY crosses or hugs an existing edge splits it at Temp generation
                    // exactly like an endpoint tap, but ClassifyEndpoint only sees the two endpoints —
                    // probe the span interior too, or two quick drags across the same road slip into one
                    // batch and hit the stale-edge crash below.
                    if (!nativeOperation && !defer && !splittingCourse)
                        splittingCourse = BodyTouchesExistingEdge(bezier, placedInfo, ref edges);
                    // At most ONE existing-edge-splitting course per batch: two courses committed in the
                    // same ApplyTool pass that both touch an existing edge can make ApplyNetSystem
                    // dereference a stale (already-split/deleted) edge and crash the process natively.
                    // Courses touching nothing pre-existing are unbounded (safe — the net tool grids
                    // many at once).
                    if (!defer && splittingCourse && splitUsed && !nativeOperation) defer = true;

                    if (defer)
                    {
                        // Re-queue this and every remaining item, in order, for the next cycle - after
                        // this frame's committed edges have become query-able.
                        RequeueFrom(work, i);
                        break;
                    }

                    try
                    {
                        // All replicated courses use the same Temp/apply transaction as the source.
                        // The former Permanent shortcut could not recover a missed contact or split
                        // and exposed half-realized geometry to dependent commands in this frame.
                        if (built == 0) PrepareDefinitionFrame();
                        Entity definition;
                        if (command.HasNativeCourse)
                            definition = CreateNativeCourse(prefab, command, bezier,
                                startSnap, startT, startKind, startElevation,
                                endSnap, endT, endKind, endElevation);
                        else
                            definition = CreateCourse(prefab, bezier, command.Length,
                                startSnap, startT, startKind, endSnap, endT, endKind,
                                startElevation, endElevation, command.PinProfile);
                        createdDefinitions.Add(definition);
                        built++;
                        (retained ?? (retained = new List<SimulationCommandMessage>())).Add(message);
                        if (splittingCourse) splitUsed = true;
                        if (startKind == KindFree) batchNewNodes.Add(a);
                        if (endKind == KindFree) batchNewNodes.Add(d);
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
                        Mod.log.Error("[MP] NetSync realize FAILED for '" + command.PrefabName +
                                      "': " + ex);
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
                    int failures;
                    _operationBuildFailures.TryGetValue(failureKey, out failures);
                    failures++;
                    // Aliasing is deterministic: retrying the same courses against the same local
                    // geometry resolves them onto the same edge again. Recover the world instead.
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
                    Mod.log.Warn("[MP] NetSync: native operation rolled back before generation - " +
                                 abortReason + outcome);
                    Diagnostics.FlightRecorder.Note(abortAliasedSplit
                        ? "net native op aliased split target op=" + header.OperationId
                        : "net native op rollback before generation retry=" + (retry ? failures : 0));
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

                // Accumulate the operation only after every selected definition exists. The actual
                // host treasury update is one write after this Temp transaction has drained, so a
                // failed/replayed later grid or parallel course cannot leave a partial charge.
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
                    Mod.log.Warn("[MP] NetSync: could not calculate remote net charge: " + ex.Message);
                }
                // Publish echo guards and diagnostics only after every definition selected for this
                // operation exists. A failed later course therefore cannot leave a phantom realized
                // span suppressing unrelated local capture.
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
                batchNewNodes.Dispose();
                batchEdges.Dispose();
            }

            if (built == 0 && _isolatedLocalTemps.Count > 0)
            {
                ReleaseTrackedTemps(_isolatedLocalTemps);
                ForceActiveToolUpdate();
            }

            // Arm the commit for the Temp batch: those definitions become Temp edges at this frame's
            // Modification, and the next quiet frame applies that isolated set through the net domain.
            if (built > 0)
            {
                _pendingApply = true;
                _pendingTransactionKind = RemoteToolTransactionKind.Net;
                _armTick = System.Environment.TickCount;
                _pendingNetConstructionCharge = constructionCost;
                _pendingNetConstructionChargeCourses = chargedCourses;
                // A partially reconciled native operation may have skipped courses that were
                // already present locally. If this commit is lost, replay the complete source
                // operation so it can be assembled atomically again; replaying only the missing
                // fragments could never satisfy CourseCount.
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
                        Diagnostics.FlightRecorder.Note("net operation committed/drained op=" +
                                                          completionKey.Operation);
                    };
                }
                Diagnostics.FlightRecorder.Note("net build batch armed n=" + built + (splitUsed ? " +split" : ""));
            }
        }
    }
}
