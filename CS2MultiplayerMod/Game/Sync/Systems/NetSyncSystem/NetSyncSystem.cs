using System.Collections.Concurrent;
using System.Collections.Generic;
using Game;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Unity.Entities;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;

using CS2MultiplayerMod.Game.Sync.Infrastructure;
using CS2MultiplayerMod.Game.Sync.Commands;
namespace CS2MultiplayerMod.Game.Sync.Systems.Net
{
    /// <summary>
    /// Replicates roads (net segments) in both directions - the road counterpart to
    /// <see cref="BuildSyncSystem"/>. A built segment is an <see cref="Edge"/> with a
    /// <see cref="Curve"/> (its Bezier) and a net <see cref="PrefabRef"/>; the receiver
    /// rebuilds it via a <see cref="CreationDefinition"/>/<see cref="NetCourse"/> definition
    /// so the game's net systems lay the actual nodes and edges.
    ///
    /// The same origin-skip + <see cref="ReplicationGuard"/> logic as objects prevents
    /// echo loops. Realized geometry may snap/merge differently than the source - exact
    /// fidelity is an in-game tuning item.
    ///
    /// This class is split across files by responsibility: this file holds state + lifecycle +
    /// the receive Observer; <c>.Apply</c> the commit/drain orchestration; <c>.Capture</c> the
    /// host-side detection + diagnostics; <c>.Realize</c> the batch builder + classification;
    /// <c>.Course</c> the NetCourse construction + endpoint geometry + self-test; <c>.Replay</c>
    /// the click stash (gestures the definition gate killed) + swallowed-click replay.
    /// </summary>
    public partial class NetSyncSystem : GameSystemBase
    {
        private readonly ConcurrentQueue<SimulationCommandMessage> _incoming =
            new ConcurrentQueue<SimulationCommandMessage>();
        private readonly ReplicationGuard _guard = new ReplicationGuard();

        private readonly Dictionary<string, int> _diag = new Dictionary<string, int>();
        private long _diagStartMs = -1;
        private int _diagTotal;

        private PrefabSystem _prefabSystem;
        private PrefabIndex _prefabIndex;
        private EntityQuery _createdEdges;
        private EntityQuery _existingNodes;
        private EntityQuery _existingEdges;
        private EntityQuery _ownedNodes;
        private EntityQuery _updatedEdges;
        private EntityQuery _deletedEdges;
        private Observer _observer;

        // Terrain/water samplers for deriving a course endpoint's elevation (Y - surface) - the
        // course must carry it explicitly or the game commits an elevated net (power line, pipe,
        // bridge) as a GROUND net at that Y and terraforms the ground up/down to meet it.
        private global::Game.Simulation.TerrainSystem _terrainSystem;
        private global::Game.Simulation.WaterSystem _waterSystem;

        // Per-net-prefab facts consulted for every course endpoint (connect layers for the utility
        // connector snap, the allowed elevation range for the ground dead zone, the half-width that
        // scales the endpoint snap radii). Prefab entities are stable for the session, so entries
        // never invalidate.
        private struct NetPrefabInfo
        {
            public Layer ConnectLayers;
            public float ElevMin, ElevMax;
            public float HalfWidth;
            public bool Placeable;
        }
        private readonly Dictionary<Entity, NetPrefabInfo> _netInfoCache = new Dictionary<Entity, NetPrefabInfo>();

        // Endpoint → existing-node snap tolerance (metres, XZ). A replicated segment's Bézier
        // endpoints ARE the source node positions, and the same map produces the same node
        // coordinates on every machine, so the matching node sits at ~0 m away.
        //
        // INVARIANT: NodeSnapDistance MUST be >= EdgeSnapDistance - this is the Y-junction / 2nd-edge
        // split fix. When we split an edge at a tap point, the game places the new junction node on
        // that edge's CENTRELINE, up to EdgeSnapDistance away from the (off-centre) tap. A later course
        // that must connect there (the 2nd, 3rd... road of the junction, serialised one split per commit)
        // then looks for a node to reuse at its own endpoint. With the old 1 m tolerance there was a
        // DEAD ZONE: the new node was too far to reuse (> 1 m) yet too close to the fresh edge ends to
        // re-split (< MinSplitOffset 2 m), so the endpoint fell through to FREE ground and the road
        // landed disconnected (verified in a live 2p host log: a 2nd-split endpoint went SPLIT->FREE
        // between commit cycles). A split only ever happens within EdgeSnapDistance of a centreline, so
        // the resulting node is always within EdgeSnapDistance of the tap; matching that here guarantees
        // the reuse and provably closes the dead zone. (This is a client-side realize tweak only - it
        // does not change the wire, the capture side, or building placement.)
        private const float NodeSnapDistance = 2.0f;

