using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading;
using CS2MultiplayerMod.Game.Diagnostics;

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

        /// <summary>
        /// The synchronous resync gate, installed by the mod (see <see cref="Settle"/>). Left null
        /// in tests, where every request settles - a test has no world to reload and no arbiter to
        /// consult, so the historical "ask and it happens" behaviour is the right default there.
        /// </summary>
        public static Func<ResyncReport, ResyncVerdict> Arbitrate;

        private static readonly object DrainGate = new object();
        private static readonly List<Action> Drains = new List<Action>();
        private static readonly object ResyncGate = new object();
        private static int _resyncPending;
        private static ResyncReport _resyncReport;

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
            // Shedding a queued suffix means commands were lost before they could be applied.
            // Nothing local can supply them again, so this is not a wait-and-see.
            RequestResync(ResyncReport
                .Create("sync inbox overflow", "stream", ResyncEvidence.StreamLoss)
                .About("inbox cap " + cap)
                .Fact("queue cap", cap)
                .Tried("shed the incomplete queued suffix rather than applying dependent work " +
                       "without the command it depends on"));
            Action<string> warn = LogWarn;
            if (warn != null)
                warn("[MP] Sync inbox overflowed; cleared the incomplete command suffix and " +
                     "requested a fresh world sync.");
            return false;
        }

        /// <summary>
        /// Ask for a world reload without evidence. Kept for the call sites that have already let
        /// go of their work and for anything running off the main thread; the request is classified
        /// as an unproven timeout, so the arbiter will corroborate it before any world is reloaded.
        /// </summary>
        public static void RequestResync(string reason)
        {
            RequestResync(ResyncReport.FromReason(reason));
        }

        /// <summary>
        /// Queue an evidence-carrying request for the main thread to weigh. Use this when the caller
        /// cannot keep its work - the queue was shed, the command was malformed, the graph is gone.
        /// A caller that CAN hold its work should use <see cref="Settle"/> instead and act on the
        /// verdict, because a held report is a world reload that never has to happen.
        /// </summary>
        public static void RequestResync(ResyncReport report)
        {
            lock (ResyncGate)
            {
                // First reason wins, as before: the earliest fault is the one that explains the
                // rest, and a later request is usually a consequence of the same divergence.
                if (_resyncPending == 0) _resyncReport = report ?? ResyncReport.FromReason(null);
                Volatile.Write(ref _resyncPending, 1);
            }
        }

        /// <summary>
        /// Put a report to the arbiter now and get its verdict, on the main thread.
        ///
        /// <see cref="ResyncVerdict.Held"/> means the caller must KEEP its work queued and retry:
        /// the fault is not proven, the mutating net feeders have been frozen for the length of the
        /// hold, and a retry that succeeds withdraws the report instead of reloading the world.
        /// </summary>
        public static ResyncVerdict Settle(ResyncReport report)
        {
            if (report == null) return ResyncVerdict.Settled;
            Func<ResyncReport, ResyncVerdict> arbitrate = Arbitrate;
            if (arbitrate == null)
            {
                RequestResync(report);
                return ResyncVerdict.Settled;
            }
            return arbitrate(report);
        }

        public static bool TryTakeResyncRequest(out ResyncReport report)
        {
            report = null;
            if (Interlocked.Exchange(ref _resyncPending, 0) == 0) return false;
            lock (ResyncGate)
            {
                report = _resyncReport ?? ResyncReport.FromReason(null);
                _resyncReport = null;
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
