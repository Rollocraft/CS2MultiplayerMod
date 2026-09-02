using Game;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Completes identity-aware household move-ins after the native property queue has run. Keeping
    /// this as a separate ordering point lets channel 21 install the host rent before the later rent
    /// payment systems, without replacing the native move-in transaction.
    /// </summary>
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
