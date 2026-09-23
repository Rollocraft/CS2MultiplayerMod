using System.Collections.Generic;
using System.Text;

namespace CS2MultiplayerMod.Game.Diagnostics
{
    /// <summary>What the arbiter decided about a submitted <see cref="ResyncReport"/>.</summary>
    public enum ResyncVerdict
    {
        /// <summary>
        /// Not settled: KEEP the work and retry while the net feeders are frozen. Settles by itself when the
        /// hold elapses unless <see cref="ResyncArbiter.Withdraw"/> is called.
        /// </summary>
        Held = 0,

        /// <summary>Settled: the evidence stands. The world will be reloaded.</summary>
        Settled,

        /// <summary>A reload is in flight: drop the work, the incoming snapshot supersedes it.</summary>
        AlreadyRecovering,
    }

    /// <summary>
    /// What a resync request claims. Only two kinds are statements about the city; the others are about
    /// this machine's pipeline, which is not a reason to reload the world.
    /// </summary>
    public enum ResyncEvidence
    {
        /// <summary>A deadline, budget or drain window expired; says nothing about the world. Needs corroboration.</summary>
        Timeout = 0,

        /// <summary>
        /// Something the source named is absent locally. Needs one corroboration, as other feeders may still
        /// be mutating the world.
        /// </summary>
        MissingTarget,

        /// <summary>
        /// The local world contradicts the source beyond repair (two sources on one entity, duplicate
        /// identity, a stale original in the commit). Settles at once.
        /// </summary>
        Contradiction,

        /// <summary>Commands were lost, shed or refused. Settles at once.</summary>
        StreamLoss,
    }

    /// <summary>
    /// The evidence behind a resync request: operation, endpoint, what stood there, how long the
    /// pipeline was blocked, what was tried. Logged ungated and settled by <see cref="ResyncArbiter"/>.
    /// Build with <see cref="Create"/> and chained <see cref="Fact"/> calls.
    /// </summary>
    public sealed class ResyncReport
    {
        /// <summary>Facts are bounded: a report is a log line, not a heap dump.</summary>
        private const int MaxFacts = 24;
        private const int MaxFactChars = 220;

        private readonly List<string> _facts = new List<string>();

        private ResyncReport(string reason, string subsystem, ResyncEvidence evidence)
        {
            Reason = string.IsNullOrEmpty(reason) ? "sync pipeline recovery" : reason;
            Subsystem = string.IsNullOrEmpty(subsystem) ? "sync" : subsystem;
            Evidence = evidence;
            Subject = Reason;
        }

        /// <summary>The short phrase this request has always carried. Still the grep key.</summary>
        public string Reason { get; private set; }

        /// <summary>Which sync domain raised it: net, object, route, growable, area, stream.</summary>
        public string Subsystem { get; private set; }

        public ResyncEvidence Evidence { get; private set; }

        /// <summary>
        /// Stable across repeats (usually the operation identity): same subject and reason is the same
        /// fault seen twice.
        /// </summary>
        public string Subject { get; private set; }

        /// <summary>What was tried before asking for a reload ("retried for 10 s"), printed on its own line.</summary>
        public string Attempted { get; private set; }

        /// <summary>Set by the arbiter when the report is first submitted.</summary>
        public long FirstSeenMs { get; internal set; }

        /// <summary>How many times this exact fault has been submitted, including the first.</summary>
        public int Observations { get; internal set; }

        public static ResyncReport Create(string reason, string subsystem, ResyncEvidence evidence) =>
            new ResyncReport(reason, subsystem, evidence);

        /// <summary>A bare legacy request: unclassified, and therefore never settled on sight.</summary>
        public static ResyncReport FromReason(string reason) =>
            new ResyncReport(reason, "sync", ResyncEvidence.Timeout);

        public ResyncReport About(string subject)
        {
            if (!string.IsNullOrEmpty(subject)) Subject = subject;
            return this;
        }

        public ResyncReport Tried(string attempted)
        {
            Attempted = attempted;
            return this;
        }

        /// <summary>Record one named observation. Silently ignored past the cap.</summary>
        public ResyncReport Fact(string name, string value)
        {
            if (string.IsNullOrEmpty(name) || _facts.Count >= MaxFacts) return this;
            string text = name + ": " + (value ?? "(none)");
            if (text.Length > MaxFactChars) text = text.Substring(0, MaxFactChars) + "…";
            _facts.Add(text);
            return this;
        }

        public ResyncReport Fact(string name, long value) =>
            Fact(name, value.ToString(System.Globalization.CultureInfo.InvariantCulture));

        public ResyncReport Fact(string name, bool value) => Fact(name, value ? "yes" : "no");

        /// <summary>The report as log lines, in sentences for players pasting logs.</summary>
        public List<string> Lines()
        {
            var lines = new List<string>(_facts.Count + 4)
            {
                "what happened: " + Reason,
                "where: " + Subsystem + " sync, " + Subject,
                "evidence: " + Describe(Evidence)
            };
            if (!string.IsNullOrEmpty(Attempted)) lines.Add("already tried: " + Attempted);
            lines.AddRange(_facts);
            return lines;
        }

        /// <summary>One-line form for the flight log and the chat/system feed.</summary>
        public string Summary()
        {
            var text = new StringBuilder(Reason);
            text.Append(" [").Append(Subsystem).Append('/').Append(Subject).Append(']');
            for (int i = 0; i < _facts.Count; i++) text.Append(' ').Append(_facts[i]);
            return text.ToString();
        }

        private static string Describe(ResyncEvidence evidence)
        {
            switch (evidence)
            {
                case ResyncEvidence.MissingTarget:
                    return "something the other player's edit named is not present here";
                case ResyncEvidence.Contradiction:
                    return "this world contradicts the edit and no amount of waiting repairs it";
                case ResyncEvidence.StreamLoss:
                    return "part of the command stream was lost before it could be applied";
                default:
                    return "a deadline expired - this may be a local stall rather than a divergence";
            }
        }
    }
}