        // How close (XZ) an endpoint must sit to an existing edge's centreline, away from its ends, to
        // count as a mid-span tap that SPLITS that edge (a T-junction). Acted on by FindEdgeAt /
        // ClassifyEndpoint: the split goes through the game's own Temp + ApplyTool path, so it is
        // non-destructive. MinSplitOffset keeps a near-the-end tap from splitting (it reuses the node).
        private const float EdgeSnapDistance = 2.0f;
        private const float MinSplitOffset = 2.0f;

        // Max height difference (metres) for an endpoint to connect to (or split) existing geometry.
        // Anything further above/below is a different LEVEL: a bridge whose endpoint passes over a
        // ground road must NOT reuse the ground node or split the ground edge underneath — it crosses,
        // it doesn't connect. Genuine connections happen at matching heights (the Bézier Y is
        // transmitted and terrain is synced, so machine-to-machine drift stays well under a metre),
        // while stacked levels differ by at least a full elevation step.
        private const float VerticalSnapTol = 3.0f;

        // Utility net layers whose endpoints may connect to a building's OWNED sub-net (a power
        // plant's high-voltage connector stub, a water facility's pipe stub). Only these relax the
        // Owner exclusion below — a road endpoint must still never snap to a building's driveway or
        // a road's hidden lane sub-nets.
        private const Layer UtilityConnectLayers = Layer.PowerlineLow | Layer.PowerlineHigh |
            Layer.WaterPipe | Layer.SewagePipe | Layer.StormwaterPipe | Layer.ResourceLine;

        // Below this |curve Y - local terrain| a road-like net (one whose allowed elevation range
        // spans 0) counts as GROUND (course elevation 0): a committed ground road's Y deviates from
        // the pre-build terrain by the game's own grading (cut/fill on slopes), which must not be
        // mistaken for a raised/lowered placement. Real raised segments start at a full elevation
        // step (2.5 m); fixed-elevation nets (power lines, pipes) skip the dead zone entirely.
        private const float GroundElevationDeadZone = 2.0f;

        // Endpoint classifications used when building a realize batch (see ClassifyEndpoint).
        private const int KindFree = 0;          // open ground → a fresh node
        private const int KindReuseNode = 1;     // coincides with an existing real node → reuse it
        private const int KindMergeBatch = 2;    // coincides with a NEW node another course in this
                                                 // batch creates → GenerateNodesSystem merges them
        private const int KindSplit = 3;         // mid-span on an existing real edge → split it
        private const int KindDeferBatchEdge = 4;// taps the middle of a not-yet-real batch edge → defer
        private const int KindReuseConnector = 5;// coincides with a building's utility sub-net node
                                                 // (power/pipe connector) → reuse it (utility nets only)

        // Realize-side 5 s counters (INFO): segments built, and per endpoint whether it reused an
        // existing node, merged with another new node in the same batch, split an existing edge
        // (T-junction), or was on free ground.
        private int _rzSegments, _rzSnapEnds, _rzMergeEnds, _rzMidEnds, _rzFreeEnds;

        // Capture-side 5 s peak counts of net-edge lifecycle tags, to reveal how CS2 represents an
        // edge split: an in-place reuse of the original shows up as Updated (NOT Created/Deleted).
        private int _peakCreated, _peakUpdated, _peakDeleted;

        // Capture-side 5 s count of Created edges we dropped because they were split halves (sub-curves
        // of a same-frame Deleted edge) rather than something the player drew.
        private int _capFilteredHalves;

        // Pieces of a span whose delete is replicated this frame (rebuilt at another height, or
        // partially consumed by a placement such as a roundabout). Held back one frame so the delete
        // travels first — otherwise it would tear down the fresh pieces on arrival (they lie exactly
        // on the deleted span). See CaptureNewEdges.
        private readonly List<NetPlacementCommand> _deferredSpanPieces = new List<NetPlacementCommand>();

