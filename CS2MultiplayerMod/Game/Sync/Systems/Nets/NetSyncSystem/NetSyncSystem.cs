using System.Collections.Concurrent;
using System.Collections.Generic;
using Game;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Unity.Entities;
using CS2MultiplayerMod.Core.Protocol.Messages;

using CS2MultiplayerMod.Game.Sync.Infrastructure;
using CS2MultiplayerMod.Game.Sync.Commands;
namespace CS2MultiplayerMod.Game.Sync.Systems.Net
{
    /// <summary>
    /// Replicates roads and other nets both ways. The receiver rebuilds a segment through a
    /// <see cref="CreationDefinition"/>/<see cref="NetCourse"/> so the game lays the real nodes and
    /// edges. Local tool definitions are captured before their endpoint, split and elevation intent is
    /// reduced to geometry. Files: Apply* commit and drain, Capture detection, Realize* the batch
    /// builder, Intent* the local gesture, MixedOperation* place/delete/replace, Course construction.
    /// </summary>
    public partial class NetSyncSystem : GameSystemBase, IRealizeStage
    {
        private const int NetInboxCap = 4096;
        // Mixed envelopes are large; bound the shared queue's byte footprint.
        private const int MixedNetInboxAdmissionCap = 32;
        private readonly ConcurrentQueue<SimulationCommandMessage> _incoming =
            new ConcurrentQueue<SimulationCommandMessage>();
        // Deferred commands stay ahead of the concurrent inbox. Simulation thread only.
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

        // Free-height endpoints are solved against this machine's surface (see EndElevation).
        private global::Game.Simulation.TerrainSystem _terrainSystem;
        private global::Game.Simulation.WaterSystem _waterSystem;

        // Per-prefab facts used for every endpoint; prefab entities are stable for the session.
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
            // Elevated-only nets: the deck floors at the endpoint heights regardless of elevation.
            public bool RequireElevated;
        }
        private readonly Dictionary<Entity, NetPrefabInfo> _netInfoCache = new Dictionary<Entity, NetPrefabInfo>();

        // Endpoint-to-node snap (XZ). Must be >= EdgeSnapDistance: a split puts the new node on the
        // centreline up to that far from the tap, and a smaller value leaves a gap where the next course
        // can neither reuse that node nor re-split, landing disconnected.
        private const float NodeSnapDistance = 2.0f;

        // Distance from a centreline, away from the ends, that counts as a mid-span tap (a split).
        private const float EdgeSnapDistance = 2.0f;
        private const float MinSplitOffset = 2.0f;

        // Height beyond this is another level: a bridge over a road crosses it, it does not connect.
        private const float VerticalSnapTol = 3.0f;

        // Only these layers may connect to a building's owned sub-net; roads never touch driveways.
        private const Layer UtilityConnectLayers = Layer.PowerlineLow | Layer.PowerlineHigh |
            Layer.WaterPipe | Layer.SewagePipe | Layer.StormwaterPipe | Layer.ResourceLine;

        // Surface disagreement above which the absolute height is preserved (see EndElevation). Below it a
        // ground net keeps elevation 0 so terrain grades to it; must clear that grading.
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

        // Realize counters per endpoint outcome: reused, merged in batch, split, free.
        private int _rzSegments, _rzSnapEnds, _rzMergeEnds, _rzMidEnds, _rzFreeEnds;

        // Utility endpoints matched only after projecting onto the local surface.
        private int _rzLocalSurfaceMatches;

        // Endpoints corrected for surface disagreement: terrain or water differs under the geometry.
        private int _rzSurfaceCorrections;
        private float _rzSurfaceCorrectionMax;

        // What the current realize cycle worked on, for the slow-cycle report only.
        private int _rzCycleCourses;
        private int _rzCyclePool;

        // Peak edge lifecycle tags per window; an in-place split reuse shows as Updated.
        private int _peakCreated, _peakUpdated, _peakDeleted;

        // Created split halves dropped at capture.
        private int _capFilteredHalves;

        // Pieces of a span whose delete is sent this frame; held one frame so the delete lands first.
        private readonly List<NetPlacementCommand> _deferredSpanPieces = new List<NetPlacementCommand>();
    }
}
