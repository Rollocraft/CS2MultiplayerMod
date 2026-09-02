using System;
using System.Collections.Generic;
using System.Text;
using CS2MultiplayerMod.Core.Diagnostics;

namespace CS2MultiplayerMod.Game.Diagnostics
{
    /// <summary>
    /// The mod's logger. Everything the mod writes goes through here - there is no second way in.
    ///
    /// <b>A line belongs to a feature, not to a "debug" switch.</b> Every call names a
    /// <see cref="LogTopic"/>, and each topic is switched on by itself, so a player chasing one
    /// problem gets a log about that problem rather than everything at once. Asking
    /// <see cref="IsEnabled"/> is a field read, so a caller can and should ask before building the
    /// string: a diagnostic nobody reads must not cost a frame.
    ///
    /// <b>A fault is never a topic.</b> A warning, an error or a milestone is not a diagnostic a
    /// player chooses to receive - it is the thing they are about to report. So severity, not the
    /// switches, decides where a line goes. Nobody turns a switch on before the crash they did not
    /// know was coming:
    ///
    /// <list type="table">
    ///   <item><term><see cref="Detail"/></term><description>troubleshooting chatter; both logs,
    ///     but only while its topic is on.</description></item>
    ///   <item><term><see cref="Trace"/></term><description>a compact breadcrumb; always in the
    ///     flight log, in the game log only while its topic is on.</description></item>
    ///   <item><term><see cref="Event"/></term><description>a milestone; both logs,
    ///     always.</description></item>
    ///   <item><term><see cref="Warn"/>, <see cref="Error"/></term><description>something went
    ///     wrong; both logs, always, flushed.</description></item>
    /// </list>
    ///
    /// <b>The call site never writes a prefix.</b> It passes a plain sentence; the topic tag and
    /// the severity marker are attached here, in one place, so every line is uniformly greppable
    /// and no two subsystems can drift into spelling the same tag differently.
    ///
    /// <b>There is one log, written to two files.</b> Both are produced from this one path, so
    /// they never disagree: the game log is the readable one, and the flight log is the same
    /// content made durable, structured and process-wide - see <see cref="FlightRecorder"/> for
    /// why that second file has to exist. Nothing reaches the game log without also reaching the
    /// flight log, which is what makes "send us the flight log" a complete answer.
    /// </summary>
    public static class SyncLog
    {
        /// <summary>
        /// The tag attached to each topic, in <see cref="LogTopic"/> order. Lower case and short:
        /// these are grep targets first and prose second.
        /// </summary>
        private static readonly string[] Tags =
        {
            "startup",
            "session",
            "transport",
            "world",
            "resync",
            "pipeline",
            "nets",
            "buildings",
            "land",
            "city",
            "routes",
            "residential",
            "commercial",
            "industrial",
            "office",
            "players",
            "ui",
            "perf",
        };

        /// <summary>
        /// Whether a <see cref="Detail"/> line on this topic would be written anywhere.
        ///
        /// Ask before <i>computing</i> a diagnostic, not only before logging one. Warnings, errors
        /// and events do not consult this and must not be guarded by it - guarding a fault behind
        /// a switch is how a bug report arrives with the interesting line missing.
        /// </summary>
        public static bool IsEnabled(LogTopic topic)
        {
            Setting setting = Mod.Setting;
            if (setting == null) return false;
            return setting.VerboseLogging || setting.IsTopicEnabled(topic);
        }

        /// <summary>
        /// Whether a <see cref="Trace"/> on this topic would be recorded anywhere. Traces survive
        /// with every switch off, so this is nearly always true - it exists for the handful of
        /// callers that walk a batch to build one, and must not do that walk for a build that
        /// ships no flight log at all.
        /// </summary>
        public static bool IsRecording(LogTopic topic)
        {
            return FlightRecorder.Enabled || IsEnabled(topic);
        }

        // ---- Gated: troubleshooting detail ------------------------------------------------

        /// <summary>
        /// One line of troubleshooting detail, written only while its topic is switched on.
        ///
        /// This is where the per-action, per-entity and per-interval chatter belongs. Pass a plain
        /// sentence: the topic tag is added here.
        /// </summary>
        public static void Detail(LogTopic topic, string message)
        {
            if (message == null || !IsEnabled(topic)) return;
            Emit(topic, Severity.Detail, message);
        }

