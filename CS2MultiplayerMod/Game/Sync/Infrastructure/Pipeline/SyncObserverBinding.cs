using System;
using CS2MultiplayerMod.Core.Session;

namespace CS2MultiplayerMod.Game.Sync.Infrastructure
{
    /// <summary>
    /// A sync system's session attachment as a matched pair: the observer feeding its inbox and the
    /// drain emptying it on reload.
    /// </summary>
    internal static class SyncObserverBinding
    {
        /// <summary>
        /// Attaches the observer (skipped without a service) and registers the drain either way.
        /// </summary>
        public static T Bind<T>(Func<T> create, Action drain = null) where T : class, ISessionObserver
        {
            T observer = null;
            if (Mod.Service != null)
            {
                observer = create();
                Mod.Service.Session.AddObserver(observer);
            }
            if (drain != null) SyncInbox.RegisterDrain(drain);
            return observer;
        }

        /// <summary>Drain first, so nothing drains a queue its observer is still filling.</summary>
        public static void Unbind(ISessionObserver observer, Action drain = null)
        {
            if (drain != null) SyncInbox.UnregisterDrain(drain);
            if (observer != null && Mod.Service != null)
                Mod.Service.Session.RemoveObserver(observer);
        }
    }
}
