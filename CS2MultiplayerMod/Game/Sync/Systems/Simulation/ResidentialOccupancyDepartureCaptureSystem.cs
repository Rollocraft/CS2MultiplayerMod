using Game;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Just before the move-away executor: samples departures on a host, strips local lifecycle
    /// decisions on a client. State lives in ResidentialOccupancySyncSystem.
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
        /// HouseholdMoveAwaySystem's interval; an equal interval inherits its update offset, so both tick on
        /// the same frame.
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
