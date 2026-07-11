using System;
using System.Collections.Concurrent;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;

namespace CS2MultiplayerMod.Game.Sync.Infrastructure
{
    /// <summary>
    /// Shared <see cref="SessionObserver"/> that funnels every command matching one of the given
    /// command ids into a sync system's incoming queue. Replaces the near-identical per-system nested
    /// <c>Observer</c> classes - construct one with the id(s) that system handles, e.g.
    /// <c>new CommandObserver(_incoming, ObjectDeleteCommand.Id, NetDeleteCommand.Id)</c>.
    /// Systems with a non-command observer keep their own bespoke observer.
    /// </summary>
    internal sealed class CommandObserver : SessionObserver
    {
        private readonly ConcurrentQueue<SimulationCommandMessage> _sink;
        private readonly ushort[] _ids;

        /// <summary>
        /// Ceiling on a single command body. A batching command (terrain) sets this to its own
        /// encoded cap so a forged oversized body is dropped on the network thread before it ever
        /// reaches the queue or a decoder. Default: unlimited (bodies are already transport-capped).
        /// </summary>
        public int MaxBodyBytes = int.MaxValue;

        // Backpressure warnings are throttled so a flood can't itself spam the log.
        private const int WarnThrottleMs = 5000;
        private int _lastWarnTick;
        private bool _warnedOnce;

        public CommandObserver(ConcurrentQueue<SimulationCommandMessage> sink, params ushort[] ids)
        {
            _sink = sink;
            _ids = ids;
        }

        public override void OnCommandReceived(SimulationCommandMessage command)
        {
            for (int i = 0; i < _ids.Length; i++)
            {
                if (command.CommandId != _ids[i]) continue;
                if (command.Body != null && command.Body.Length > MaxBodyBytes)
                {
                    WarnThrottled("[MP] Dropping oversized command id " + command.CommandId +
                                  " body=" + command.Body.Length + " > " + MaxBodyBytes + ".");
                    return;
                }
                SyncInbox.Push(_sink, command);
                return;
            }
        }

        private void WarnThrottled(string message)
        {
            Action<string> warn = SyncInbox.LogWarn;
            if (warn == null) return;
            int now = Environment.TickCount;
            if (_warnedOnce && (now - _lastWarnTick) < WarnThrottleMs) return;
            _warnedOnce = true;
            _lastWarnTick = now;
            warn(message);
        }
    }
}
