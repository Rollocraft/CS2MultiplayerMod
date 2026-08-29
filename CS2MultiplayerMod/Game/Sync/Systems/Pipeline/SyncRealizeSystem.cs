using System;
using System.Collections.Generic;
using Game;
using CS2MultiplayerMod.Game.Sync.Infrastructure;
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
        private DisasterSyncSystem _disasterSync;
        private GrowableSyncSystem _growableSync;

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
            _disasterSync = World.GetOrCreateSystemManaged<DisasterSyncSystem>();
            _growableSync = World.GetOrCreateSystemManaged<GrowableSyncSystem>();
        }

        private bool _wasDeferringTerrain;

        private const int FaultReportThrottleMs = 10000;
        private readonly Dictionary<string, int> _lastFaultTick = new Dictionary<string, int>();
        private int _lastEntityPruneMs;

        /// <summary>
        /// Run one stage in isolation. The stages are ordered but not dependent: letting a
        /// throw escape costs every stage behind it that frame, and a fault that repeats
        /// (a bad remote command, a torn-down prefab) silently strands whole features for
        /// as long as it lasts.
        /// </summary>
        private void Step(string stage, Action work)
        {
            try
            {
                work();
            }
            catch (Exception ex)
            {
                int now = Environment.TickCount;
                int last;
                if (_lastFaultTick.TryGetValue(stage, out last) &&
                    unchecked(now - last) < FaultReportThrottleMs) return;
                _lastFaultTick[stage] = now;

                Mod.log.Warn("[MP] " + stage + " failed this frame and was skipped: " +
                             ex.GetType().Name + ": " + ex.Message);
                CS2MultiplayerMod.Game.Diagnostics.FlightRecorder.NoteException(
                    "realize stage " + stage, ex);
            }
        }

        protected override void OnUpdate()
        {
            int nowTicks = Environment.TickCount;
            if (unchecked(nowTicks - _lastEntityPruneMs) > 5000)
            {
                _lastEntityPruneMs = nowTicks;
                EntityMapTable.PruneDeadEntities(EntityManager);
            }

            // Reset the net pipeline's per-frame state (the one-preview-wipe-per-frame guard) before
            // any feeder runs — DeleteSync/NetReplaceSync may hijack the frame before NetSync does.
            _netSync.BeginRealizeFrame();
            Step("BuildSync.ObserveLocalToolOutput", _buildSync.ObserveLocalToolOutput);
            Step("BuildSync.CaptureLocalObjectApply", _buildSync.CaptureLocalObjectApply);

            // The active net tool has already selected Apply, while ToolOutputSystem has not yet
            // consumed its standing preview. Publish its cached native courses and remember exact
            // split originals now. Object graphs are captured later at the dedicated pre-output
            // hook, directly from their standing definitions and only on the Apply frame.
            Step("NetSync.CaptureLocalNetApply", _netSync.CaptureLocalNetApply);

            // Hold NEW net/object realizes while remote terrain edits are backlogged: a course or
            // object drawn right after a terraform stroke assumes the sender's post-edit surface, and
            // realizing it against this machine's not-yet-graded terrain buries/floats it and misses
            // every height-gated snap. Terrain drains within frames (its capture rate is far below
            // the apply budget), so the hold is frames long. In-flight net commits still finish;
            // local click-replays are exempt (their Y was measured here).
            Step("TerrainSync.CompletePendingHeightReadback", _terrainSync.CompletePendingHeightReadback);
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

            Step("BuildSync", _buildSync.RealizePending);
            // DeleteSync BEFORE NetSync: a remote bulldoze applied this frame tags its edge Deleted,
            // and NetSync's split-target query excludes Deleted edges — so NetSync never resolves a
            // split onto an edge that is being removed this same frame (a stale-reference crash in
            // ApplyNetSystem). NetSync's own commit (flipping applyMode) is independent of delete order.
            Step("DeleteSync", _deleteSync.RealizePending);
            // Road-type replacements also drive NetSync's single ApplyTool commit slot, so run after
            // DeleteSync and before NetSync's build: a delete armed this frame makes replace defer
            // (IsCommitBusy), and an armed replace makes NetSync's build defer — only one net batch
            // enters any one ApplyTool pass, never a build+replace of the same edge together.
            if (!deferTerrain) Step("NetReplaceSync", _netReplaceSync.RealizePending);
            Step("NetSync", _netSync.RealizePending);
            bool deferNetworkDependents = deferTerrain || _netSync.HasPlacementBacklog;
            if (!deferNetworkDependents) Step("ZoneSync", _zoneSync.RealizePending);
            Step("TerrainSync", _terrainSync.RealizePending);
            // After ZoneSync and behind the same network gate: a zoned building is grown on a lot
            // that a road and its zoning produced, so realizing one before those arrive would put
            // it on ground the receiver does not yet consider buildable.
            _growableSync.DeferForTerrain = deferTerrain;
            if (!deferNetworkDependents) Step("GrowableSync", _growableSync.RealizePending);
            Step("UpgradeSync", _upgradeSync.RealizePending);
            Step("MoveSync", _moveSync.RealizePending);
            if (!deferNetworkDependents) Step("NetUpgradeSync", _netUpgradeSync.RealizePending);
            Step("AreaSync", _areaSync.RealizePending);
            Step("RouteSync.FinalizePending", _routeSync.FinalizePending);
            if (!deferNetworkDependents) Step("RouteSync", _routeSync.RealizePending);
            Step("TilePurchaseSync", _tileSync.RealizePending);
            // Disaster events are plain simulation entities - no definitions, no terrain
            // dependency - but they must still be created here: the game's event initialization
            // runs later this frame and only ever looks at freshly Created events.
            Step("DisasterSync", _disasterSync.RealizePending);
        }
    }
}
