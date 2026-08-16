using System.Collections.Generic;
using Unity.Entities;
using CS2MultiplayerMod.Core.Session;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    public partial class GrowableSyncSystem
    {
        /// <summary>
        /// The simulation systems a client must not run, because each one decides on its own
        /// whether a zoned building exists - and decides it from a per-machine random draw.
        /// Left running, a client grows and demolishes a city the host has never seen.
        ///
        /// Deliberately limited to the systems that create or destroy a building outright. Level
        /// changes are left running and corrected instead (see CaptureLevelChanges): the system
        /// that makes them also collects upkeep, and taking that away would change the client's
        /// economy for a difference the host's command already overwrites.
        /// </summary>
        private static readonly System.Type[] ClientSuppressedSystems =
        {
            typeof(global::Game.Simulation.ZoneSpawnSystem),
            typeof(global::Game.Simulation.CondemnedBuildingSystem),
            typeof(global::Game.Simulation.DestroyAbandonedSystem),
            typeof(global::Game.Simulation.CollapsedBuildingSystem),
        };

        private readonly Dictionary<System.Type, bool> _suppressedWasEnabled =
            new Dictionary<System.Type, bool>();
        private bool _authorityApplied;

        /// <summary>
        /// Hands the growable lifecycle to the host. Idempotent, and re-checked every frame so a
        /// system the game re-enables on a state change does not quietly start growing again.
        /// </summary>
        private void ApplyLocalAuthority(MultiplayerSession session)
        {
            if (session.Role == SessionRole.Host)
            {
                // A host owns its own simulation; nothing to take away. Restore in case this
                // process was a client earlier in its life.
                RestoreLocalAuthority();
                return;
            }

            for (int i = 0; i < ClientSuppressedSystems.Length; i++)
            {
                System.Type type = ClientSuppressedSystems[i];
                ComponentSystemBase system = World.GetExistingSystemManaged(type);
                if (system == null) continue;

                if (!_suppressedWasEnabled.ContainsKey(type))
                    _suppressedWasEnabled[type] = system.Enabled;
                if (!system.Enabled) continue;

                system.Enabled = false;
                Mod.Verbose("[MP] GrowableSync: " + type.Name +
                            " disabled on this client; the host decides zoned buildings.");
            }

            if (_authorityApplied) return;
            _authorityApplied = true;
            Mod.log.Info("[MP] GrowableSync: zoned-building authority handed to the host (" +
                         ClientSuppressedSystems.Length + " simulation system(s) held).");
            Diagnostics.FlightRecorder.Note("growable authority -> host");
        }

        /// <summary>
        /// Gives the local simulation its buildings back when the session ends. Without this a
        /// player who leaves a session keeps a city that can never grow again.
        /// </summary>
        private void RestoreLocalAuthority()
        {
            if (_suppressedWasEnabled.Count == 0)
            {
                _authorityApplied = false;
                return;
            }

            foreach (KeyValuePair<System.Type, bool> pair in _suppressedWasEnabled)
            {
                ComponentSystemBase system = World.GetExistingSystemManaged(pair.Key);
                if (system != null) system.Enabled = pair.Value;
            }
            _suppressedWasEnabled.Clear();
            _authorityApplied = false;
            Mod.log.Info("[MP] GrowableSync: zoned-building authority returned to the local simulation.");
            Diagnostics.FlightRecorder.Note("growable authority -> local");
        }
    }
}
