using System.Collections.Generic;
using CS2MultiplayerMod.Core.Diagnostics;

namespace CS2MultiplayerMod.Game.Diagnostics
{
    /// <summary>
    /// The gate for every automatic world reload. A claim about the CITY (something named is absent, an
    /// identity contradicts) settles on sight or second sighting; a claim about this machine's PIPELINE
    /// (a timeout) must survive a hold. During a hold the destructive net feeders stand down and the
    /// reporter retries; a success withdraws the report, otherwise it settles. Every outcome is an
    /// ungated event.
    /// </summary>
    public static class ResyncArbiter
    {
        /// <summary>
        /// Long enough for a large native drain and another full retry window; short enough not to play on
        /// a diverged city.
        /// </summary>
        private const long HoldWindowMs = 12000;

        /// <summary>Bound on distinct held reports, so a flapping subsystem cannot grow this.</summary>
        private const int MaxHeld = 32;

        private sealed class Held
        {
            public ResyncReport Report;
            public long HoldUntilMs;
        }

        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Held> Pending = new Dictionary<string, Held>();

        /// <summary>The subsystem whose held reports freeze the net-mutation feeders.</summary>
        private const string NetSubsystem = "net";

        /// <summary>
        /// True while a NET report is held: bulldoze and road replacement stand down so the deciding retry
        /// does not race local mutations. Other reports do not freeze them.
        /// </summary>
        public static bool NetMutationFrozen(long nowMs)
        {
            lock (Gate)
            {
                foreach (KeyValuePair<string, Held> entry in Pending)
                    if (nowMs < entry.Value.HoldUntilMs &&
                        entry.Value.Report.Subsystem == NetSubsystem) return true;
                return false;
            }
        }

        /// <summary>
        /// Weighs one report and returns what the caller must do; never reloads itself.
        /// <paramref name="recovering"/>: a reload is already under way.
        /// </summary>
        public static ResyncVerdict Submit(ResyncReport report, long nowMs, bool recovering)
        {
            if (report == null) return ResyncVerdict.Held;

            if (recovering)
            {
                SyncLog.Event(LogTopic.Resync, "World sync: " + report.Reason +
                    " while a world sync is already running; folded into it (" + report.Subsystem +
                    "/" + report.Subject + ").");
                return ResyncVerdict.AlreadyRecovering;
            }

            bool settle;
            int observations;
            long heldForMs = 0;
            lock (Gate)
            {
                string key = KeyOf(report.Subsystem, report.Reason, report.Subject);
                if (Pending.TryGetValue(key, out Held held))
                {
                    held.Report.Observations++;
                    observations = held.Report.Observations;
                    heldForMs = nowMs - held.Report.FirstSeenMs;
                    // Seen again: a world claim is corroborated; a pipeline claim still needs its hold.
                    settle = SettlesOnRepeat(report.Evidence) || nowMs >= held.HoldUntilMs;
                    if (settle) Pending.Remove(key);
                }
                else
                {
                    report.FirstSeenMs = nowMs;
                    report.Observations = 1;
                    observations = 1;
                    settle = SettlesImmediately(report.Evidence);
                    if (!settle)
                    {
                        if (Pending.Count >= MaxHeld) Evict();
                        Pending[key] = new Held
                        {
                            Report = report,
                            HoldUntilMs = nowMs + HoldWindowMs,
                        };
                    }
                }
            }

            if (settle)
            {
                // The verdict only; the service decides (with its cooldown) whether the reload runs.
                SyncLog.Event(LogTopic.Resync,
                    "World sync: this city and the host's have diverged and cannot be reconciled " +
                    "locally. Reason: " + report.Reason + ".",
                    Decorate(report, observations, heldForMs, settled: true));
                return ResyncVerdict.Settled;
            }

            SyncLog.Event(LogTopic.Resync, "World sync: holding off on a world reload for up to " +
                (HoldWindowMs / 1000) + " s while this is confirmed. Reason: " + report.Reason +
                ".", Decorate(report, observations, heldForMs, settled: false));
            return ResyncVerdict.Held;
        }

