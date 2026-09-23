using Game;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>Seeds the downloaded world's household contracts before the first RentAdjust.</summary>
    public sealed partial class ResidentialOccupancyRentSeedSystem : GameSystemBase
    {
        private ResidentialOccupancySyncSystem _occupancy;

        protected override void OnCreate()
        {
            base.OnCreate();
            _occupancy = World.GetOrCreateSystemManaged<ResidentialOccupancySyncSystem>();
        }

        /// <summary>RentAdjustSystem's interval, which also hands this system its update offset.</summary>
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
