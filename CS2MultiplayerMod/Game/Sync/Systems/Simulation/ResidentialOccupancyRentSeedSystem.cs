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

        protected override void OnUpdate()
        {
            using (Diagnostics.SyncProfiler.Measure("Occupancy.RentSeed", Diagnostics.SyncZone.Residential))
            {
                if (_occupancy != null) _occupancy.SeedLoadedWorldHouseholdRents();
            }
        }
    }
}