        /// <summary>
        /// A held fault cleared. The only way a held report ends without a reload; a subsystem that drops
        /// its work never calls this and its report matures.
        /// </summary>
        public static void Withdraw(string subsystem, string reason, string subject, long nowMs,
            string outcome)
        {
            Held held;
            lock (Gate)
            {
                string key = KeyOf(subsystem, reason, subject);
                if (!Pending.TryGetValue(key, out held)) return;
                Pending.Remove(key);
            }

            List<string> lines = held.Report.Lines();
            lines.Add("held for: " + (nowMs - held.Report.FirstSeenMs) + " ms");
            lines.Add("outcome: " + (outcome ?? "the fault cleared on its own"));
            SyncLog.Event(LogTopic.Resync, "World sync: not needed after all - " +
                held.Report.Reason + " resolved without reloading the world.", lines);
        }

        /// <summary>Reports whose hold elapsed: reload for the first, fold in the rest.</summary>
        public static List<ResyncReport> TakeMatured(long nowMs)
        {
            List<Held> matured = null;
            lock (Gate)
            {
                if (Pending.Count == 0) return null;
                List<string> drop = null;
                foreach (KeyValuePair<string, Held> entry in Pending)
                {
                    if (nowMs < entry.Value.HoldUntilMs) continue;
                    (drop ?? (drop = new List<string>())).Add(entry.Key);
                    (matured ?? (matured = new List<Held>())).Add(entry.Value);
                }
                if (drop != null)
                    for (int i = 0; i < drop.Count; i++) Pending.Remove(drop[i]);
            }

            if (matured == null) return null;
            var reports = new List<ResyncReport>(matured.Count);
            for (int i = 0; i < matured.Count; i++)
            {
                ResyncReport report = matured[i].Report;
                List<string> lines = report.Lines();
                lines.Add("observed: " + report.Observations +
                          (report.Observations == 1 ? " time" : " times"));
                lines.Add("held for: " + (nowMs - report.FirstSeenMs) +
                          " ms with the net feeders standing down");
                lines.Add("verdict: settled - nothing repaired it in that time");
                SyncLog.Event(LogTopic.Resync, "World sync: the hold expired and " + report.Reason +
                    " is still unresolved, so this city has to be replaced by the host's.", lines);
                reports.Add(report);
            }
            return reports;
        }

        /// <summary>Forget everything (a world reload, a session end): the evidence no longer applies.</summary>
        public static void Reset()
        {
            lock (Gate) Pending.Clear();
        }

        private static string KeyOf(string subsystem, string reason, string subject) =>
            (subsystem ?? "sync") + "|" + reason + "|" + (subject ?? reason);

        private static List<string> Decorate(ResyncReport report, int observations, long heldForMs,
            bool settled)
        {
            List<string> lines = report.Lines();
            lines.Add("observed: " + observations + (observations == 1 ? " time" : " times") +
                      (heldForMs > 0 ? ", first seen " + heldForMs + " ms ago" : string.Empty));
            lines.Add(settled
                ? "verdict: settled - this is a real divergence and cannot be repaired locally"
                : "verdict: not settled yet - the net feeders stand down and the edit is retried; " +
                  "the world is reloaded in " + (HoldWindowMs / 1000) + " s unless it resolves");
            return lines;
        }

        /// <summary>Nothing local will supply lost commands, and a contradiction already describes two worlds.</summary>
        private static bool SettlesImmediately(ResyncEvidence evidence) =>
            evidence == ResyncEvidence.Contradiction || evidence == ResyncEvidence.StreamLoss;

        /// <summary>A missing target seen twice is a missing target. A timeout seen twice is not.</summary>
        private static bool SettlesOnRepeat(ResyncEvidence evidence) =>
            evidence != ResyncEvidence.Timeout;

        /// <summary>Drops the report closest to maturing; at worst the reload is delayed.</summary>
        private static void Evict()
        {
            string oldest = null;
            long soonest = long.MaxValue;
            foreach (KeyValuePair<string, Held> entry in Pending)
                if (entry.Value.HoldUntilMs < soonest)
                {
                    soonest = entry.Value.HoldUntilMs;
                    oldest = entry.Key;
                }
            if (oldest != null) Pending.Remove(oldest);
        }
    }
}
