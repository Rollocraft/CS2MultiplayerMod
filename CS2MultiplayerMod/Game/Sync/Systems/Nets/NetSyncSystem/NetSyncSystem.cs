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
    /// echo loops. Local tool definitions are captured before their endpoint/split/elevation intent
    /// is reduced to final geometry; portable anchors resolve the equivalent local entities.
    ///
    /// This class is split across files by responsibility. Each file opens with a note saying what
    /// belongs in it; broadly, <c>Apply*</c> is the commit and drain orchestration, <c>Capture</c>
    /// the host-side detection and diagnostics, <c>Realize*</c> the batch builder and
    /// classification, <c>Intent*</c> the local gesture read back out of the tool's definitions,
    /// <c>MixedOperation*</c> the place/delete/replace gesture, and <c>Course</c> the NetCourse
    /// construction, endpoint geometry and self-test.
    /// </summary>
    // The queues, queries, prefab facts and snapping tolerances the net sync works from, plus the
    // per-cycle counters the diagnostics read.
    //
    // The state the commit machinery keeps - tool systems, temp isolation, what is armed and what
    // is draining - is in ApplyState.cs, next to the Apply*.cs files that use it. Creating the
    // system and draining it are in Lifecycle.cs.
    public partial class NetSyncSystem : GameSystemBase
    {
        private const int NetInboxCap = 4096;
        // A mixed operation may be much larger than one 4 KiB placement course. Keep the shared
        // queue byte footprint bounded even if a peer sends only maximum-size atomic envelopes.
        private const int MixedNetInboxAdmissionCap = 32;
        private readonly ConcurrentQueue<SimulationCommandMessage> _incoming =
            new ConcurrentQueue<SimulationCommandMessage>();
        // Commands deferred from the front of a drained batch must remain ahead of messages that
        // were still in the concurrent inbox. Only the simulation thread touches this prefix list.
        private readonly List<SimulationCommandMessage> _remoteDeferred =
            new List<SimulationCommandMessage>();
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
        private EntityQuery _ownedEdges;
        private EntityQuery _updatedEdges;
        private EntityQuery _deletedEdges;
        private Observer _observer;

        private global::Game.Net.SearchSystem _netSearchSystem;

        // Surface samplers. FreeHeight course endpoints are recomputed as local surface + elevation,
        // so reproducing their source height means solving against this machine's surface. Other
        // endpoints keep their captured Y and must not be reprojected (see SurfaceHeightAt /
        // EndElevation).
        private global::Game.Simulation.TerrainSystem _terrainSystem;
        private global::Game.Simulation.WaterSystem _waterSystem;

        // Per-net-prefab facts consulted for every course endpoint (connect layers for the utility
        // connector snap, the half-width that scales the endpoint snap radii, the elevation step at
        // which the net becomes a bridge/tunnel). Prefab entities are stable for the session, so
        // entries never invalidate.
        private struct NetPrefabInfo
        {
            public Layer RequiredLayers;
            public Layer ConnectLayers;
            public float HalfWidth;
            public float SnapDistance;
            public float ElevationLimit;
            public float MaxSlopeSteepness;
            public bool HasElevationRange;
            public float ElevationRangeMin;
            public float ElevationRangeMax;
            // An elevated-only net (a highway ramp, an elevated rail): the generator floors its
            // whole deck at the two endpoint heights whatever the endpoints' own elevations say.
            public bool RequireElevated;
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

        // How far this machine's surface may sit from the source's before the transmitted elevation
        // is corrected to preserve the ABSOLUTE height instead (see EndElevation). Below it the two
        // worlds agree and the source value is used untouched, which keeps a ground net at exactly
        // elevation 0 - the value that lets the terrain grade to the net rather than the reverse.
        // A ground net's committed Y deviates from the raw pre-build terrain by that grading, so the
        // tolerance has to clear it; real elevation steps and any water crossing are far larger.
        private const float SurfaceAgreementTol = 2.0f;

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

        // Utility endpoints that found their intended local node/edge only after projecting the
        // source's relative underground/overhead elevation onto this machine's surface. These are
        // connections that the old raw-Y-only classifier would have emitted as overlapping free
        // nodes when terrain or water simulation differed between peers.
        private int _rzLocalSurfaceMatches;

        // Endpoints whose elevation had to be corrected because this machine's surface disagreed with
        // the source's, and the largest such disagreement. A non-zero count means the two worlds'
        // terrain or water differ under replicated geometry — the reading that tells a height report
        // apart from a snapping one.
        private int _rzSurfaceCorrections;
        private float _rzSurfaceCorrectionMax;

        // What the current realize cycle worked on, for the slow-cycle report only.
        private int _rzCycleCourses;
        private int _rzCyclePool;

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
    }
}
