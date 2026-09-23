using System;
using System.Collections.Concurrent;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;

namespace CS2MultiplayerMod.Game.Sync.Infrastructure
{
    /// <summary>Funnels commands with the given ids into a sync system's inbox.</summary>
    internal sealed class CommandObserver : SessionObserver
    {
        private readonly ConcurrentQueue<SimulationCommandMessage> _sink;
        private readonly ushort[] _ids;

        /// <summary>Per-command body cap, enforced on the network thread before queueing.</summary>
        public int MaxBodyBytes = int.MaxValue;

        /// <summary>Per-system inbox cap, for systems that spread large bursts over frames.</summary>
        public int QueueCap = SyncInbox.DefaultCap;

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
                    WarnThrottled("Dropping oversized command id " + command.CommandId +
                                  " body=" + command.Body.Length + " > " + MaxBodyBytes + ".");
                    SyncInbox.RequestResync(CS2MultiplayerMod.Game.Diagnostics.ResyncReport
                        .Create("oversized sync command rejected", "stream",
                            CS2MultiplayerMod.Game.Diagnostics.ResyncEvidence.StreamLoss)
                        .About("oversized command")
                        .Tried("nothing - the command exceeded its size cap and was refused at the door"));
                    return;
                }
                SyncInbox.Push(_sink, command, QueueCap, "command " + command.CommandId);
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