        /// <summary>
        /// A multi-line detail report. Each line reaches the game log on its own so the file stays
        /// one statement per line and greppable; the flight log takes the whole report as a single
        /// event, because there it is one fact rather than many.
        /// </summary>
        public static void Detail(LogTopic topic, string headline, IList<string> lines)
        {
            if (!IsEnabled(topic)) return;
            EmitReport(topic, Severity.Detail, headline, lines);
        }

        /// <summary>
        /// A breadcrumb: always recorded to the flight log, shown in the game log only while the
        /// topic is on.
        ///
        /// This is the tier for the compact <c>key=value</c> traces the sync pipeline leaves as it
        /// works - "operation dropped malformed", "target retrying", "graph matched". Individually
        /// they are noise; as the last forty lines before a crash they are the answer, which is why
        /// they are recorded whether or not anyone asked for the topic. They are buffered rather
        /// than flushed, and the fault that follows commits them (see
        /// <see cref="FlightRecorder.Note(string,bool)"/>).
        ///
        /// Keep them short and factual. A trace that needs a sentence is an <see cref="Event"/>.
        /// </summary>
        public static void Trace(LogTopic topic, string message)
        {
            if (message == null) return;
            if (IsEnabled(topic))
            {
                try { Mod.log.Info(Tag(topic) + " " + LogPaths.Redact(message)); }
                catch { }
            }
            FlightRecorder.Note("trace " + Tag(topic) + " " + message, false);
        }

        /// <summary>
        /// Detail about the part of the city a piece of work belongs to. The company channel serves
        /// three zones from one code path, so the zone - not the class - picks the reader's switch.
        /// </summary>
        public static void DetailZone(SyncZone zone, string message)
        {
            Detail(TopicFor(zone), message);
        }

        /// <summary>As <see cref="IsEnabled"/>, for a caller that only knows the zone.</summary>
        public static bool IsZoneEnabled(SyncZone zone)
        {
            return IsEnabled(TopicFor(zone));
        }

        // ---- Ungated: the lines a bug report is read for -----------------------------------

        /// <summary>
        /// A milestone worth having in every player's log: a session opened, a world finished
        /// transferring, a resync decided. Never gated.
        ///
        /// It is not a dumping ground. Anything that repeats per frame, per entity or per command
        /// is <see cref="Detail"/> - if a line can arrive twice a second it is not a milestone.
        /// </summary>
        public static void Event(LogTopic topic, string message)
        {
            if (message == null) return;
            Emit(topic, Severity.Event, message);
        }

        /// <summary>A multi-line milestone report. Never gated; see <see cref="Event"/>.</summary>
        public static void Event(LogTopic topic, string headline, IList<string> lines)
        {
            EmitReport(topic, Severity.Event, headline, lines);
        }

        /// <summary>
        /// Something went wrong and the mod worked around it - a command dropped, a peer timing
        /// out, a value that had to be corrected. Never gated: this is what "and then it went
        /// strange" looks like in a log.
        /// </summary>
        public static void Warn(LogTopic topic, string message)
        {
            if (message == null) return;
            Emit(topic, Severity.Warn, message);
        }

        /// <summary>A multi-line warning report. Never gated; see <see cref="Warn"/>.</summary>
        public static void Warn(LogTopic topic, string headline, IList<string> lines)
        {
            EmitReport(topic, Severity.Warn, headline, lines);
        }

        /// <summary>Something went wrong that the mod could not work around. Never gated.</summary>
        public static void Error(LogTopic topic, string message)
        {
            if (message == null) return;
            Emit(topic, Severity.Error, message);
        }

        /// <summary>
        /// As <see cref="Error(LogTopic,string)"/>, with the exception behind it.
        ///
        /// The game log gets the sentence plus the exception's type and message chain, so it stays
        /// readable; the flight log additionally gets the full stack, because a stack with line
        /// numbers is usually the entire answer and that is the file people send.
        /// </summary>
        public static void Error(LogTopic topic, string message, Exception exception)
        {
            if (exception == null) { Error(topic, message); return; }

            string text = (message ?? "Unhandled exception") + " :: " + Describe(exception);
            Emit(topic, Severity.Error, text);
            FlightRecorder.NoteException(Tag(topic) + " " + (message ?? ""), exception);
        }

