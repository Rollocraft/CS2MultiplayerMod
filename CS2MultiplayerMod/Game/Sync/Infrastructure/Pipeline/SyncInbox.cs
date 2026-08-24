using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading;

namespace CS2MultiplayerMod.Game.Sync.Infrastructure
{
    /// <summary>
    /// Bounded enqueue for the sync systems' incoming-message queues, plus a drain registry so a
    /// world reload can purge every queue at once. The queues fill while gameplay sync is gated
    /// (e.g. during a map load) or when a peer floods; shedding the oldest beyond a cap keeps memory
    /// bounded. Overflow invalidates and clears the whole queued suffix, then requests an explicit
    /// world recovery; silently shedding only its oldest command could apply dependent work without
    /// the building, connector, or original it references.
    /// </summary>
    internal static class SyncInbox
    {
        public const int DefaultCap = 1024;

        /// <summary>Sink for the rare backpressure/drain warnings (set by the mod; also by tests).</summary>
        public static Action<string> LogWarn;

        private static readonly object DrainGate = new object();
        private static readonly List<Action> Drains = new List<Action>();
        private static readonly object ResyncGate = new object();
        private static int _resyncPending;
        private static string _resyncReason;

        public static bool Push<T>(ConcurrentQueue<T> queue, T item, int cap = DefaultCap)
        {
            if (queue == null) throw new ArgumentNullException(nameof(queue));
            if (cap <= 0) throw new ArgumentOutOfRangeException(nameof(cap));
            lock (queue)
            {
                queue.Enqueue(item);
                if (queue.Count <= cap) return true;
                Clear(queue);
            }
            RequestResync("sync inbox overflow");
            Action<string> warn = LogWarn;
            if (warn != null)
                warn("[MP] Sync inbox overflowed; cleared the incomplete command suffix and " +
                     "requested a fresh world sync.");
            return false;
        }

        public static void RequestResync(string reason)
        {
            lock (ResyncGate)
            {
                if (_resyncPending == 0) _resyncReason = reason ?? "sync pipeline recovery";
                Volatile.Write(ref _resyncPending, 1);
            }
        }

        public static bool TryTakeResyncRequest(out string reason)
        {
            reason = null;
            if (Interlocked.Exchange(ref _resyncPending, 0) == 0) return false;
            lock (ResyncGate)
            {
                reason = _resyncReason ?? "sync pipeline recovery";
                _resyncReason = null;
            }
            return true;
        }

        /// <summary>Empty a queue (used by each system's registered drain on a world reload).</summary>
        public static void Clear<T>(ConcurrentQueue<T> queue)
        {
            if (queue == null) return;
            lock (queue)
            {
                T dropped;
                while (queue.TryDequeue(out dropped)) { }
            }
        }

        /// <summary>
        /// Register a callback that clears one system's queue(s). Idempotent by delegate identity, so
        /// a system re-created across a session restart never double-registers.
        /// </summary>
        public static void RegisterDrain(Action drain)
        {
            if (drain == null) return;
            lock (DrainGate)
                if (!Drains.Contains(drain)) Drains.Add(drain);
        }

        public static void UnregisterDrain(Action drain)
        {
            if (drain == null) return;
            lock (DrainGate) Drains.Remove(drain);
        }

        /// <summary>
        /// Run every registered drain once. A throwing drain is caught and warned so the rest still
        /// run - a world reload must fully purge, whatever one system does.
        /// </summary>
        public static void DrainAll()
        {
            Action[] snapshot;
            lock (DrainGate) snapshot = Drains.ToArray();
            for (int i = 0; i < snapshot.Length; i++)
            {
                try { snapshot[i](); }
                catch (Exception ex)
                {
                    Action<string> warn = LogWarn;
                    if (warn != null) warn("[MP] SyncInbox drain threw: " + ex.Message);
                }
            }
        }
    }
}
