using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading;
using CS2MultiplayerMod.Game.Diagnostics;

namespace CS2MultiplayerMod.Game.Sync.Infrastructure
{
    /// <summary>
    /// Bounded inbox enqueue plus a drain registry for world reloads. Overflow clears the whole queued
    /// suffix and requests recovery: shedding only the oldest could apply work without its dependency.
    /// </summary>
    internal static class SyncInbox
    {
        public const int DefaultCap = 1024;

        /// <summary>Sink for the rare backpressure/drain warnings (set by the mod; also by tests).</summary>
        public static Action<string> LogWarn;

        /// <summary>The resync arbiter, installed by the mod; null in tests, where every request settles.</summary>
        public static Func<ResyncReport, ResyncVerdict> Arbitrate;

        private static readonly object DrainGate = new object();
        private static readonly List<Action> Drains = new List<Action>();
        private static readonly object ResyncGate = new object();
        private static int _resyncPending;
        private static ResyncReport _resyncReport;

        public static bool Push<T>(ConcurrentQueue<T> queue, T item, int cap = DefaultCap,
            string subject = null)
        {
            if (queue == null) throw new ArgumentNullException(nameof(queue));
            if (cap <= 0) throw new ArgumentOutOfRangeException(nameof(cap));
            lock (queue)
            {
                queue.Enqueue(item);
                if (queue.Count <= cap) return true;
                Clear(queue);
            }
            // Lost commands cannot be supplied again locally.
            RequestResync(ResyncReport
                .Create("sync inbox overflow", "stream", ResyncEvidence.StreamLoss)
                .About(subject ?? "inbox " + typeof(T).Name + " cap " + cap)
                .Fact("queue cap", cap)
                .Tried("shed the incomplete queued suffix rather than applying dependent work " +
                       "without the command it depends on"));
            Action<string> warn = LogWarn;
            if (warn != null)
                warn("Sync inbox " + (subject ?? typeof(T).Name) + " overflowed; cleared the incomplete command suffix and " +
                     "requested a fresh world sync.");
            return false;
        }

        /// <summary>A reload request without evidence, held and corroborated as an unproven timeout.</summary>
        public static void RequestResync(string reason) => RequestResync(ResyncReport.FromReason(reason));

        /// <summary>
        /// Queues an evidence-carrying request, for callers that cannot keep their work; callers that can
        /// should use <see cref="Settle"/>.
        /// </summary>
        public static void RequestResync(ResyncReport report)
        {
            lock (ResyncGate)
            {
                // First reason wins: the earliest fault usually explains the rest.
                if (_resyncPending == 0) _resyncReport = report ?? ResyncReport.FromReason(null);
                Volatile.Write(ref _resyncPending, 1);
            }
        }

        /// <summary>
        /// The arbiter's verdict now. <see cref="ResyncVerdict.Held"/>: keep the work and retry; net feeders
        /// are frozen meanwhile, and a successful retry withdraws the report.
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
                while (queue.TryDequeue(out T dropped)) { }
            }
        }

        /// <summary>Idempotent by delegate identity.</summary>
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

        /// <summary>Runs every drain; one throwing drain does not stop the rest.</summary>
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
                    if (warn != null) warn("Sync inbox drain threw: " + ex.Message);
                }
            }
        }
    }
}
