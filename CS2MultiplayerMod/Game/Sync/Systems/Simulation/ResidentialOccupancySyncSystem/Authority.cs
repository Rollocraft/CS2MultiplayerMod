using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Sync.Infrastructure;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    public partial class ResidentialOccupancySyncSystem
    {
        /// <summary>
        /// Lifecycle systems a client must not run once the host owns households: they create and split
        /// households, choose homes and add pets, and would race the host's next correction. Sickness and
        /// death checks are held because they draw from RandomSeed.Next(). Deliberately left running:
        /// HouseholdBehaviorSystem (shopping and car demand; its move proposals are stripped at the
        /// lifecycle boundary), the move-away/remove systems (they execute host removals), property,
        /// renter, job and wage systems, HealthProblemSystem (local ambulances and hearses) and
        /// AgingSystem (deterministic).
        /// </summary>
        private readonly LocalAuthorityHold _authority = new LocalAuthorityHold(
            "Occupancy", "residential occupancy", "who lives where", "occupancy authority",
            typeof(global::Game.Simulation.HouseholdSpawnSystem),
            typeof(global::Game.Simulation.HouseholdFindPropertySystem),
            typeof(global::Game.Simulation.HouseholdPetSpawnSystem),
            typeof(global::Game.Simulation.BirthSystem),
            typeof(global::Game.Simulation.LeaveHouseholdSystem),
            typeof(global::Game.Simulation.SicknessCheckSystem),
            typeof(global::Game.Simulation.DeathCheckSystem));

        /// <summary>Idempotent; re-checked every update because the game can re-enable held systems.</summary>
        private void ApplyLocalAuthority(MultiplayerSession session)
        {
            // Without simulation sync the host never sends these decisions.
            if (!session.SimulationSyncEnabled)
            {
                RestoreLocalAuthority();
                return;
            }
            _authority.Apply(World, session);
        }

        /// <summary>Gives the local population back when the session ends.</summary>
        private void RestoreLocalAuthority() => _authority.Restore(World);
    }
}
