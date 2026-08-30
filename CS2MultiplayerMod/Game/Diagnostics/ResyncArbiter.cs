using System.Collections.Generic;
using CS2MultiplayerMod.Core.Diagnostics;

namespace CS2MultiplayerMod.Game.Diagnostics
{
    /// <summary>What the arbiter decided about a submitted <see cref="ResyncReport"/>.</summary>
    public enum ResyncVerdict
    {
        /// <summary>
        /// Not settled yet. A caller that can keep its work must KEEP it and retry: the mutating
        /// net feeders are frozen for the length of the hold, so the retry runs against a world
        /// that is no longer moving underneath it.
        ///
        /// Held is not "dismissed". Unless something calls <see cref="ResyncArbiter.Withdraw"/> to
        /// say the fault cleared, the report settles by itself when its hold elapses - so a real
        /// divergence still gets repaired, it just gets one honest chance not to be one first.
        /// </summary>
        Held = 0,

        /// <summary>Settled: the evidence stands. The world will be reloaded.</summary>
        Settled,

        /// <summary>
        /// Settled by someone else already - a reload is in flight. The caller drops its work; the
        /// incoming snapshot supersedes it either way.
        /// </summary>
        AlreadyRecovering,
    }

    /// <summary>
    /// The gate every automatic world reload passes through.
    ///
    /// A resync costs both players a save, a tens-of-megabyte transfer and a full world load, and
    /// it does not fix the edit that triggered it - that edit is simply gone afterwards. So it is
    /// worth being sure. The field logs behind this class show two shapes of wrong answer:
    ///
    ///  * A placement was rejected because a road it anchors to was "missing", while this machine's
    ///    own delete feeder had bulldozed that road during the ten seconds the placement spent
    ///    waiting. The divergence was manufactured by the wait, not observed before it.
    ///  * A commit was quarantined because 311 entities had not left their temporary state inside a
    ///    fixed three-second window - a window tuned against batches a sixth of that size.
    ///
    /// Both are timeouts dressed as evidence. So a claim about the CITY (a named thing is absent,
    /// an identity contradicts) is allowed to settle on sight or on its second sighting, while a
    /// claim about this machine's PIPELINE has to survive a hold first. During the hold the feeders
    /// that can only destroy what a pending edit is waiting for stand down, and the subsystem that
    /// raised the report gets to retry against a world that is standing still. If it succeeds it
    /// withdraws the report; if nothing withdraws it, the hold elapses and the reload happens.
    ///
    /// Every outcome - held, withdrawn, settled - is written as an ungated event with the full
    /// report, so the log says why the world was reloaded, or why it nearly was, even when no
    /// diagnostic switch was ever turned on.
    /// </summary>
    public static class ResyncArbiter
    {
        /// <summary>
        /// How long a report is held before it settles on its own. Long enough for a large native
        /// drain to finish and for a deferred operation to see another full retry window against a
        /// frozen world; short enough that a genuinely diverged city is not played on.
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
        /// True while a NET report is being corroborated. The feeders that can only destroy a
        /// pending road operation's target - bulldoze, road replacement - stand down while this is
        /// set, so the retry that decides the verdict is not racing this machine's own mutations.
        ///
        /// Only net reports count. A held route or growable report says nothing about whether a
        /// road is about to be removed, and freezing bulldozing for it would be a pause the player
        /// feels for no reason. Bounded by the hold window either way.
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
        /// Weigh one report. Returns what the caller must do; never reloads anything itself.
        /// <paramref name="recovering"/> is true when a world reload is already under way.
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
                Held held;
                if (Pending.TryGetValue(key, out held))
                {
                    held.Report.Observations++;
                    observations = held.Report.Observations;
                    heldForMs = nowMs - held.Report.FirstSeenMs;
                    // Seen again. A claim about the world is now corroborated. A claim about this
                    // machine's own pipeline is not: failing again one frame later says nothing the
                    // first failure did not, and the hold is what gives the retry a quiet world.
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
                // States the VERDICT, not the action. Whether the reload actually runs is the
                // service's call - it still has a cooldown - and it reports that itself.
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
        /// Tell the arbiter a held fault has cleared - the operation resolved, the graph drained.
        /// Withdrawing is the whole point of holding: it is a world reload that did not have to
        /// happen, and it is logged in the same shape as one that did.
        ///
        /// This is the ONLY way a held report goes away without a reload. A subsystem that drops
        /// its work instead of retrying simply never calls it, and its report matures on schedule.
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

        /// <summary>
        /// Reports whose hold has elapsed with nothing withdrawing them. They are settled: the
        /// caller reloads the world for the first one and folds the rest into it.
        /// </summary>
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

        /// <summary>
        /// A contradiction or a lost command is not a wait-and-see: nothing local will supply the
        /// missing commands, and a contradiction is already a statement about two worlds.
        /// </summary>
        private static bool SettlesImmediately(ResyncEvidence evidence) =>
            evidence == ResyncEvidence.Contradiction || evidence == ResyncEvidence.StreamLoss;

        /// <summary>A missing target seen twice is a missing target. A timeout seen twice is not.</summary>
        private static bool SettlesOnRepeat(ResyncEvidence evidence) =>
            evidence != ResyncEvidence.Timeout;

        /// <summary>
        /// Drop the report closest to maturing. It is the one that has already had its chance, and
        /// dropping it costs at most a delayed reload - the fault will be raised again.
        /// </summary>
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
