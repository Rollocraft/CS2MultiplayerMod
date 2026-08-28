using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Sync.Infrastructure;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    public partial class ResidentialOccupancySyncSystem
    {
        /// <summary>
        /// The lifecycle systems a client must not run once the host owns residential households.
        /// Together these systems create and split households, choose their homes, and add pets.
        /// The host's absolute roster mirrors those decisions, the
        /// household's daily scalar state, and its pet roster, so running the writers locally would
        /// race the next host correction and can turn one move-out into a different family locally.
        ///
        /// Not on this list, deliberately:
        ///
        /// * <c>HouseholdBehaviorSystem</c> stays running. Besides proposing moves, it is the
        ///   native producer for shopping needs and car demand. The every-frame lifecycle boundary
        ///   strips its local move/seeker decisions before their consumers run, while leaving the
        ///   traffic/economy work intact.
        /// * <c>HouseholdMoveAwaySystem</c>, <c>HouseholdAndCitizenRemoveSystem</c>, and
        ///   <c>HouseholdPetRemoveSystem</c> stay running. They execute and clean up removals that
        ///   the host roster requests; they do not choose the authoritative roster.
        /// * <c>PropertyProcessingSystem</c>, <c>PropertyRenterSystem</c>, and the job/wage systems
        ///   stay running. They maintain native renter links, payments, and local employment; this
        ///   authority boundary is limited to household lifecycle and mirrored household state.
        /// * <c>DeathCheckSystem</c> stays running. Death of old age is drawn from the citizen's own
        ///   stored pseudo-random value and their age, both of which the roster keeps identical, so
        ///   it already agrees between peers. The same job also owns illness recovery and marking a
        ///   body for collection, so holding it would break healthcare and deathcare to fix a
        ///   divergence that only affects citizens who are already ill — and that the next roster
        ///   page repairs anyway.
        /// * <c>AgingSystem</c> stays running: it contains no randomness and both peers hold the
        ///   same birthdays.
        /// </summary>
        private readonly LocalAuthorityHold _authority = new LocalAuthorityHold(
            "Occupancy", "residential occupancy", "who lives where", "occupancy authority",
            typeof(global::Game.Simulation.HouseholdSpawnSystem),
            typeof(global::Game.Simulation.HouseholdFindPropertySystem),
            typeof(global::Game.Simulation.HouseholdPetSpawnSystem),
            typeof(global::Game.Simulation.BirthSystem),
            typeof(global::Game.Simulation.LeaveHouseholdSystem));

        /// <summary>
        /// Hands residential occupancy to the host. Idempotent, and re-checked every update so a
        /// system the game re-enables on a state change does not quietly start populating houses
        /// this peer's own way again.
        /// </summary>
        private void ApplyLocalAuthority(MultiplayerSession session) =>
            _authority.Apply(World, session);

        /// <summary>
        /// Gives the local simulation its population back when the session ends. Without this a
        /// player who leaves a session keeps a city nobody can ever move into again.
        /// </summary>
        private void RestoreLocalAuthority() => _authority.Restore(World);
    }
}
