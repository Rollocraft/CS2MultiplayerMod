using System;
using System.Collections.Concurrent;
using CS2MultiplayerMod.Core.Protocol.Messages;
using Game;

namespace CS2MultiplayerMod.Game.Sync.Infrastructure
{
    /// <summary>
    /// A sync system fed by peer commands. Owns the inbox, the session observer that fills it and
    /// the drain that empties it on a world reload. Subclasses call <see cref="ListenFor"/> in OnCreate.
    /// </summary>
    public abstract partial class CommandSyncSystem : GameSystemBase
    {
        protected readonly ConcurrentQueue<SimulationCommandMessage> _incoming =
            new ConcurrentQueue<SimulationCommandMessage>();

        private CommandObserver _observer;
        private Action _drain;

        protected void ListenFor(ushort[] ids, int maxBodyBytes = int.MaxValue,
            int queueCap = SyncInbox.DefaultCap, bool drainOnReload = true)
        {
            _drain = drainOnReload ? DrainQueue : (Action)null;
            _observer = SyncObserverBinding.Bind(
                () => new CommandObserver(_incoming, ids)
                {
                    MaxBodyBytes = maxBodyBytes,
                    QueueCap = queueCap,
                },
                _drain);
        }

        /// <summary>Empties the inbox on a world reload; override to clear retry state as well.</summary>
        protected virtual void DrainQueue() => SyncInbox.Clear(_incoming);

        protected override void OnDestroy()
        {
            SyncObserverBinding.Unbind(_observer, _drain);
            base.OnDestroy();
        }
    }
}
