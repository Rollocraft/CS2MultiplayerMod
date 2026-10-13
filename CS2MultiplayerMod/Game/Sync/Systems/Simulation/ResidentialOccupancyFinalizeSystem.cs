using Game;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>Completes move-ins after the native property queue, before rent payment.</summary>
    public sealed partial class ResidentialOccupancyFinalizeSystem : GameSystemBase
    {
        private ResidentialOccupancySyncSystem _occupancy;
        private CompanyStatsSyncSystem _companies;

        protected override void OnCreate()
        {
            base.OnCreate();
            _occupancy = World.GetOrCreateSystemManaged<ResidentialOccupancySyncSystem>();
            _companies = World.GetOrCreateSystemManaged<CompanyStatsSyncSystem>();
        }

        protected override void OnUpdate()
        {
            using (Diagnostics.SyncProfiler.Measure("Occupancy.Finalize", Diagnostics.SyncZone.Residential))
            {
                if (_occupancy == null) return;
                _occupancy.CaptureRenterChanges();
                if (_companies != null) _companies.CaptureTenancyChanges();
                _occupancy.FinalizeMoveIns();
            }
        }
    }
}
