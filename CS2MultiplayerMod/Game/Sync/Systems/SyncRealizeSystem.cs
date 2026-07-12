using Game;

using CS2MultiplayerMod.Game.Sync.Systems.Net;
namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Runs remote-command realization during ToolUpdate - the only phase where definitions
    /// spawn into built entities. Later creation (e.g. at ModificationEnd) drops at Cleanup.
    /// </summary>
    public partial class SyncRealizeSystem : GameSystemBase
    {
        private BuildSyncSystem _buildSync;
        private NetSyncSystem _netSync;
        private DeleteSyncSystem _deleteSync;
        private NetReplaceSyncSystem _netReplaceSync;
        private ZoneSyncSystem _zoneSync;
        private TerrainSyncSystem _terrainSync;
        private UpgradeSyncSystem _upgradeSync;
        private MoveSyncSystem _moveSync;
        private NetUpgradeSyncSystem _netUpgradeSync;
        private AreaSyncSystem _areaSync;
        private RouteSyncSystem _routeSync;
        private TilePurchaseSyncSystem _tileSync;

        protected override void OnCreate()
        {
            base.OnCreate();
            _buildSync = World.GetOrCreateSystemManaged<BuildSyncSystem>();
            _netSync = World.GetOrCreateSystemManaged<NetSyncSystem>();
            _deleteSync = World.GetOrCreateSystemManaged<DeleteSyncSystem>();
            _netReplaceSync = World.GetOrCreateSystemManaged<NetReplaceSyncSystem>();
            _zoneSync = World.GetOrCreateSystemManaged<ZoneSyncSystem>();
            _terrainSync = World.GetOrCreateSystemManaged<TerrainSyncSystem>();
            _upgradeSync = World.GetOrCreateSystemManaged<UpgradeSyncSystem>();
            _moveSync = World.GetOrCreateSystemManaged<MoveSyncSystem>();
            _netUpgradeSync = World.GetOrCreateSystemManaged<NetUpgradeSyncSystem>();
            _areaSync = World.GetOrCreateSystemManaged<AreaSyncSystem>();
            _routeSync = World.GetOrCreateSystemManaged<RouteSyncSystem>();
            _tileSync = World.GetOrCreateSystemManaged<TilePurchaseSyncSystem>();
        }

        private bool _wasDeferringTerrain;

        protected override void OnUpdate()
        {
            // Reset the net pipeline's per-frame state (the one-preview-wipe-per-frame guard) before
            // any feeder runs — DeleteSync/NetReplaceSync may hijack the frame before NetSync does.
            _netSync.BeginRealizeFrame();
            _buildSync.ObserveLocalToolOutput();

            // The active net tool has already selected Apply, while ToolOutputSystem has not yet
            // consumed its standing preview. Publish the native definition cached after last
            // frame's tool-output barrier and remember its exact split originals now.
            _netSync.CaptureLocalNetApply();

            // Hold NEW net/object realizes while remote terrain edits are backlogged: a course or
            // object drawn right after a terraform stroke assumes the sender's post-edit surface, and
            // realizing it against this machine's not-yet-graded terrain buries/floats it and misses
            // every height-gated snap. Terrain drains within frames (its capture rate is far below
            // the apply budget), so the hold is frames long. In-flight net commits still finish;
            // local click-replays are exempt (their Y was measured here).
            _terrainSync.CompletePendingHeightReadback();
            bool deferTerrain = _terrainSync.HasBacklog();
            _netSync.DeferForTerrain = deferTerrain;
            _buildSync.DeferForTerrain = deferTerrain;
            _moveSync.DeferForTerrain = deferTerrain;
            _deleteSync.DeferNetForTerrain = deferTerrain;
            if (deferTerrain != _wasDeferringTerrain)
            {
                _wasDeferringTerrain = deferTerrain;
                CS2MultiplayerMod.Game.Diagnostics.FlightRecorder.Note(deferTerrain
                    ? "net/build realize deferred (terrain backlog)"
                    : "terrain drained; net/build realize resumed");
            }

            _buildSync.RealizePending();
            // DeleteSync BEFORE NetSync: a remote bulldoze applied this frame tags its edge Deleted,
            // and NetSync's split-target query excludes Deleted edges — so NetSync never resolves a
            // split onto an edge that is being removed this same frame (a stale-reference crash in
            // ApplyNetSystem). NetSync's own commit (flipping applyMode) is independent of delete order.
            _deleteSync.RealizePending();
            // Road-type replacements also drive NetSync's single ApplyTool commit slot, so run after
            // DeleteSync and before NetSync's build: a delete armed this frame makes replace defer
            // (IsCommitBusy), and an armed replace makes NetSync's build defer — only one net batch
            // enters any one ApplyTool pass, never a build+replace of the same edge together.
            if (!deferTerrain) _netReplaceSync.RealizePending();
            _netSync.RealizePending();
            bool deferNetworkDependents = deferTerrain || _netSync.HasPlacementBacklog;
            if (!deferNetworkDependents) _zoneSync.RealizePending();
            _terrainSync.RealizePending();
            _upgradeSync.RealizePending();
            _moveSync.RealizePending();
            if (!deferNetworkDependents) _netUpgradeSync.RealizePending();
            _areaSync.RealizePending();
            if (!deferNetworkDependents) _routeSync.RealizePending();
            _tileSync.RealizePending();
        }
    }
}
