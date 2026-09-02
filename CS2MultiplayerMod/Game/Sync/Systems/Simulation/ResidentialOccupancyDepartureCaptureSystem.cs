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

        /// <summary>
        /// HouseholdMoveAwaySystem, the executor this boundary sits in front of, updates every 16
        /// frames. At full rate this walked every departing household 15 more times for a queue
        /// nothing could act on yet, which is what turned a 10 ms/30 s scope into 3,000 ms once a
        /// city started shedding thousands of families at once. Matching the interval also makes
        /// the ordering exact rather than incidental: a system whose interval equals the interval
        /// of the system it is registered against inherits that system's update offset, so the two
        /// always tick on the same frame.
        /// </summary>
        public override int GetUpdateInterval(SystemUpdatePhase phase) =>
            phase == SystemUpdatePhase.GameSimulation ? 16 : 1;

        protected override void OnUpdate()
        {
            using (Diagnostics.SyncProfiler.Measure("Occupancy.Lifecycle", Diagnostics.SyncZone.Residential))
            {
                if (_occupancy != null) _occupancy.ProcessHouseholdLifecycleBoundary();
            }
        }
    }
}
