using System;
using CS2MultiplayerMod.Core.Session;

namespace CS2MultiplayerMod.Game.Sync.Infrastructure
{
    /// <summary>
    /// The two ends of a sync system's attachment to the session: an observer that feeds its
    /// incoming queue, and the drain that empties that queue when the world reloads.
    ///
    /// Every sync system wrote both out in full, and the pair has to stay symmetric - an observer
    /// added and never removed keeps a destroyed system fed, and a drain left registered has
    /// SyncInbox calling into one. Naming the two as a matched pair is what keeps them that way:
    ///
    /// <code>
    /// _observer = SyncObserverBinding.Bind(
    ///     () => new CommandObserver(_incoming, ObjectMoveCommand.Id), DrainQueue);
    /// ...
    /// SyncObserverBinding.Unbind(_observer, DrainQueue);
    /// </code>
    /// </summary>
    internal static class SyncObserverBinding
    {
        /// <summary>
        /// Builds the observer and attaches it to the session, then registers the drain. Returns
        /// null without ever calling <paramref name="create"/> when there is no service to attach
        /// to, which is what the systems' own <c>if (Mod.Service != null)</c> guard did; the drain
        /// is registered either way, because a queue can still need emptying.
        /// </summary>
        public static T Bind<T>(Func<T> create, Action drain = null) where T : ISessionObserver
        {
            T observer = default(T);
            if (Mod.Service != null)
            {
                observer = create();
                Mod.Service.Session.AddObserver(observer);
            }
            if (drain != null) SyncInbox.RegisterDrain(drain);
            return observer;
        }

        /// <summary>
        /// Detaches both, in the order the systems used: the drain goes first, so nothing can be
        /// draining a queue whose observer is still filling it.
        /// </summary>
        public static void Unbind(ISessionObserver observer, Action drain = null)
        {
            if (drain != null) SyncInbox.UnregisterDrain(drain);
            if (observer != null && Mod.Service != null)
                Mod.Service.Session.RemoveObserver(observer);
        }
    }
}