        // --- Temp + ApplyTool realize (the real fix for T-junctions) -----------------------------
        // The shipped realize uses CreationFlags.Permanent, which makes GenerateEdgesSystem build a
        // finished edge directly and NEVER enters the Temp/ApplyTool pipeline — so it can create and
        // reuse-node but can never SPLIT an existing edge (ApplyNetSystem, the only splitter, runs
        // ONLY in SystemUpdatePhase.ApplyTool, gated by ToolOutputSystem on applyMode==Apply). This
        // path instead builds a NON-Permanent definition (→ Temp edge) and drives the ApplyTool phase
        // ourselves so the game splits/connects/zones natively (see the .Apply / .Course partials).
        private global::Game.Tools.ToolSystem _toolSystem;
        private EntityQuery _tempNetEntities;
        // ALL live preview Temps (net, object, zone, area …) and the definitions created this frame —
        // what PrepareDefinitionFrame clears so a commit while a build tool is out contains ONLY our
        // batch, never the player's preview (see Apply.cs).
        private EntityQuery _allTempEntities;
        private EntityQuery _freshDefinitions;
        private bool _pendingApply;
        // After a commit we must WAIT for its Temp entities to clear (the committed nodes/edges only
        // become query-able then) before building the next batch — otherwise a course that should
        // connect to the just-committed geometry cannot find it and lands on free ground.
        private bool _awaitingDrain;
        private int _armTick;
        private int _drainArmTick;
        // Frames spent draining. With a build tool out its preview Temps regenerate every frame, so
        // "net Temps == 0" never happens — the committed geometry is query-able one frame after the
        // ApplyTool pass, and the frame counter releases the drain then (Apply.cs).
        private int _drainFrames;
        // True only on the frame a self-driven ApplyTool pass commits our batch: every non-Temp
        // Created edge at that frame's ModificationEnd is from OUR pass (the player's own gesture
        // never commits on a self-flip frame - that branch runs only when the tool isn't applying),
        // so capture skips exactly that one frame instead of a wall-clock window that also
        // swallowed roads the player built while remote batches streamed in. Set by the commit
        // flip (Apply.cs), cleared by the next frame's BeginRealizeFrame.
        private bool _suppressCaptureThisFrame;
        // One preview wipe per realize frame (see PrepareDefinitionFrame); reset by BeginRealizeFrame.
        private bool _prepDoneThisFrame;
        // Spans this machine realized from remote commands recently. A realize commit can trigger the
        // game's node reduction, which re-surfaces the just-built span as a LOCAL Updated/Created edge
        // (merged with a neighbour); these records let the capture side (NetReplaceSync's extension
        // detection) recognise that geometry as remote work, not something to broadcast back.
        private readonly List<(Colossal.Mathematics.Bezier4x3 curve, long expiresMs)> _recentRealizedSpans =
            new List<(Colossal.Mathematics.Bezier4x3, long)>();
        // Recovery hook for an armed-but-never-committed batch: while a build tool is out, the game's
        // per-frame clear pass deletes EVERY Temp entity (it is not scoped to the tool's own preview),
        // which destroys an armed batch before its commit can run. Whoever arms a commit leaves a
        // callback here that re-queues the batch's source commands; RealizePending invokes it the frame
        // it sees the wipe coming (or on window expiry) so the batch is rebuilt instead of lost.
        private System.Action _onCommitLost;
        // Realize-frame counter (BeginRealizeFrame). Ages the click stash below: gestures the gate
        // killed at the end of frame N are replayable only during frame N+1 (see the .Replay partial).
        private int _realizeFrame;
        // The local player's gestures DefinitionGateSystem killed on the last armed frame: plain net
        // courses, bulldoze targets and free-standing object ghosts. If the player's click lands
        // inside the armed window (it would apply nothing - the definitions that were to become its
        // committing Temps are gone) these are re-encoded as ordinary sync commands instead.
        private readonly List<(Entity prefab, Colossal.Mathematics.Bezier4x3 curve, float length)> _stashCourses =
            new List<(Entity, Colossal.Mathematics.Bezier4x3, float)>();
        private readonly List<Entity> _stashBulldozes = new List<Entity>();
        private readonly List<(Entity prefab, Unity.Mathematics.float3 position, Unity.Mathematics.quaternion rotation)> _stashObjects =
            new List<(Entity, Unity.Mathematics.float3, Unity.Mathematics.quaternion)>();
        private int _stashFrame = -1;
        // Replayed local courses waiting to realize. Drained ahead of _incoming, and exempt from the
        // terrain deferral below - their Y was measured against this machine's own terrain.
        private readonly List<SimulationCommandMessage> _localReplays = new List<SimulationCommandMessage>();
        /// <summary>
        /// Set by <see cref="SyncRealizeSystem"/> while remote terrain edits are backlogged: no NEW
        /// remote course realizes until terrain catches up (a course realized against pre-terraform
        /// ground commits at the wrong height - craters, buried streets, missed height-gated snaps).
        /// In-flight commits still finish, and local click-replays are exempt.
        /// </summary>
        public bool DeferForTerrain;
        // Consecutive expired-window replays (reset by any successful commit). A batch whose
        // definitions the game always rejects would otherwise rebuild forever.
        private int _expiryReplays;

