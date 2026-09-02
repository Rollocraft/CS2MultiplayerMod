using System.Collections.Generic;
using System.Text;

namespace CS2MultiplayerMod.Game.Diagnostics
{
    /// <summary>
    /// What a resync request is actually claiming about the world.
    ///
    /// The distinction matters because only two of these are statements about the CITY. The other
    /// two are statements about this machine's pipeline, and a pipeline that fell behind is not a
    /// reason to throw away tens of megabytes of world and freeze both players for half a minute.
    /// Field logs show that difference costing real sessions: an operation was rejected for a
    /// "missing" road that the mod's own delete feeder had removed while the placement waited.
    /// </summary>
    public enum ResyncEvidence
    {
        /// <summary>
        /// A deadline, retry budget or drain window expired. Says nothing about the world - the
        /// pipeline may simply have been blocked by something unrelated for the whole window.
        /// Always needs corroboration.
        /// </summary>
        Timeout = 0,

        /// <summary>
        /// Something the source named is not present locally and the pipeline looked for it.
        /// Usually a real divergence, but the search runs against a world other feeders are still
        /// mutating, so it also needs one corroboration under a quiesced pipeline.
        /// </summary>
        MissingTarget,

        /// <summary>
        /// The local world contradicts the source's description in a way no amount of waiting can
        /// repair - two source entities collapsed onto one local entity, a duplicate identity, a
        /// graph that cannot be committed without dereferencing a stale original. Settles at once.
        /// </summary>
        Contradiction,

        /// <summary>
        /// Part of the command stream was lost, shed or refused before it could be applied, so
        /// this machine can no longer derive the source's state from what it received. Settles at
        /// once: nothing local will ever supply the missing commands.
        /// </summary>
        StreamLoss,
    }

    /// <summary>
    /// The evidence behind one resync request.
    ///
    /// Historically every caller passed a bare phrase ("native net target did not resolve") and the
    /// world reloaded. That is enough to grep for and not enough to fix anything: the log never
    /// said which operation, which endpoint, what was actually standing there instead, how long the
    /// pipeline had been blocked, or whether anything cheaper had been tried. A report carries all
    /// of that, is written as an ungated event whether or not the reload follows, and is
    /// what <see cref="ResyncArbiter"/> settles before any world is thrown away.
    ///
    /// Build one with <see cref="Create"/> and chain <see cref="Fact"/> calls; every setter returns
    /// the report so a call site stays one statement.
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
        /// What the request is about, stable across repeats - typically the operation identity.
        /// Two submissions with the same subject and reason are the SAME fault observed twice,
        /// which is what lets a held report settle; two different subjects are two faults.
        /// </summary>
        public string Subject { get; private set; }

        /// <summary>
        /// What the pipeline already tried before asking for a reload, in the caller's own words
        /// ("retried for 10 s", "replayed 3x"). Printed as its own line: a reader's first question
        /// about any automatic world reload is whether anything cheaper was attempted.
        /// </summary>
        public string Attempted { get; private set; }

        /// <summary>Set by the arbiter when the report is first submitted.</summary>
        public long FirstSeenMs { get; internal set; }

        /// <summary>How many times this exact fault has been submitted, including the first.</summary>
        public int Observations { get; internal set; }

        public static ResyncReport Create(string reason, string subsystem, ResyncEvidence evidence)
        {
            return new ResyncReport(reason, subsystem, evidence);
        }

        /// <summary>A bare legacy request: unclassified, and therefore never settled on sight.</summary>
        public static ResyncReport FromReason(string reason)
        {
            return new ResyncReport(reason, "sync", ResyncEvidence.Timeout);
        }

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

        public ResyncReport Fact(string name, long value)
        {
            return Fact(name, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        public ResyncReport Fact(string name, bool value) => Fact(name, value ? "yes" : "no");

        /// <summary>
        /// The report as the lines the log prints under its headline. Written in
        /// sentences, because the reader is usually a player pasting a log into a bug report.
        /// </summary>
        public List<string> Lines()
        {
            var lines = new List<string>(_facts.Count + 4);
            lines.Add("what happened: " + Reason);
            lines.Add("where: " + Subsystem + " sync, " + Subject);
            lines.Add("evidence: " + Describe(Evidence));
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
