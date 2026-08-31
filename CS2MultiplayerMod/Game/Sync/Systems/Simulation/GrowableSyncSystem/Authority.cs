using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Sync.Infrastructure;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    public partial class GrowableSyncSystem
    {
        /// <summary>
        /// The simulation systems a client must not run, because each one decides on its own
        /// whether a zoned building exists - and decides it from a per-machine random draw.
        /// Left running, a client grows and demolishes a city the host has never seen.
        ///
        /// BuildingUpkeepSystem is also held. It does not merely collect upkeep: from locally
        /// drifting renters/resources it can choose a random level target or irreversibly abandon
        /// the building, removing its renter and electricity/water components. Host lifecycle and
        /// occupancy messages mirror those decisions without allowing that destructive local race.
        /// </summary>
        private readonly LocalAuthorityHold _authority = new LocalAuthorityHold(
            "GrowableSync", "zoned-building authority", "zoned buildings", "growable authority",
            typeof(global::Game.Simulation.ZoneSpawnSystem),
            typeof(global::Game.Simulation.BuildingUpkeepSystem),
            typeof(global::Game.Simulation.CondemnedBuildingSystem),
            typeof(global::Game.Simulation.DestroyAbandonedSystem),
            typeof(global::Game.Simulation.CollapsedBuildingSystem));

        /// <summary>
        /// Hands the growable lifecycle to the host. Idempotent, and re-checked every frame so a
        /// system the game re-enables on a state change does not quietly start growing again.
        /// </summary>
        private void ApplyLocalAuthority(MultiplayerSession session) =>
            _authority.Apply(World, session);

        /// <summary>
        /// Gives the local simulation its buildings back when the session ends. Without this a
        /// player who leaves a session keeps a city that can never grow again.
        /// </summary>
        private void RestoreLocalAuthority() => _authority.Restore(World);
    }
}
