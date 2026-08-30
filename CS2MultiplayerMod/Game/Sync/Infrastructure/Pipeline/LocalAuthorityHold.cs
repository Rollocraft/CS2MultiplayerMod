using System;
using System.Collections.Generic;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Diagnostics;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Infrastructure
{
    /// <summary>
    /// Holds a set of native simulation systems off on a client, so the host's messages are the
    /// only thing that decides the part of the world those systems own.
    ///
    /// Three sync systems need exactly this - growable buildings, company tenancy and residential
    /// occupancy - and each decision is a per-machine random draw, so a client left running them
    /// grows a city, opens a business or houses a family the host has never seen. Each of the three
    /// had its own copy of the hold, differing only in which systems it names and how it says so in
    /// the log.
    ///
    /// The hold is idempotent and meant to be re-applied every update: a system the game re-enables
    /// on a state change would otherwise quietly start deciding again.
    /// </summary>
    internal sealed class LocalAuthorityHold
    {
        private readonly string _label;
        private readonly string _subject;
        private readonly string _decides;
        private readonly string _topic;
        private readonly Type[] _systems;
        private readonly Dictionary<Type, bool> _wasEnabled = new Dictionary<Type, bool>();
        private bool _applied;

        /// <param name="label">Log prefix identifying the sync system, e.g. "Occupancy".</param>
        /// <param name="subject">What is being handed over, e.g. "residential occupancy".</param>
        /// <param name="decides">
        /// Completes "the host decides ..." in the per-system verbose line, e.g. "who lives where".
        /// </param>
        /// <param name="topic">Flight-recorder topic, e.g. "occupancy authority".</param>
        /// <param name="systems">The native systems a client must not run.</param>
        public LocalAuthorityHold(string label, string subject, string decides, string topic,
            params Type[] systems)
        {
            _label = label;
            _subject = subject;
            _decides = decides;
            _topic = topic;
            _systems = systems;
        }

        /// <summary>The native systems this hold covers.</summary>
        public int Count => _systems.Length;

        /// <summary>
        /// Hands the subject to the host on a client, and restores the local simulation on a host -
        /// in case this process was a client earlier in its life.
        /// </summary>
        public void Apply(World world, MultiplayerSession session)
        {
            if (session.Role == SessionRole.Host)
            {
                Restore(world);
                return;
            }

            for (int i = 0; i < _systems.Length; i++)
            {
                Type type = _systems[i];
                ComponentSystemBase system = world.GetExistingSystemManaged(type);
                if (system == null) continue;
                if (!_wasEnabled.ContainsKey(type)) _wasEnabled[type] = system.Enabled;
                if (!system.Enabled) continue;
                // If a system that was initially off becomes enabled during the session, remember
                // that latest native intent before holding it again so disconnect restores it on.
                _wasEnabled[type] = true;
                system.Enabled = false;
                SyncLog.Detail(LogTopic.Pipeline, _label + ": " + type.Name +
                    " disabled on this client; the host decides " + _decides + ".");
            }

            if (_applied) return;
            _applied = true;
            SyncLog.Detail(LogTopic.Pipeline, _label + ": " + _subject + " handed to the host (" +
                _systems.Length + " simulation system(s) held).");
        }

        /// <summary>
        /// Gives the local simulation its half of the world back when the session ends. Without
        /// this a player who leaves a session keeps a city those systems can never act on again.
        /// </summary>
        public void Restore(World world)
        {
            if (_wasEnabled.Count == 0)
            {
                _applied = false;
                return;
            }

            foreach (KeyValuePair<Type, bool> pair in _wasEnabled)
            {
                ComponentSystemBase system = world.GetExistingSystemManaged(pair.Key);
                if (system != null) system.Enabled = pair.Value;
            }
            _wasEnabled.Clear();
            _applied = false;
            SyncLog.Detail(LogTopic.Pipeline, _label + ": " + _subject +
                " returned to the local simulation.");
        }
    }
}
