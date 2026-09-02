using Game;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Establishes the downloaded world's exact household contracts before the client's first
    /// native RentAdjust pass. The actual post-adjust correction remains in PropertyRentSyncSystem.
    /// </summary>
    public sealed partial class ResidentialOccupancyRentSeedSystem : GameSystemBase
    {
        private ResidentialOccupancySyncSystem _occupancy;

        protected override void OnCreate()
        {
            base.OnCreate();
            _occupancy = World.GetOrCreateSystemManaged<ResidentialOccupancySyncSystem>();
        }

        /// <summary>
        /// RentAdjustSystem's own interval. The seed only has to land before that system's first
        /// pass over a freshly downloaded world, so running it on every frame in between bought
        /// nothing; matching the interval also hands this system RentAdjust's update offset, which
        /// is what actually guarantees the "before" in the registration.
        /// </summary>
        public override int GetUpdateInterval(SystemUpdatePhase phase) =>
            phase == SystemUpdatePhase.GameSimulation
                ? 262144 / (RentAdjustUpdatesPerDay * 16) : 1;

        /// <summary>Matches <c>RentAdjustSystem.kUpdatesPerDay</c>.</summary>
        private const int RentAdjustUpdatesPerDay = 16;

        protected override void OnUpdate()
        {
            using (Diagnostics.SyncProfiler.Measure("Occupancy.RentSeed", Diagnostics.SyncZone.Residential))
            {
                if (_occupancy != null) _occupancy.SeedLoadedWorldHouseholdRents();
            }
        }
    }
}
