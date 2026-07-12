using System;
using System.Collections.Generic;
using System.Collections.Concurrent;

namespace CS2MultiplayerMod.Game.Sync.Infrastructure
{
    /// <summary>
    /// Bounded enqueue for the sync systems' incoming-message queues, plus a drain registry so a
    /// world reload can purge every queue at once. The queues fill while gameplay sync is gated
    /// (e.g. during a map load) or when a peer floods; shedding the oldest beyond a cap keeps memory
    /// bounded, and the periodic world resync repairs whatever the shed messages would have applied.
    /// </summary>
    internal static class SyncInbox
    {
        public const int DefaultCap = 1024;

        /// <summary>Sink for the rare backpressure/drain warnings (set by the mod; also by tests).</summary>
        public static Action<string> LogWarn;

        private static readonly object DrainGate = new object();
        private static readonly List<Action> Drains = new List<Action>();

        public static void Push<T>(ConcurrentQueue<T> queue, T item, int cap = DefaultCap)
        {
            queue.Enqueue(item);
            T dropped;
            while (queue.Count > cap && queue.TryDequeue(out dropped)) { }
        }

        /// <summary>Empty a queue (used by each system's registered drain on a world reload).</summary>
        public static void Clear<T>(ConcurrentQueue<T> queue)
        {
            T dropped;
            while (queue.TryDequeue(out dropped)) { }
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
