using System;
using System.Collections.Generic;
using Game;

using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Game.Diagnostics;
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
        private TerrainSyncSystem _terrainSync;
        private IRealizeStage[] _netStages;
        private IRealizeStage[] _dependentStages;

        private bool _wasDeferringTerrain;
        private bool _wasHoldingNetMutations;

        private const int FaultReportThrottleMs = 10000;
        private readonly Dictionary<string, int> _lastFaultTick = new Dictionary<string, int>();

        protected override void OnCreate()
        {
            base.OnCreate();
            _buildSync = World.GetOrCreateSystemManaged<BuildSyncSystem>();
            _netSync = World.GetOrCreateSystemManaged<NetSyncSystem>();
            _terrainSync = World.GetOrCreateSystemManaged<TerrainSyncSystem>();

            // One net batch per ApplyTool pass. Deletes first: NetSync's split-target query skips
            // Deleted edges, so it never splits an edge removed this frame (stale-edge crash in
            // ApplyNetSystem). Replace next: an armed delete defers it, an armed replace defers the build.
            _netStages = new IRealizeStage[]
            {
                World.GetOrCreateSystemManaged<DeleteSyncSystem>(),
                World.GetOrCreateSystemManaged<NetReplaceSyncSystem>(),
                _netSync,
            };

            _dependentStages = new IRealizeStage[]
            {
                World.GetOrCreateSystemManaged<ZoneSyncSystem>(),
                _terrainSync,
                World.GetOrCreateSystemManaged<GrowableSyncSystem>(), // grows on lots zoning produced
                World.GetOrCreateSystemManaged<UpgradeSyncSystem>(),
                World.GetOrCreateSystemManaged<MoveSyncSystem>(),
                World.GetOrCreateSystemManaged<NetUpgradeSyncSystem>(),
                World.GetOrCreateSystemManaged<AreaSyncSystem>(),
                World.GetOrCreateSystemManaged<RouteSyncSystem>(),
                World.GetOrCreateSystemManaged<TilePurchaseSyncSystem>(),
                // Event initialization later this frame only looks at freshly Created events.
                World.GetOrCreateSystemManaged<DisasterSyncSystem>(),
                // Last: mod state is stored against roads and buildings created by the stages above.
                World.GetOrCreateSystemManaged<Mods.ModStateSyncSystem>(),
            };
        }

        protected override void OnUpdate()
        {
            using (Diagnostics.SyncProfiler.Measure("Realize"))
            {
                // Before any feeder: DeleteSync/NetReplaceSync may hijack the frame before NetSync does.
                _netSync.BeginRealizeFrame();
                Step("BuildSync.ObserveLocalToolOutput", _buildSync.ObserveLocalToolOutput);
                Step("BuildSync.CaptureLocalObjectApply", _buildSync.CaptureLocalObjectApply);
                // The net tool has selected Apply but ToolOutputSystem has not consumed its preview yet.
                Step("NetSync.CaptureLocalNetApply", _netSync.CaptureLocalNetApply);
                Step("TerrainSync.CompletePendingHeightReadback", _terrainSync.CompletePendingHeightReadback);

                bool terrain = _terrainSync.HasBacklog();
                RealizeGate.TerrainBacklog = terrain;
                TraceChange(ref _wasDeferringTerrain, terrain,
                    "net/build realize deferred (terrain backlog)", "terrain drained; net/build realize resumed");

                // BuildSync attaches to roads NetSync has not realized yet, so it needs this frame's value.
                RealizeGate.WorldBuildingHeld = terrain || _netSync.HasPlacementBacklog;
                Realize(_buildSync);

                // A placement still waiting on its road must not be overtaken by a bulldoze or
                // replace that removes it. Bounded by the placement's retry window, so no deadlock.
                long nowMs = Mod.Service != null ? Mod.Service.NowMs : 0L;
                bool netHeld = _netSync.HasStalledNativeOperation(nowMs) || ResyncArbiter.NetMutationFrozen(nowMs);
                RealizeGate.NetMutationHeld = netHeld;
                TraceChange(ref _wasHoldingNetMutations, netHeld,
                    "net delete/replace held behind a stalled placement", "net delete/replace resumed");
                Realize(_netStages);

                RealizeGate.WorldBuildingHeld = terrain || _netSync.HasPlacementBacklog;
                Realize(_dependentStages);
            }
        }

        private static void TraceChange(ref bool was, bool now, string on, string off)
        {
            if (now == was) return;
            was = now;
            SyncLog.Trace(LogTopic.Pipeline, now ? on : off);
        }

        private void Realize(IRealizeStage[] stages)
        {
            for (int i = 0; i < stages.Length; i++) Realize(stages[i]);
        }

        // Isolated: a repeating fault in one stage must not strand every stage behind it.
        private void Realize(IRealizeStage stage)
        {
            try { stage.RealizePending(); }
            catch (Exception ex) { Fault(stage.GetType().Name, ex); }
        }

        private void Step(string stage, Action work)
        {
            try { work(); }
            catch (Exception ex) { Fault(stage, ex); }
        }

        private void Fault(string stage, Exception ex)
        {
            int now = Environment.TickCount;
            if (_lastFaultTick.TryGetValue(stage, out int last) &&
                unchecked(now - last) < FaultReportThrottleMs) return;
            _lastFaultTick[stage] = now;
            SyncLog.Error(LogTopic.Pipeline, "Realize stage '" + stage + "' failed this frame and was skipped.", ex);
        }
    }
}