        protected override void OnCreate()
        {
            base.OnCreate();

            Mod.log.Info(nameof(NetSyncSystem) + " ready.");
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _prefabIndex = new PrefabIndex(_prefabSystem, GetEntityQuery(ComponentType.ReadOnly<PrefabData>()));

            _toolSystem = World.GetOrCreateSystemManaged<global::Game.Tools.ToolSystem>();
            // Live net Temp entities (a tool preview, or our own pre-commit definitions), used to
            // confirm a commit (count drops to 0 after ApplyTool) and to detect a tool preview.
            // Deleted is excluded: a wiped Temp lingers until Cleanup, and counting those corpses
            // as live made the commit/drain checks act on a batch that no longer exists.
            _tempNetEntities = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Temp>() },
                Any = new[] { ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<Node>() },
                None = new[] { ComponentType.ReadOnly<Deleted>() },
            });

            // Every live preview Temp of any domain — what the game's own clear pass operates on.
            _allTempEntities = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Temp>() },
                None = new[] { ComponentType.ReadOnly<Deleted>() },
            });

            // Definition entities created THIS frame (they carry Updated only on their birth frame;
            // stale ones are inert — the generate systems consume Updated definitions only).
            _freshDefinitions = GetEntityQuery(
                ComponentType.ReadOnly<CreationDefinition>(),
                ComponentType.ReadOnly<Updated>());

            _createdEdges = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Created>(),
                    ComponentType.ReadOnly<Edge>(),
                    ComponentType.ReadOnly<Curve>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                    // Exclude sub-networks owned by a road/building (the invisible
                    // pedestrian/car/road paths and lane connectors the game auto-creates).
                    ComponentType.ReadOnly<Owner>(),
                },
            });

            // Standalone net nodes we can snap incoming segment endpoints onto. Owner-less so
            // we only ever connect to real roads/paths, never to a building's or road's hidden
            // sub-network nodes; Temp/Deleted excluded so we never snap to a preview or a node
            // that is being torn down this frame.
            _existingNodes = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Node>() },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Owner>(),
                },
            });

            // Read-only: standalone edges, used to classify an incoming endpoint as a mid-span tap.
            _existingEdges = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<Curve>() },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Owner>(),
                },
            });

            // OWNED nodes — building sub-net stubs among them. A power line / pipe endpoint may
            // connect to one of these when its net layers say so (see UtilityConnectLayers and
            // FindUtilityNodeAt); everything else keeps ignoring them, exactly like _existingNodes.
            _ownedNodes = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Node>(),
                    ComponentType.ReadOnly<Owner>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                },
            });

            _terrainSystem = World.GetOrCreateSystemManaged<global::Game.Simulation.TerrainSystem>();
            _waterSystem = World.GetOrCreateSystemManaged<global::Game.Simulation.WaterSystem>();

            // Diagnostic: pre-existing edges whose geometry CHANGED this frame (Updated but NOT
            // freshly Created) — exactly what an in-place split of the original edge looks like.
            _updatedEdges = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<Curve>(), ComponentType.ReadOnly<Updated>() },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Created>(),
                    ComponentType.ReadOnly<Owner>(),
                },
            });

            // Diagnostic: edges being removed this frame.
            _deletedEdges = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<Curve>(), ComponentType.ReadOnly<Deleted>() },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Owner>(),
                },
            });

            if (Mod.Service != null)
            {
                _observer = new Observer(_incoming);
                Mod.Service.Session.AddObserver(_observer);
            }
        }

        protected override void OnDestroy()
        {
            if (_observer != null && Mod.Service != null)
                Mod.Service.Session.RemoveObserver(_observer);
            base.OnDestroy();
        }

        protected override void OnUpdate()
        {
            MultiplayerService service = Mod.Service;
            if (service == null) return;

            MultiplayerSession session = service.Session;
            if (!service.GameplaySyncReady)
            {
                if (_deferredSpanPieces.Count > 0) _deferredSpanPieces.Clear();
                return;
            }

            long now = service.NowMs;
            _guard.Prune(now);

            // Sample net-edge lifecycle tags every frame (peak over the 5 s window). Runs at
            // ModificationEnd where the one-frame Created/Updated/Deleted tags are still alive.
            _peakCreated = System.Math.Max(_peakCreated, _createdEdges.CalculateEntityCount());
            _peakUpdated = System.Math.Max(_peakUpdated, _updatedEdges.CalculateEntityCount());
            _peakDeleted = System.Math.Max(_peakDeleted, _deletedEdges.CalculateEntityCount());

            FlushDeferredSpanPieces(session);
            CaptureNewEdges(session, now);
            FlushDiagnostics(now);
        }

        private sealed class Observer : SessionObserver
        {
            private readonly ConcurrentQueue<SimulationCommandMessage> _sink;
            public Observer(ConcurrentQueue<SimulationCommandMessage> sink) { _sink = sink; }

            public override void OnCommandReceived(SimulationCommandMessage command)
            {
                if (command.CommandId != NetPlacementCommand.Id) return;
                SyncInbox.Push(_sink, command);
                // Network thread: log on RECEIPT so a missing realize can be told apart from a missing
                // send. The body is the encoded Bézier; we don't decode here (cheap + thread-safe).
            }
        }
    }
}
