using System;
using System.Collections.Generic;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Diagnostics;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Infrastructure
{
    /// <summary>
    /// Holds native systems off on a client so the host alone decides what they own. Idempotent and
    /// re-applied every update, since the game can re-enable a system.
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

        /// <param name="label">Log prefix, e.g. "Occupancy".</param>
        /// <param name="subject">What is handed over, e.g. "residential occupancy".</param>
        /// <param name="decides">Completes "the host decides ...", e.g. "who lives where".</param>
        /// <param name="topic">Flight-recorder topic.</param>
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

        /// <summary>Holds on a client; restores on a host in case this process was a client before.</summary>
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
                // Enabled natively during the session: restore it enabled on disconnect.
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

        /// <summary>Gives the local simulation back when the session ends.</summary>
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
