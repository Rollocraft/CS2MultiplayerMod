using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Sync.Infrastructure;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    public partial class GrowableSyncSystem
    {
        /// <summary>
        /// Systems a client must not run: each decides from a per-machine random draw whether a zoned
        /// building exists. BuildingUpkeepSystem is held too: it can pick a level target or abandon a
        /// building from locally drifting state.
        /// </summary>
        private readonly LocalAuthorityHold _authority = new LocalAuthorityHold(
            "GrowableSync", "zoned-building authority", "zoned buildings", "growable authority",
            typeof(global::Game.Simulation.ZoneSpawnSystem),
            typeof(global::Game.Simulation.BuildingUpkeepSystem),
            typeof(global::Game.Simulation.CondemnedBuildingSystem),
            typeof(global::Game.Simulation.DestroyAbandonedSystem),
            typeof(global::Game.Simulation.CollapsedBuildingSystem));

        /// <summary>Idempotent; re-checked every frame because the game can re-enable held systems.</summary>
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

        /// <summary>Gives the local growth back when the session ends.</summary>
        private void RestoreLocalAuthority() => _authority.Restore(World);
    }
}
