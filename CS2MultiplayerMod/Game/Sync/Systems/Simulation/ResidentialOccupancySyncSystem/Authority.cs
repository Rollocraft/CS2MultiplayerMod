using System;
using System.Collections.Generic;
using CS2MultiplayerMod.Core.Session;
using Unity.Entities;

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
        private static readonly Type[] ClientSuppressedSystems =
        {
            typeof(global::Game.Simulation.HouseholdSpawnSystem),
            typeof(global::Game.Simulation.HouseholdFindPropertySystem),
            typeof(global::Game.Simulation.HouseholdPetSpawnSystem),
            typeof(global::Game.Simulation.BirthSystem),
            typeof(global::Game.Simulation.LeaveHouseholdSystem),
        };

        private readonly Dictionary<Type, bool> _suppressedWasEnabled = new Dictionary<Type, bool>();
        private bool _authorityApplied;

        /// <summary>
        /// Hands residential occupancy to the host. Idempotent, and re-checked every update so a
        /// system the game re-enables on a state change does not quietly start populating houses
        /// this peer's own way again.
        /// </summary>
        private void ApplyLocalAuthority(MultiplayerSession session)
        {
            if (session.Role == SessionRole.Host)
            {
                // A host owns its own simulation. Restore in case this process was a client
                // earlier in its life.
                RestoreLocalAuthority();
                return;
            }

            for (int i = 0; i < ClientSuppressedSystems.Length; i++)
            {
                Type type = ClientSuppressedSystems[i];
                ComponentSystemBase system = World.GetExistingSystemManaged(type);
                if (system == null) continue;
                if (!_suppressedWasEnabled.ContainsKey(type))
                    _suppressedWasEnabled[type] = system.Enabled;
                if (!system.Enabled) continue;
                // If a system that was initially off becomes enabled during the session, remember
                // that latest native intent before holding it again so disconnect restores it on.
                _suppressedWasEnabled[type] = true;
                system.Enabled = false;
                Mod.Verbose("[MP] Occupancy: " + type.Name +
                            " disabled on this client; the host decides who lives where.");
            }

            if (_authorityApplied) return;
            _authorityApplied = true;
            Mod.log.Info("[MP] Occupancy: residential occupancy handed to the host (" +
                         ClientSuppressedSystems.Length + " simulation system(s) held).");
            Diagnostics.FlightRecorder.Note("occupancy authority -> host");
        }

        /// <summary>
        /// Gives the local simulation its population back when the session ends. Without this a
        /// player who leaves a session keeps a city nobody can ever move into again.
        /// </summary>
        private void RestoreLocalAuthority()
        {
            if (_suppressedWasEnabled.Count == 0)
            {
                _authorityApplied = false;
                return;
            }

            foreach (KeyValuePair<Type, bool> pair in _suppressedWasEnabled)
            {
                ComponentSystemBase system = World.GetExistingSystemManaged(pair.Key);
                if (system != null) system.Enabled = pair.Value;
            }
            _suppressedWasEnabled.Clear();
            _authorityApplied = false;
            Mod.log.Info("[MP] Occupancy: residential occupancy returned to the local simulation.");
            Diagnostics.FlightRecorder.Note("occupancy authority -> local");
        }
    }
}
