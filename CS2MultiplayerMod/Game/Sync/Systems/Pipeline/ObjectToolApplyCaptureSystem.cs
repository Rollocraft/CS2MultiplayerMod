using Game;
using CS2MultiplayerMod.Game.Sync.Systems.Net;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Captures committed tool previews immediately before ToolOutputSystem consumes them,
    /// including one-shot object operations and zoning marquee releases.
    /// </summary>
    public partial class ObjectToolApplyCaptureSystem : GameSystemBase
    {
        private BuildSyncSystem _buildSync;
        private NetSyncSystem _netSync;
        private ZoneSyncSystem _zoneSync;

        protected override void OnCreate()
        {
            base.OnCreate();
            _buildSync = World.GetOrCreateSystemManaged<BuildSyncSystem>();
            _netSync = World.GetOrCreateSystemManaged<NetSyncSystem>();
            _zoneSync = World.GetOrCreateSystemManaged<ZoneSyncSystem>();
        }

        protected override void OnUpdate()
        {
            using (Diagnostics.SyncProfiler.Measure("ToolApplyCapture"))
            {
                MultiplayerService service = Mod.Service;
                if (service == null || !service.GameplaySyncReady) return;

                // A world reload can recreate BuildSync independently of this hook.
                if (_buildSync == null)
                    _buildSync = World.GetOrCreateSystemManaged<BuildSyncSystem>();
                if (_netSync == null)
                    _netSync = World.GetOrCreateSystemManaged<NetSyncSystem>();

                // Idempotent retry for a net tool that selected Apply later in the phase.
                _netSync.CaptureLocalNetApply();
                _buildSync.CaptureLocalObjectApplyBeforeToolOutput();
                if (_zoneSync == null) _zoneSync = World.GetOrCreateSystemManaged<ZoneSyncSystem>();
                _zoneSync.CaptureLocalToolApply();
            }
        }
    }
}
