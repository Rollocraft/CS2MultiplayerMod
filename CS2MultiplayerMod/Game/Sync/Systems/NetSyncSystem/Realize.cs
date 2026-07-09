using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Net;
using Game.Simulation;
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
    // Realize (client) side of NetSyncSystem: drain queued NetPlacementCommands into a single batch,
    // classify where each endpoint connects (reuse node / merge new node / split edge / defer / free),
    // and queue the courses — letting the game merge shared new nodes and serialising existing-edge
    // splits to one per batch.
    public partial class NetSyncSystem
    {
        private void RealizeIncoming(MultiplayerSession session, long now)
        {
            if (_incoming.IsEmpty) return;

            // One batch in flight at a time (a course built before the previous batch's nodes/edges
            // are query-able could not connect to them), and never on the frame the player's own
            // gesture applies. Any other tool state realizes LIVE: the def-frame hijack below wipes
            // the player's preview for one frame and the commit overrides the tool's applyMode - see
            // CanBuildDefinitions / PrepareDefinitionFrame in the .Apply partial.
            if (!CanBuildDefinitions) return;

            // Drain a bounded working set out of the concurrent queue so we can build a whole batch in a
            // SINGLE ApplyTool pass. Committing several non-splitting courses together is exactly how the
            // net tool places a multi-segment drag or a grid: GenerateNodesSystem then merges their
            // coincident NEW nodes by exact position, so shared endpoints become one node and the network
            // connects. (Building one course per frame — the previous behaviour — defeated that merge and
            // left every segment on its own isolated node.)
            const int MaxBatch = 64;
            var work = new List<SimulationCommandMessage>(MaxBatch);
            SimulationCommandMessage msg;
            while (work.Count < MaxBatch && _incoming.TryDequeue(out msg)) work.Add(msg);
            if (work.Count == 0) return;

            NativeArray<Entity> nodeEntities = default, edgeEntities = default, ownedNodeEntities = default;
            NativeArray<Node> nodeData = default, ownedNodeData = default;
            NativeArray<Curve> edgeCurves = default;
            TerrainHeightData heightData = default;
            WaterSurfaceData<SurfaceWater> waterData = default;
            bool haveSnapshot = false;
            int built = 0;
            bool splitUsed = false;

            // Source messages of the courses this batch builds, retained until the commit actually
            // runs: if the armed batch is wiped before committing (see _onCommitLost) they are
            // re-enqueued and the batch rebuilds instead of being lost.
            List<SimulationCommandMessage> retained = null;

            // New nodes / edges this batch will create, so a later course can recognise (a) an endpoint
            // that coincides with one of our pending new nodes — it will MERGE, so it is not a split —
            // and (b) an endpoint that taps the middle of a pending batch edge, which must wait until
            // that edge is real (deferred to the next, post-commit cycle).
            var batchNewNodes = new NativeList<float3>(MaxBatch, Allocator.Temp);
            var batchEdges = new NativeList<Bezier4x3>(MaxBatch, Allocator.Temp);

            try
            {
                for (int i = 0; i < work.Count; i++)
                {
                    SimulationCommandMessage message = work[i];
                    if (message.OriginPlayerId == session.LocalPlayerId)
                    {
                        continue;
                    }

                    NetPlacementCommand command;
                    try { command = NetPlacementCommand.Decode(message.Body); }
                    catch (System.Exception ex) { Mod.log.Warn("[MP] NetSync: dropping malformed command: " + ex.Message); continue; }

                    Entity prefab;
                    if (!_prefabIndex.TryResolve(command.PrefabName, out prefab))
                    {
                        Mod.log.Warn("[MP] NetSync realize: unknown prefab '" + command.PrefabName +
                                     "' from player " + message.OriginPlayerId + "; skipping.");
                        continue;
                    }

                    var a = new float3(command.Ax, command.Ay, command.Az);
                    var b = new float3(command.Bx, command.By, command.Bz);
                    var c = new float3(command.Cx, command.Cy, command.Cz);
                    var d = new float3(command.Dx, command.Dy, command.Dz);
                    var bezier = new Bezier4x3 { a = a, b = b, c = c, d = d };

                    if (!haveSnapshot)
                    {
                        nodeEntities = _existingNodes.ToEntityArray(Allocator.Temp);
                        nodeData = _existingNodes.ToComponentDataArray<Node>(Allocator.Temp);
                        edgeEntities = _existingEdges.ToEntityArray(Allocator.Temp);
                        edgeCurves = _existingEdges.ToComponentDataArray<Curve>(Allocator.Temp);
                        // Building sub-net stubs a utility endpoint may connect to (FindUtilityNodeAt).
                        ownedNodeEntities = _ownedNodes.ToEntityArray(Allocator.Temp);
                        ownedNodeData = _ownedNodes.ToComponentDataArray<Node>(Allocator.Temp);
                        // Surface samplers for the courses' endpoint elevations (see EndElevation).
                        // The water dependency completes here so the data is main-thread readable;
                        // between simulation steps the handle is already complete.
                        heightData = _terrainSystem.GetHeightData();
                        JobHandle waterDeps;
                        waterData = _waterSystem.GetSurfaceData(out waterDeps);
                        waterDeps.Complete();
                        haveSnapshot = true;
                    }

                    // Idempotence: skip a span this machine already has as live same-prefab geometry.
                    // The game's node reduction can merge a committed span into a neighbour and
                    // re-surface it as a wider create on the other machine; without this check that
                    // echo would stack a duplicate road on top of the existing one (and ping-pong).
                    // The tolerances are SplitMatch-tight (~1 m), far below a parallel lane, and a
                    // span rebuilt at another elevation fails the height match — never wrongly skipped.
                    if (SpanAlreadyBuilt(prefab, bezier, edgeEntities, edgeCurves))
                    {
                        continue;
                    }

                    Layer placedConnect = NetInfoOf(prefab).ConnectLayers;
                    int startKind, endKind;
                    float startT, endT;
                    Entity startSnap = ClassifyEndpoint(a, placedConnect, nodeEntities, nodeData,
                        edgeEntities, edgeCurves, ownedNodeEntities, ownedNodeData,
                        batchNewNodes, batchEdges, out startT, out startKind);
                    Entity endSnap = ClassifyEndpoint(d, placedConnect, nodeEntities, nodeData,
                        edgeEntities, edgeCurves, ownedNodeEntities, ownedNodeData,
                        batchNewNodes, batchEdges, out endT, out endKind);

                    // The elevation each course end must carry (a reused node's committed value, or
                    // derived from the transmitted Y against the local surface — see EndElevation).
                    float2 startElevation = EndElevation(prefab, startSnap, startKind, a, ref heightData, ref waterData);
                    float2 endElevation = EndElevation(prefab, endSnap, endKind, d, ref heightData, ref waterData);


                    bool defer = startKind == KindDeferBatchEdge || endKind == KindDeferBatchEdge;
                    bool splittingCourse = startKind == KindSplit || endKind == KindSplit;
                    // A course whose BODY crosses or hugs an existing edge splits it at Temp generation
                    // exactly like an endpoint tap, but ClassifyEndpoint only sees the two endpoints —
                    // probe the span interior too, or two fast drags across the same road slip into one
                    // batch and hit the stale-edge crash below.
                    if (!defer && !splittingCourse)
                        splittingCourse = BodyTouchesExistingEdge(bezier, edgeCurves);
                    // At most ONE existing-edge-splitting course per batch: two courses committed in the
                    // same ApplyTool pass that both touch an existing edge can make ApplyNetSystem
                    // dereference a stale (already-split/deleted) edge and crash the process natively.
                    // Courses touching nothing pre-existing are unbounded (safe — the net tool grids
                    // many at once).
                    if (!defer && splittingCourse && splitUsed) defer = true;

                    if (defer)
                    {
                        string why = (startKind == KindDeferBatchEdge || endKind == KindDeferBatchEdge)
                            ? "taps a not-yet-real batch edge"
                            : "2nd existing-edge touch this batch (serialised to avoid a stale-edge crash)";
                        // Re-queue this and every remaining item, in order, for the next cycle - after
                        // this batch has committed and its edges/nodes have become real.
                        for (int j = i; j < work.Count; j++) _incoming.Enqueue(work[j]);
                        break;
                    }

                    MarkRealizeGuards(command.PrefabName, a, d, startSnap, startKind, startT,
                        endSnap, endKind, endT, now);
                    try
                    {
                        // First course of the frame: make the frame safe for our definitions while a
                        // build tool is out (wipes its preview + fresh definitions; see .Apply).
                        if (built == 0) PrepareDefinitionFrame();
                        CreateTempCourse(prefab, bezier, command.Length, startSnap, startT, endSnap, endT,
                            startElevation, endElevation);
                        built++;
                        RecordRealizedSpan(bezier);
                        (retained ?? (retained = new List<SimulationCommandMessage>())).Add(message);
                        _rzSegments++;
                        TallyEnd(startKind);
                        TallyEnd(endKind);
                        if (splittingCourse) splitUsed = true;
                        if (startKind == KindFree) batchNewNodes.Add(a);
                        if (endKind == KindFree) batchNewNodes.Add(d);
                        batchEdges.Add(bezier);
                    }
                    catch (System.Exception ex)
                    {
                        Mod.log.Error("[MP] NetSync realize FAILED for '" + command.PrefabName + "': " + ex);
                    }
                }
            }
            finally
            {
                if (haveSnapshot)
                {
                    nodeEntities.Dispose(); nodeData.Dispose(); edgeEntities.Dispose(); edgeCurves.Dispose();
                    ownedNodeEntities.Dispose(); ownedNodeData.Dispose();
                }
                batchNewNodes.Dispose();
                batchEdges.Dispose();
            }

            // Arm the commit: these definitions become Temp edges at this frame's Modification, and
            // next frame's RealizePending flips applyMode=Apply so ToolOutputSystem commits them all.
            if (built > 0)
            {
                _pendingApply = true;
                _armTick = System.Environment.TickCount;
                List<SimulationCommandMessage> batchSources = retained;
                _onCommitLost = delegate
                {
                    for (int j = 0; j < batchSources.Count; j++) _incoming.Enqueue(batchSources[j]);
                };
                Diagnostics.FlightRecorder.Note("net build batch armed n=" + built + (splitUsed ? " +split" : ""));
            }
            else
            {
            }
        }

        /// <summary>
        /// True when every point of <paramref name="span"/> already lies on live same-prefab geometry
        /// - five samples along the curve, each of which must sit on SOME existing edge of that prefab
        /// (the span may map to several local sub-edges). Uses the tight SplitMatch tolerances so a
        /// parallel road or a span rebuilt at another elevation is never wrongly treated as a
        /// duplicate.
        /// </summary>
        private bool SpanAlreadyBuilt(Entity prefab, Bezier4x3 span,
            NativeArray<Entity> edgeEntities, NativeArray<Curve> edgeCurves)
        {
            for (int s = 0; s <= 4; s++)
            {
                float3 p = MathUtils.Position(span, s / 4f);
                bool covered = false;
                for (int i = 0; i < edgeCurves.Length; i++)
                {
                    Bezier4x3 bez = edgeCurves[i].m_Bezier;
                    float t;
                    if (MathUtils.Distance(bez.xz, p.xz, out t) > SplitMatch.TolXZ) continue;
                    if (math.abs(MathUtils.Position(bez, t).y - p.y) > SplitMatch.TolY) continue;
                    if (EntityManager.GetComponentData<global::Game.Prefabs.PrefabRef>(edgeEntities[i]).m_Prefab
                        != prefab) continue;
                    covered = true;
                    break;
                }
                if (!covered) return false;
            }
            return true;
        }

        /// <summary>
        /// True when the course's interior (away from both endpoints, which
        /// <see cref="ClassifyEndpoint"/> already resolved) comes within splitting range of any
        /// existing edge — a transversal crossing or a lengthwise overlap. The game cuts every such
        /// edge during Temp generation, so the course counts against the one-splitting-course-per-batch
        /// rule even though neither endpoint classifies as a split. Layer-blind and endpoint-snap-blind
        /// on purpose: a false positive only defers a course one cycle, a false negative is a CTD.
        /// </summary>
        private static bool BodyTouchesExistingEdge(Bezier4x3 course, NativeArray<Curve> edgeCurves)
        {
            // The control hull contains the curve, so an expanded-AABB miss is an exact reject.
            float3 lo = math.min(math.min(course.a, course.b), math.min(course.c, course.d))
                - new float3(EdgeSnapDistance, VerticalSnapTol, EdgeSnapDistance);
            float3 hi = math.max(math.max(course.a, course.b), math.max(course.c, course.d))
                + new float3(EdgeSnapDistance, VerticalSnapTol, EdgeSnapDistance);

            // Sample tightly enough (≈ EdgeSnapDistance apart, via the control-polygon length upper
            // bound) that a perpendicular crossing cannot slip between two samples.
            float approxLen = math.distance(course.a, course.b) + math.distance(course.b, course.c)
                + math.distance(course.c, course.d);
            int samples = math.clamp((int)(approxLen / EdgeSnapDistance), 8, 128);

            for (int i = 0; i < edgeCurves.Length; i++)
            {
                Bezier4x3 bez = edgeCurves[i].m_Bezier;
                float3 elo = math.min(math.min(bez.a, bez.b), math.min(bez.c, bez.d));
                float3 ehi = math.max(math.max(bez.a, bez.b), math.max(bez.c, bez.d));
                if (math.any(elo > hi) || math.any(ehi < lo)) continue;

                for (int s = 1; s < samples; s++)
                {
                    float3 p = MathUtils.Position(course, s / (float)samples);
                    // Endpoint neighbourhoods belong to endpoint classification (reuse/split/merge).
                    if (math.distance(p.xz, course.a.xz) < NodeSnapDistance) continue;
                    if (math.distance(p.xz, course.d.xz) < NodeSnapDistance) continue;
                    float t;
                    if (MathUtils.Distance(bez.xz, p.xz, out t) >= EdgeSnapDistance) continue;
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
        /// mid-span (defer until real), an existing real edge it taps mid-span (split), else free ground.
        /// Returns the snap entity (node to reuse, or edge to split, or Entity.Null) and, via out params,
        /// the split parameter and the <c>Kind*</c> classification.
        /// </summary>
        private Entity ClassifyEndpoint(float3 p, Layer placedConnect,
            NativeArray<Entity> nodeEntities, NativeArray<Node> nodeData,
            NativeArray<Entity> edgeEntities, NativeArray<Curve> edgeCurves,
            NativeArray<Entity> ownedNodeEntities, NativeArray<Node> ownedNodeData,
            NativeList<float3> batchNewNodes, NativeList<Bezier4x3> batchEdges,
            out float t, out int kind)
        {
            t = 0f;
            Entity node = FindNodeAt(p, nodeEntities, nodeData);
            if (node != Entity.Null) { kind = KindReuseNode; return node; }
            // A power line / pipe endpoint lying on a building's connector stub connects to it —
            // the sender drew it onto that stub, so the committed segment ends exactly there.
            if ((placedConnect & UtilityConnectLayers) != Layer.None)
            {
                node = FindUtilityNodeAt(p, ownedNodeEntities, ownedNodeData, placedConnect);
                if (node != Entity.Null) { kind = KindReuseConnector; return node; }
            }
            // Coincides with a new node another course in this batch creates -> leave it as a fresh node
            // (Entity.Null) and let GenerateNodesSystem merge the two by exact position.
            if (NearAny(p, batchNewNodes, NodeSnapDistance)) { kind = KindMergeBatch; return Entity.Null; }
            // Taps the middle of an edge this batch is still building -> can't split a not-yet-real edge;
            // defer the whole course to the next cycle, where that edge is real and this becomes a split.
            if (MidSpanOfAnyBatch(p, batchEdges)) { kind = KindDeferBatchEdge; return Entity.Null; }
            Entity edge;
            FindEdgeAt(p, edgeEntities, edgeCurves, out edge, out t);
            if (edge != Entity.Null) { kind = KindSplit; return edge; }
            kind = KindFree;
            return Entity.Null;
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