        /// <summary>A multi-line error report. Never gated; see <see cref="Error(LogTopic,string)"/>.</summary>
        public static void Error(LogTopic topic, string headline, IList<string> lines)
        {
            EmitReport(topic, Severity.Error, headline, lines);
        }

        // ---- Machinery ---------------------------------------------------------------------

        private enum Severity { Detail, Event, Warn, Error }

        private static void Emit(LogTopic topic, Severity severity, string message)
        {
            // Redacted here rather than at each call site: IO and asset faults quote the offending
            // path, and every CS2 folder sits under the player's profile, so a raw path in a log
            // pasted into a bug report hands out their Windows account name.
            string line = Tag(topic) + " " + LogPaths.Redact(message);

            // The game log is the readable one, so it keeps the framework's own severity column
            // rather than repeating the level in the text.
            try
            {
                switch (severity)
                {
                    case Severity.Warn: Mod.log.Warn(line); break;
                    case Severity.Error: Mod.log.Error(line); break;
                    default: Mod.log.Info(line); break;
                }
            }
            catch { /* diagnostics must never take the mod down */ }

            // The flight log gets every line the game log gets, so a player only ever has to send
            // that one file. Only a fault or a milestone is worth stalling the caller to flush.
            FlightRecorder.Note(Level(severity) + " " + line, severity != Severity.Detail);
        }

        private static void EmitReport(LogTopic topic, Severity severity, string headline,
            IList<string> lines)
        {
            string tag = Tag(topic);
            string level = Level(severity);

            try
            {
                if (headline != null) WriteGameLog(severity, tag + " " + LogPaths.Redact(headline));
                if (lines != null)
                    for (int i = 0; i < lines.Count; i++)
                        if (lines[i] != null)
                            WriteGameLog(severity, tag + "     " + LogPaths.Redact(lines[i]));
            }
            catch { }

            FlightRecorder.Note(level + " " + tag + " " + Flatten(headline, lines),
                severity != Severity.Detail);
        }

        private static void WriteGameLog(Severity severity, string line)
        {
            switch (severity)
            {
                case Severity.Warn: Mod.log.Warn(line); break;
                case Severity.Error: Mod.log.Error(line); break;
                default: Mod.log.Info(line); break;
            }
        }

        private static string Flatten(string headline, IList<string> lines)
        {
            var flat = new StringBuilder(headline ?? string.Empty);
            if (lines != null)
                for (int i = 0; i < lines.Count; i++)
                {
                    if (lines[i] == null) continue;
                    if (flat.Length > 0) flat.Append(" | ");
                    flat.Append(lines[i]);
                }
            return flat.ToString();
        }

        /// <summary>
        /// The severity marker for the flight log. The game log has a severity column of its own;
        /// the flight log is one flat stream, so it carries the level in the line.
        /// </summary>
        private static string Level(Severity severity)
        {
            switch (severity)
            {
                case Severity.Warn: return "WARN";
                case Severity.Error: return "ERROR";
                case Severity.Event: return "EVENT";
                default: return "detail";
            }
        }

        /// <summary>
        /// One short, greppable description of an exception and its causes. Deliberately not
        /// <c>ToString()</c>: that is the whole stack, which belongs in the flight log, not in the
        /// middle of a readable sentence.
        /// </summary>
        private static string Describe(Exception exception)
        {
            var text = new StringBuilder();
            for (int depth = 0; exception != null && depth < 4; depth++)
            {
                if (text.Length > 0) text.Append(" <- ");
                try { text.Append(exception.GetType().Name).Append(": ").Append(exception.Message); }
                catch { text.Append("unreadable exception"); }
                exception = exception.InnerException;
            }
            return text.ToString();
        }

        private static LogTopic TopicFor(SyncZone zone)
        {
            switch (zone)
            {
                case SyncZone.Residential: return LogTopic.Residential;
                case SyncZone.Commercial: return LogTopic.Commercial;
                case SyncZone.Industrial: return LogTopic.Industrial;
                case SyncZone.Office: return LogTopic.Office;
                default: return LogTopic.Pipeline;
            }
        }

        private static string Tag(LogTopic topic)
        {
            int index = (int)topic;
            return "[" + (index >= 0 && index < Tags.Length ? Tags[index] : Tags[0]) + "]";
        }
    }
}
