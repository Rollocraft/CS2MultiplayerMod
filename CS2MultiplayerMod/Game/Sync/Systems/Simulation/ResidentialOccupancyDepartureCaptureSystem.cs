using Game;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Owns the native household-lifecycle boundary immediately before the move-away executor.
    /// On a host it samples departures before the executor consumes them. On a client it removes
    /// lifecycle decisions made by local household behaviour while retaining that system's
    /// shopping and vehicle-demand work. The main occupancy system owns all retained state; this
    /// class is only its every-frame ordering point.
    /// </summary>
    public sealed partial class ResidentialOccupancyDepartureCaptureSystem : GameSystemBase
    {
        private ResidentialOccupancySyncSystem _occupancy;

        protected override void OnCreate()
        {
            base.OnCreate();
            _occupancy = World.GetOrCreateSystemManaged<ResidentialOccupancySyncSystem>();
        }

        protected override void OnUpdate()
        {
            using (Diagnostics.SyncProfiler.Measure("Occupancy.Lifecycle", Diagnostics.SyncZone.Residential))
            {
                if (_occupancy != null) _occupancy.ProcessHouseholdLifecycleBoundary();
            }
        }
    }
}
