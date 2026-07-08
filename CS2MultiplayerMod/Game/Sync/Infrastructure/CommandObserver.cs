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

        public CommandObserver(ConcurrentQueue<SimulationCommandMessage> sink, params ushort[] ids)
        {
            _sink = sink;
            _ids = ids;
        }

        public override void OnCommandReceived(SimulationCommandMessage command)
        {
            for (int i = 0; i < _ids.Length; i++)
            {
                if (command.CommandId == _ids[i]) { SyncInbox.Push(_sink, command); return; }
            }
        }
    }
}
