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
                    if (abortAliasedSplit) SyncInbox.RequestResync("net split target aliased by local divergence");
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

        private void PruneCompletedNetOperations(long now)
        {
            _completedNetOperations.Prune(now);
            _armedNetOperations.Prune(now);
        }

        /// <summary>
        /// Re-queue <paramref name="work"/>[<paramref name="from"/>..] ahead of the shared inbox.
        /// </summary>
        private void RequeueFrom(List<SimulationCommandMessage> work, int from)
        {
            if (from < work.Count)
                _remoteDeferred.InsertRange(0, work.GetRange(from, work.Count - from));
        }

        private static bool HasExternalNativeTarget(NetEndpointTargetKind kind) =>
            kind == NetEndpointTargetKind.Node || kind == NetEndpointTargetKind.Edge ||
            kind == NetEndpointTargetKind.OwnedNode || kind == NetEndpointTargetKind.OwnedEdge;

        private static string DescribeUnresolvedEndpoint(NetPlacementCommand command,
            bool startResolved)
        {
            NetEndpointIntent failed = startResolved ? command.End : command.Start;
            return "course=" + command.CourseIndex + " " + (startResolved ? "end" : "start") +
                   " kind=" + failed.Kind + " prefab='" + failed.TargetPrefabName + "' anchor=" +
                   failed.AnchorX.ToString("F1") + "," + failed.AnchorY.ToString("F1") + "," +
                   failed.AnchorZ.ToString("F1");
        }

        /// <summary>
        /// Record that a resolved endpoint will split <paramref name="target"/>, and report whether
        /// that claim is consistent with the source operation.
        ///
        /// Several courses of one operation may legitimately tap the SAME source edge: CourseSplitSystem
        /// receives them together and cuts that edge once into all of its pieces. Two courses that named
        /// DIFFERENT source edges but land on the same local edge are a different matter - this machine
        /// never received the split that separated them. Committing both would hand the apply pass two
        /// Temps sharing one original, which it dereferences without a liveness check.
        /// </summary>
        private bool TryClaimSplitTarget(NetEndpointIntent intent, Entity target, int kind)
        {
            if (kind != KindSplit || target == Entity.Null) return true;
            Bezier4x3 source = TargetCurveOf(intent);
            Bezier4x3 claimed;
            if (!_batchSplitClaims.TryGetValue(target, out claimed))
            {
                _batchSplitClaims[target] = source;
                return true;
            }
            return SameCurveBits(claimed, source) || SameCurveBitsReversed(claimed, source);
        }

        /// <summary>
        /// Pull one complete source operation from the ordered command streams. Messages belonging
        /// to later operations may be encountered while waiting for an interleaved course; they are
        /// returned to the simulation-thread prefix in their original order. An incomplete operation
        /// waits briefly and is then dropped as a whole, never realized as broken geometry.
        /// </summary>
        private bool TryTakeCompleteOperation(MultiplayerSession session, long now,
            out List<SimulationCommandMessage> operation, out bool nativeOperation,
            out NetToolOperationCommand mixedOperation)
        {
            operation = null;
            nativeOperation = false;
            mixedOperation = null;

            const int MaxScan = NetInboxCap;
            var scanned = new List<SimulationCommandMessage>();
            NetOperationKey key = default(NetOperationKey);
            int expected = 0;
            SimulationCommandMessage[] courses = null;
            NetPlacementCommand[] decodedCourses = null;
            int received = 0;

            for (int scan = 0; scan < MaxScan && (expected == 0 || received < expected); scan++)
            {
                SimulationCommandMessage message;
                if (!TryTakeNextPlacementMessage(out message)) break;
                if (message.OriginPlayerId == session.LocalPlayerId) continue;

                if (message.CommandId == NetToolOperationCommand.Id)
                {
                    if (expected == 0)
                    {
                        try { mixedOperation = NetToolOperationCommand.Decode(message.Body); }
                        catch (System.Exception ex)
                        {
                            Mod.log.Warn("[MP] NetSync: dropping malformed mixed net operation: " +
                                         ex.Message);
                            SyncInbox.RequestResync(Diagnostics.ResyncReport
                                .Create("malformed mixed net operation", "net",
                                    Diagnostics.ResyncEvidence.StreamLoss)
                                .About("mixed operation from player " + message.OriginPlayerId)
                                .Tried("nothing - the operation could not be decoded")
                                .Fact("decoder said", ex.Message));
                            return false;
                        }
                        operation = new List<SimulationCommandMessage>(1) { message };
                        return true;
                    }

                    // It arrived after the first fragment of an older placement operation. Keep it
                    // in the ordered prefix while scanning for that older operation's remaining
                    // fragments; it will be the next operation realized, never overtaken.
                    scanned.Add(message);
                    continue;
                }
                if (message.CommandId != NetPlacementCommand.Id)
                {
                    Mod.log.Warn("[MP] NetSync: dropping unsupported queued command " +
                                 message.CommandId + ".");
                    continue;
                }

                NetPlacementCommand command;
                try { command = NetPlacementCommand.Decode(message.Body); }
                catch (System.Exception ex)
                {
                    Mod.log.Warn("[MP] NetSync: dropping malformed command: " + ex.Message);
                    continue;
                }

                scanned.Add(message);
                if (expected == 0)
                {
                    key = new NetOperationKey
                    {
                        Origin = message.OriginPlayerId,
                        Operation = command.OperationId,
                    };
                    expected = command.CourseCount;
                    courses = new SimulationCommandMessage[expected];
                    decodedCourses = new NetPlacementCommand[expected];
                }

                if (message.OriginPlayerId != key.Origin || command.OperationId != key.Operation)
                    continue;
                if (command.CourseCount != expected)
                {
                    Mod.log.Warn("[MP] NetSync: dropping inconsistent course count for op=" +
                                 key.Operation + " from player " + key.Origin + ".");
                    continue;
                }

                int index = command.CourseIndex;
                if (courses[index] != null) continue;
                courses[index] = message;
                decodedCourses[index] = command;
                received++;
            }

            if (expected == 0) return false;

            if (received != expected)
            {
                long deadline;
                if (!_operationAssemblyDeadlines.TryGetValue(key, out deadline))
                {
                    deadline = now + OperationAssemblyWindowMs;
                    _operationAssemblyDeadlines[key] = deadline;
                }

                if (now < deadline)
                {
                    RequeueAtFront(scanned);
                    return false;
                }

                _operationAssemblyDeadlines.Remove(key);
                var later = new List<SimulationCommandMessage>();
                for (int i = 0; i < scanned.Count; i++)
                {
                    if (scanned[i].CommandId != NetPlacementCommand.Id)
                    {
                        later.Add(scanned[i]);
                        continue;
                    }
                    NetPlacementCommand command;
                    try { command = NetPlacementCommand.Decode(scanned[i].Body); }
                    catch { continue; }
                    if (scanned[i].OriginPlayerId != key.Origin || command.OperationId != key.Operation)
                        later.Add(scanned[i]);
                }
                RequeueAtFront(later);
                Diagnostics.FlightRecorder.Note("net incomplete op dropped=" + key.Operation +
                    " courses=" + received + "/" + expected);
                SyncInbox.RequestResync(Diagnostics.ResyncReport
                    .Create("incomplete net operation expired", "net",
                        Diagnostics.ResyncEvidence.StreamLoss)
                    .About("op " + key.Operation + " from player " + key.Origin)
                    .Tried("waited " + (OperationAssemblyWindowMs / 1000) +
                           " s for the missing pieces of the road the other player drew")
                    .Fact("pieces received", received + " of " + expected));
                return false;
            }

            _operationAssemblyDeadlines.Remove(key);
            operation = new List<SimulationCommandMessage>(expected);
            nativeOperation = true;
            bool hasNativeCourse = false;
            bool hasGeometryOnlyCourse = false;
            for (int i = 0; i < expected; i++)
            {
                operation.Add(courses[i]);
                nativeOperation &= decodedCourses[i].HasNativeCourse;
                hasNativeCourse |= decodedCourses[i].HasNativeCourse;
                hasGeometryOnlyCourse |= !decodedCourses[i].HasNativeCourse;
            }

            // Preserve later operations in their original receive order. Extra messages carrying
            // the completed key are duplicates or inconsistent fragments and are discarded.
            var deferred = new List<SimulationCommandMessage>();
            for (int i = 0; i < scanned.Count; i++)
            {
                if (scanned[i].CommandId != NetPlacementCommand.Id)
                {
                    deferred.Add(scanned[i]);
                    continue;
                }
                NetPlacementCommand command;
                try { command = NetPlacementCommand.Decode(scanned[i].Body); }
                catch { continue; }
                if (scanned[i].OriginPlayerId == key.Origin && command.OperationId == key.Operation)
                    continue;
                deferred.Add(scanned[i]);
            }
            RequeueAtFront(deferred);

            // Current senders only group exact native definitions. Geometry-only capture represents
            // one final edge per command. Rejecting mixed or grouped fallback input prevents a peer
            // from smuggling a partially native operation into per-course fallback realization.
            if ((hasNativeCourse && hasGeometryOnlyCourse) || (expected > 1 && !nativeOperation))
            {
                Diagnostics.FlightRecorder.Note("net incompatible multi-course op dropped=" +
                                                  key.Operation);
                SyncInbox.RequestResync(Diagnostics.ResyncReport
                    .Create("incompatible net operation rejected", "net",
                        Diagnostics.ResyncEvidence.StreamLoss)
                    .About("op " + key.Operation + " from player " + key.Origin)
                    .Tried("nothing - the operation mixed two course encodings that cannot be " +
                           "applied as one transaction")
                    .Fact("courses in the operation", expected));
                operation = null;
                nativeOperation = false;
                return false;
            }
            return true;
        }

        private bool TryTakeNextPlacementMessage(out SimulationCommandMessage message)
        {
            if (DeferForTerrain)
            {
                message = default(SimulationCommandMessage);
                return false;
            }
            if (_remoteDeferred.Count > 0)
            {
                message = _remoteDeferred[0];
                _remoteDeferred.RemoveAt(0);
                return true;
            }
            return _incoming.TryDequeue(out message);
        }

        private void RequeueAtFront(List<SimulationCommandMessage> messages)
        {
            if (messages != null && messages.Count > 0)
                _remoteDeferred.InsertRange(0, messages);
        }

        /// <summary>
        /// Re-queue an operation that is waiting for a target it cannot see yet, WITHOUT parking
        /// the rest of the pipeline behind it.
        ///
        /// The queue is strictly ordered, so the previous behaviour - putting it straight back at
        /// the front - stopped every later operation, from every player, for the whole retry
        /// window. That is worse than a delay: in the sessions this came from, the deferred
        /// placement spent ten seconds in front of a queue while the world it was searching went on
        /// changing, and then asked for a full world reload because what it was looking for was no
        /// longer there.
        ///
        /// Causal order was only ever meaningful per sender, so only ANOTHER sender's work may
        /// overtake. Everything the same sender queued behind this operation stays behind it.
        /// </summary>
        private void RequeueStalledOperation(List<SimulationCommandMessage> messages)
        {
            if (messages == null || messages.Count == 0) return;
            int origin = messages[0].OriginPlayerId;

            // Admit what has arrived so the reorder sees the whole ready set rather than whatever a
            // previous scan happened to leave behind. Realization stays gated where it always was
            // (see TryTakeNextPlacementMessage); only where the messages sit changes, and the same
            // inbox cap bounds it.
            SimulationCommandMessage admitted;
            while (_remoteDeferred.Count < NetInboxCap && _incoming.TryDequeue(out admitted))
                _remoteDeferred.Add(admitted);

            int insertAt = 0;
            while (insertAt < _remoteDeferred.Count &&
                   _remoteDeferred[insertAt].OriginPlayerId != origin) insertAt++;
            _remoteDeferred.InsertRange(insertAt, messages);
        }

        /// <summary>
        /// A hold whose window lapsed this long ago no longer counts. An operation can leave the
        /// pipeline by other doors - duplicate suppression, a malformed decode - and a forgotten
        /// hold must never be able to stop bulldozing for the rest of a session.
        /// </summary>
        private const long StaleHoldGraceMs = 2000;

        /// <summary>
        /// True while at least one native operation is inside a live window waiting for a target
        /// that has not arrived, pruning any hold whose window has fully lapsed.
        ///
        /// The feeders that can only ever REMOVE such a target - bulldoze, road replacement - stand
        /// down while this is true. They used to run ahead of it every frame, which is how a
        /// placement came to be rejected for a road this machine had just deleted out from under
        /// it. Two windows is the most any operation gets, so the hold is seconds, not minutes.
        /// </summary>
        public bool HasStalledNativeOperation(long now)
        {
            if (_nativeOperationHolds.Count == 0) return false;
            List<NetOperationKey> stale = null;
            bool live = false;
            foreach (KeyValuePair<NetOperationKey, NativeOperationHold> entry in _nativeOperationHolds)
            {
                if (now < entry.Value.DeadlineMs + StaleHoldGraceMs) { live = true; continue; }
                (stale ?? (stale = new List<NetOperationKey>())).Add(entry.Key);
            }
            if (stale != null)
                for (int i = 0; i < stale.Count; i++) _nativeOperationHolds.Remove(stale[i]);
            return live;
        }

        /// <summary>
        /// Whether the resolver may use its relaxed last-resort matches for this operation - the
        /// merged-node-as-edge-split fallback. Unlocked once a full retry window has passed, and
        /// never locked again for that operation (see <see cref="NativeOperationHold"/>).
        /// </summary>
        private bool RelaxedResolveAllowed(NetOperationKey key, long now)
        {
            NativeOperationHold hold;
            if (!_nativeOperationHolds.TryGetValue(key, out hold)) return false;
            return hold.Relaxed || now >= hold.DeadlineMs;
        }

        /// <summary>
        /// Arm or advance the hold on an operation whose target is missing. Returns true while it
        /// should keep waiting; false once the current window is up and a verdict is due.
        /// </summary>
        private bool HoldUnresolvedOperation(NetOperationKey key, long now, long operationId,
            string detail, out int windows)
        {
            NativeOperationHold hold;
            if (!_nativeOperationHolds.TryGetValue(key, out hold))
            {
                hold = new NativeOperationHold
                {
                    DeadlineMs = now + NativeTargetRetryWindowMs,
                    Relaxed = false,
                    Windows = 1,
                };
                _nativeOperationHolds[key] = hold;
                Diagnostics.FlightRecorder.Note("net native target retry op=" + operationId +
                                                  " " + detail);
            }
            windows = hold.Windows;
            return now < hold.DeadlineMs;
        }

        /// <summary>
        /// Grant one more window after the arbiter declined to settle the report. Deliberately
        /// shorter than the first: this one runs against a world the arbiter has frozen, so it is a
        /// far better test than the first window was, and the feeders it holds up are waiting on it.
        /// </summary>
        private void ExtendUnresolvedOperation(NetOperationKey key, long now)
        {
            NativeOperationHold hold;
            _nativeOperationHolds.TryGetValue(key, out hold);
            hold.DeadlineMs = now + NativeTargetRetryWindowMs / 2;
            hold.Relaxed = true;
            hold.Windows++;
            _nativeOperationHolds[key] = hold;
        }

        /// <summary>How a report and a withdrawal name the same stalled operation.</summary>
        internal const string UnresolvedNativeTargetReason = "native net target did not resolve";
        internal const string UnresolvedMixedTargetReason = "mixed net operation target did not resolve";

        private static string NativeOperationSubject(long operationId, int origin) =>
            "op " + operationId + " from player " + origin;

        private static string MixedOperationSubject(long operationId, int origin) =>
            "mixed op " + operationId + " from player " + origin;

        /// <summary>
        /// Release a hold because the operation succeeded. Withdrawing the report is the point of
        /// holding one: a world reload that was proposed and then did not have to happen is worth
        /// exactly as much in the log as one that did.
        /// </summary>
        private void ClearOperationHold(NetOperationKey key, string reason, string subject,
            string outcome)
        {
            if (!_nativeOperationHolds.Remove(key)) return;
            Diagnostics.ResyncArbiter.Withdraw("net", reason, subject,
                Mod.Service != null ? Mod.Service.NowMs : 0L, outcome);
        }

        /// <summary>
        /// What this machine actually has where the source anchored its endpoint, and why each
        /// candidate was refused.
        ///
        /// This is the fact the log never carried. "No road within reach", "a Medium Road is there
        /// instead of the Small Road named", and "the road is there but six metres lower" are three
        /// entirely different bugs, and all three used to print the same line before reloading the
        /// world. Runs once, on the frame a reload is being considered, so the wider search it does
        /// costs nothing in the normal case.
        /// </summary>
        private string DescribeLocalAnchorNeighbourhood(NetEndpointIntent intent,
            ref NodePool nodes, ref EdgePool edges)
        {
            const float SearchXZ = 16f;
            float3 anchor = new float3(intent.AnchorX, intent.AnchorY, intent.AnchorZ);
            var report = new System.Text.StringBuilder();

            float bestNodeXZ = float.MaxValue, bestNodeDy = 0f;
            NetCellIndex.Enumerator nodeCandidates = nodes.Index.Near(anchor.xz, SearchXZ);
            while (nodeCandidates.MoveNext())
            {
                int i = nodeCandidates.Current;
                float xz = math.distance(nodes.Data[i].m_Position.xz, anchor.xz);
                if (xz >= bestNodeXZ) continue;
                bestNodeXZ = xz;
                bestNodeDy = nodes.Data[i].m_Position.y - anchor.y;
            }

            float bestEdgeXZ = float.MaxValue, bestEdgeDy = 0f;
            string bestEdgePrefab = null;
            NetCellIndex.Enumerator edgeCandidates = edges.Index.Near(anchor.xz, SearchXZ);
            while (edgeCandidates.MoveNext())
            {
                int i = edgeCandidates.Current;
                float t;
                float xz = MathUtils.Distance(edges.Curves[i].m_Bezier.xz, anchor.xz, out t);
                if (xz >= bestEdgeXZ) continue;
                Entity edge = edges.Entities[i];
                if (!EntityManager.Exists(edge)) continue;
                bestEdgeXZ = xz;
                bestEdgeDy = MathUtils.Position(edges.Curves[i].m_Bezier, t).y - anchor.y;
                bestEdgePrefab = EntityManager.HasComponent<global::Game.Prefabs.PrefabRef>(edge)
                    ? PrefabNameOf(EntityManager
                        .GetComponentData<global::Game.Prefabs.PrefabRef>(edge).m_Prefab)
                    : "(no prefab)";
            }

            if (bestEdgeXZ == float.MaxValue)
            {
                report.Append("no road at all within ").Append(SearchXZ.ToString("F0")).Append(" m");
            }
            else
            {
                report.Append("nearest road is '").Append(bestEdgePrefab).Append("' ")
                    .Append(bestEdgeXZ.ToString("F1")).Append(" m away, ")
                    .Append(bestEdgeDy.ToString("F1")).Append(" m in height");
            }

            if (bestNodeXZ != float.MaxValue)
                report.Append("; nearest junction ").Append(bestNodeXZ.ToString("F1"))
                    .Append(" m away, ").Append(bestNodeDy.ToString("F1")).Append(" m in height");

            // The resolver's own tolerances, so a reader can see at a glance whether the candidate
            // above was refused on distance or on identity.
            report.Append(" (must be within ").Append(NativeEdgeResolveXZ.ToString("F0"))
                .Append(" m and ").Append(NativeTargetResolveY.ToString("F0"))
                .Append(" m of height, same prefab, same layers, same owner)");
            return report.ToString();
        }

        private static NativeTargetRetryKey NativeRetryKey(SimulationCommandMessage message,
            NetPlacementCommand command)
        {
            return new NativeTargetRetryKey
            {
                Origin = message.OriginPlayerId,
                Operation = command.OperationId,
                Course = command.CourseIndex,
            };
        }

        /// <summary>
        /// True when every point of <paramref name="span"/> already lies on live same-prefab geometry
        /// - five samples along the curve, each of which must sit on SOME existing edge of that prefab
        /// (the span may map to several local sub-edges). Uses the tight SplitMatch tolerances so a
        /// parallel road or a span rebuilt at another elevation is never wrongly treated as a
        /// duplicate.
        /// </summary>
        private bool SpanAlreadyBuilt(Entity prefab, Bezier4x3 span, ref EdgePool edges)
        {
            for (int s = 0; s <= 4; s++)
            {
                float3 p = MathUtils.Position(span, s / 4f);
                bool covered = false;
                NetCellIndex.Enumerator candidates = edges.Index.Near(p.xz, SplitMatch.TolXZ);
                while (candidates.MoveNext())
                {
                    int i = candidates.Current;
                    Bezier4x3 bez = edges.Curves[i].m_Bezier;
                    float t;
                    if (MathUtils.Distance(bez.xz, p.xz, out t) > SplitMatch.TolXZ) continue;
                    if (math.abs(MathUtils.Position(bez, t).y - p.y) > SplitMatch.TolY) continue;
                    if (EntityManager.GetComponentData<global::Game.Prefabs.PrefabRef>(edges.Entities[i]).m_Prefab
                        != prefab) continue;
                    covered = true;
                    break;
                }
                if (!covered) return false;
            }
            return true;
        }

        /// <summary>
        /// Native multi-course operations use the game's live network search tree for idempotence.
        /// Only edges near each of the five coverage samples are visited, so a large grid scales with
        /// local network density rather than with every edge in the city.
        /// </summary>
        private static bool SpanAlreadyBuilt(Entity prefab, Bezier4x3 span,
            ref LiveEdgeSearchSnapshot search)
        {
            for (int s = 0; s <= 4; s++)
            {
                float3 point = MathUtils.Position(span, s / 4f);
                var iterator = new SpanCoverageIterator
                {
                    Bounds = new Bounds3(
                        point - new float3(SplitMatch.TolXZ, SplitMatch.TolY, SplitMatch.TolXZ),
                        point + new float3(SplitMatch.TolXZ, SplitMatch.TolY, SplitMatch.TolXZ)),
                    Point = point,
                    Prefab = prefab,
                    Curves = search.Curves,
                    Prefabs = search.Prefabs,
                    Owners = search.Owners,
                    Temps = search.Temps,
                    Deleted = search.Deleted,
                };
                search.Tree.Iterate(ref iterator);
                if (!iterator.Covered) return false;
            }
            return true;
        }

        /// <summary>
        /// True when the course's interior (away from both endpoints, which
        /// <see cref="ClassifyEndpoint"/> already resolved) comes within splitting range of any
        /// existing edge — a transversal crossing or a lengthwise overlap. The game cuts every such
        /// edge during Temp generation, so the course counts against the one-splitting-course-per-batch
        /// rule even though neither endpoint classifies as a split. The fallback probe uses native
        /// connection layers and physical widths; a conservative false positive only serializes work,
        /// while a false negative could place two conflicting split courses in one commit.
        /// </summary>
        private bool BodyTouchesExistingEdge(Bezier4x3 course, NetPrefabInfo placedInfo,
            ref EdgePool edges)
        {
            // The control hull contains the curve, so an expanded-AABB miss is an exact reject.
            float3 lo = math.min(math.min(course.a, course.b), math.min(course.c, course.d))
                - new float3(MaxEndpointSearch, VerticalSnapTol, MaxEndpointSearch);
            float3 hi = math.max(math.max(course.a, course.b), math.max(course.c, course.d))
                + new float3(MaxEndpointSearch, VerticalSnapTol, MaxEndpointSearch);

            // Sample tightly enough (≈ EdgeSnapDistance apart, via the control-polygon length upper
            // bound) that a perpendicular crossing cannot slip between two samples.
            float approxLen = math.distance(course.a, course.b) + math.distance(course.b, course.c)
                + math.distance(course.c, course.d);
            int samples = math.clamp((int)(approxLen / EdgeSnapDistance), 8, 128);

            NetCellIndex.Enumerator candidates = edges.Index.Overlapping(lo.xz, hi.xz);
            while (candidates.MoveNext())
            {
                int i = candidates.Current;
                Bezier4x3 bez = edges.Curves[i].m_Bezier;
                float3 elo = math.min(math.min(bez.a, bez.b), math.min(bez.c, bez.d));
                float3 ehi = math.max(math.max(bez.a, bez.b), math.max(bez.c, bez.d));
                if (math.any(elo > hi) || math.any(ehi < lo)) continue;

                Entity candidate = edges.Entities[i];
                NetPrefabInfo targetInfo = default(NetPrefabInfo);
                if (EntityManager.HasComponent<global::Game.Prefabs.PrefabRef>(candidate))
                    targetInfo = NetInfoOf(EntityManager.GetComponentData<global::Game.Prefabs.PrefabRef>(candidate).m_Prefab);
                if (!LayersCanConnect(placedInfo, targetInfo)) continue;
                float touchDistance = math.max(EdgeSnapDistance,
                    placedInfo.HalfWidth + EdgeHalfWidth(candidate, targetInfo.HalfWidth) +
                    placedInfo.SnapDistance);

                for (int s = 1; s < samples; s++)
                {
                    float3 p = MathUtils.Position(course, s / (float)samples);
                    // Endpoint neighbourhoods belong to endpoint classification (reuse/split/merge).
                    if (math.distance(p.xz, course.a.xz) < NodeSnapDistance) continue;
                    if (math.distance(p.xz, course.d.xz) < NodeSnapDistance) continue;
                    float t;
                    if (MathUtils.Distance(bez.xz, p.xz, out t) >= touchDistance) continue;
                    if (math.abs(MathUtils.Position(bez, t).y - p.y) > VerticalSnapTol) continue; // other level
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Resolve where one course endpoint connects, in priority order: an existing real node (reuse),
        /// a building's utility sub-net node (utility nets only - a power/pipe connector stub), a
        /// pending new node another course in this batch creates (merge), a pending batch edge it taps
        /// mid-span (defer until real), an existing real edge - reusing an end node for taps inside its
        /// end zone, splitting for interior taps - else free ground. Returns the snap entity (node to
        /// reuse, or edge to split, or Entity.Null) and, via out params, the split parameter and the
        /// <c>Kind*</c> classification.
        /// </summary>
        private Entity ClassifyEndpoint(float3 p, NetPrefabInfo placedInfo,
            ref NodePool nodes, ref EdgePool edges, ref NodePool ownedNodes,
            NativeList<float3> batchNewNodes, NativeList<Bezier4x3> batchEdges,
            out float t, out int kind)
        {
            t = 0f;
            Entity node = FindNodeAt(p, placedInfo, ref nodes);
            if (node != Entity.Null) { kind = KindReuseNode; return node; }
            // A power line / pipe endpoint lying on a building's connector stub connects to it —
            // the sender drew it onto that stub, so the committed segment ends exactly there.
            if ((placedInfo.ConnectLayers & UtilityConnectLayers) != Layer.None)
            {
                node = FindUtilityNodeAt(p, ref ownedNodes, placedInfo);
                if (node != Entity.Null) { kind = KindReuseConnector; return node; }
            }
            // Coincides with a new node another course in this batch creates -> leave it as a fresh node
            // (Entity.Null) and let GenerateNodesSystem merge the two by exact position.
            if (NearAny(p, batchNewNodes, NodeSnapDistance)) { kind = KindMergeBatch; return Entity.Null; }
            // Taps the middle of an edge this batch is still building -> can't split a not-yet-real edge;
            // defer the whole course to the next cycle, where that edge is real and this becomes a split.
            if (MidSpanOfAnyBatch(p, batchEdges)) { kind = KindDeferBatchEdge; return Entity.Null; }
            Entity edge, endNode;
            FindEdgeAt(p, placedInfo, ref edges, out edge, out t, out endNode);
            // A tap inside an existing edge's end zone reuses that end's node (see FindEdgeAt).
            if (endNode != Entity.Null) { kind = KindReuseNode; return endNode; }
            if (edge != Entity.Null) { kind = KindSplit; return edge; }
            kind = KindFree;
            return Entity.Null;
        }

        /// <summary>
        /// Classify against the source's absolute height first. If that finds open ground and the
        /// placed network is a utility, retry at the height produced by applying the source endpoint
        /// elevation to this machine's surface. A successful second lookup means the endpoint is the
        /// same visible local pipe/cable connection despite terrain or water drift; retaining the
        /// first result for every other case preserves bridge/tunnel level separation.
        /// </summary>
        private Entity ClassifyEndpointWithLocalSurface(Entity prefab, float3 sourcePoint,
            float2 sourceElevation, NetPrefabInfo placedInfo,
            ref NodePool nodes, ref EdgePool edges, ref NodePool ownedNodes,
            NativeList<float3> batchNewNodes, NativeList<Bezier4x3> batchEdges,
            ref TerrainHeightData heightData, ref WaterSurfaceData<SurfaceWater> waterData,
            out float t, out int kind)
        {
            Entity result = ClassifyEndpoint(sourcePoint, placedInfo, ref nodes, ref edges,
                ref ownedNodes, batchNewNodes, batchEdges, out t, out kind);
            if (kind != KindFree) return result;

            float3 projected;
            // Utility layers only here: this path classifies an endpoint the source left on open
            // ground, where a height reference moved for a road is exactly the bridge/tunnel level
            // separation that must be preserved.
            if (!TryProjectEndpointToLocalSurface(prefab, placedInfo, sourcePoint,
                    sourceElevation, false, ref heightData, ref waterData, out projected))
                return result;

            float projectedT;
            int projectedKind;
            Entity projectedResult = ClassifyEndpoint(projected, placedInfo,
                ref nodes, ref edges, ref ownedNodes, batchNewNodes, batchEdges,
                out projectedT, out projectedKind);
            if (projectedKind == KindFree) return result;

            t = projectedT;
            kind = projectedKind;
            _rzLocalSurfaceMatches++;
            return projectedResult;
        }

        /// <summary>
        /// Mark the echo-suppression guard for a course being realized. The capture side
        /// consumes the key of the committed edge's START (its <c>a</c> endpoint), but the
        /// committed geometry can differ from the command: an endpoint that reuses a node
        /// lands exactly ON that node - up to <see cref="NodeSnapDistance"/> from the
        /// commanded point, past the guard's 0.5 m buckets - a split lands on the split
        /// point, and the game may commit the edge with its endpoints swapped. So mark
        /// every position the committed start can be: both raw endpoints plus each end's
        /// resolved snap target. Stale extras simply age out (15 s TTL).
        /// </summary>
        private void MarkRealizeGuards(string prefabName, float3 a, float3 d,
            Entity startSnap, int startKind, float startT,
            Entity endSnap, int endKind, float endT, long now)
        {
            _guard.Mark(ReplicationGuard.Key(prefabName, a), now);
            _guard.Mark(ReplicationGuard.Key(prefabName, d), now);
            MarkResolvedEndpoint(prefabName, startSnap, startKind, startT, now);
            MarkResolvedEndpoint(prefabName, endSnap, endKind, endT, now);
        }

        private void MarkResolvedEndpoint(string prefabName, Entity snap, int kind, float t, long now)
        {
            if (snap == Entity.Null || !EntityManager.Exists(snap)) return;
            float3 position;
            if ((kind == KindReuseNode || kind == KindReuseConnector) && EntityManager.HasComponent<Node>(snap))
                position = EntityManager.GetComponentData<Node>(snap).m_Position;
            else if (kind == KindSplit && EntityManager.HasComponent<Curve>(snap))
                position = MathUtils.Position(EntityManager.GetComponentData<Curve>(snap).m_Bezier, t);
            else return;
            _guard.Mark(ReplicationGuard.Key(prefabName, position), now);
        }

        // Diagnostic tally by endpoint classification.
        private void TallyEnd(int kind)
        {
            switch (kind)
            {
                case KindReuseNode: _rzSnapEnds++; break;
                case KindReuseConnector: _rzSnapEnds++; break;
                case KindMergeBatch: _rzMergeEnds++; break;
                case KindSplit: _rzMidEnds++; break;
                default: _rzFreeEnds++; break;
            }
        }

        private void TallySurfaceCorrection(float start, float end)
        {
            if (start != 0f) { _rzSurfaceCorrections++; _rzSurfaceCorrectionMax = math.max(_rzSurfaceCorrectionMax, math.abs(start)); }
            if (end != 0f) { _rzSurfaceCorrections++; _rzSurfaceCorrectionMax = math.max(_rzSurfaceCorrectionMax, math.abs(end)); }
        }

        /// <summary>
        /// Read this frame's terrain and water surfaces once per realize cycle. The water dependency
        /// completes here so the data is main-thread readable; between simulation steps the handle is
        /// already complete.
        /// </summary>
        private void TakeSurfaceSnapshot(ref TerrainHeightData heightData,
            ref WaterSurfaceData<SurfaceWater> waterData)
        {
            heightData = _terrainSystem.GetHeightData(waitForPending: true);
            JobHandle waterDeps;
            waterData = _waterSystem.GetSurfaceData(out waterDeps);
            waterDeps.Complete();
        }

        /// <summary>
        /// True when <paramref name="p"/> lies within <paramref name="tol"/> (XZ) of any point at a
        /// matching height. The height gate mirrors the game's node merge, which is by position - a
        /// batch containing both a ground road and a bridge above it must not classify the bridge's
        /// endpoint as merging with the ground node.
        /// </summary>
        private static bool NearAny(float3 p, NativeList<float3> points, float tol)
        {
            float2 xz = p.xz;
            float tolSq = tol * tol;
            for (int i = 0; i < points.Length; i++)
                if (math.distancesq(xz, points[i].xz) < tolSq
                    && math.abs(points[i].y - p.y) <= VerticalSnapTol) return true;
            return false;
        }

        /// <summary>
        /// True when <paramref name="point"/> taps the MIDDLE (away from both ends) of any curve this
        /// batch is creating - the same mid-span test as <see cref="FindEdgeAt"/>, against pending
        /// batch edges rather than real ones, with the same height gate (a crossing on another level
        /// is not a tap).
        /// </summary>
        private static bool MidSpanOfAnyBatch(float3 point, NativeList<Bezier4x3> curves)
        {
            float2 p = point.xz;
            for (int i = 0; i < curves.Length; i++)
            {
                Bezier4x3 bez = curves[i];
                float tt;
                if (MathUtils.Distance(bez.xz, p, out tt) >= EdgeSnapDistance) continue;
                float3 sp = MathUtils.Position(bez, tt);
                if (math.abs(sp.y - point.y) > VerticalSnapTol) continue;
                if (math.distance(sp.xz, bez.a.xz) < MinSplitOffset) continue;
                if (math.distance(sp.xz, bez.d.xz) < MinSplitOffset) continue;
                return true;
            }
            return false;
        }
    }
}
