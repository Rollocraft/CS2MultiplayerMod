using System;
using System.Collections.Generic;
using System.Text;
using CS2MultiplayerMod.Core.Diagnostics;

namespace CS2MultiplayerMod.Game.Diagnostics
{
    /// <summary>
    /// The mod's only logger. Every line names a <see cref="LogTopic"/>; the tag and severity are added
    /// here, never at the call site. Severity decides where a line goes:
    /// <list type="table">
    ///   <item><term><see cref="Detail"/></term><description>both logs, only while verbose logging is on.</description></item>
    ///   <item><term><see cref="Trace"/></term><description>always in the flight log; game log only in a developer build.</description></item>
    ///   <item><term><see cref="Event"/></term><description>a milestone; both logs, always.</description></item>
    ///   <item><term><see cref="Warn"/>, <see cref="Error"/></term><description>both logs, always, flushed.</description></item>
    /// </list>
    /// Both files come from this one path, so the flight log (<see cref="FlightRecorder"/>) is complete.
    /// </summary>
    public static class SyncLog
    {
        /// <summary>Per-topic tags in <see cref="LogTopic"/> order; short grep targets.</summary>
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
            "mod-sync",
        };

        /// <summary>
        /// Whether a <see cref="Detail"/> line on this topic is written. Ask before computing a diagnostic;
        /// never guard warnings, errors or events with it.
        /// </summary>
        public static bool IsEnabled(LogTopic topic)
        {
            if (LogTopics.DetailEnabled(topic)) return true;
            Setting setting = Mod.Setting;
            return setting != null && setting.VerboseLogging;
        }

        /// <summary>
        /// Whether a <see cref="Trace"/> is recorded; nearly always true. For callers that walk a batch to
        /// build one, in a build without a flight log.
        /// </summary>
        public static bool IsRecording(LogTopic topic) => FlightRecorder.Enabled || IsEnabled(topic);

        // ---- Gated: troubleshooting detail ------------------------------------------------

        /// <summary>Per-action troubleshooting detail, written only while verbose logging is on.</summary>
        public static void Detail(LogTopic topic, string message)
        {
            if (message == null || !IsEnabled(topic)) return;
            Emit(topic, Severity.Detail, message);
        }

        /// <summary>
        /// A multi-line detail report: one game-log line each, one flight-log event for the whole.
        /// </summary>
        public static void Detail(LogTopic topic, string headline, IList<string> lines)
        {
            if (!IsEnabled(topic)) return;
            EmitReport(topic, Severity.Detail, headline, lines);
        }

        /// <summary>
        /// A compact <c>key=value</c> breadcrumb, always in the flight log and buffered until the next fault
        /// commits it; in the game log only when <see cref="LogTopics"/> asks. A trace that needs a sentence
        /// is an <see cref="Event"/>.
        /// </summary>
        public static void Trace(LogTopic topic, string message)
        {
            if (message == null) return;
            if (LogTopics.TraceInGameLog(topic))
            {
                try { Mod.log.Info(Tag(topic) + " " + LogPaths.Redact(message)); }
                catch { }
            }
            FlightRecorder.Note("trace " + Tag(topic) + " " + message, false);
        }

        /// <summary>Detail tagged by city zone: the company channel serves three zones from one path.</summary>
        public static void DetailZone(SyncZone zone, string message) => Detail(TopicFor(zone), message);

        /// <summary>As <see cref="IsEnabled"/>, for a caller that only knows the zone.</summary>
        public static bool IsZoneEnabled(SyncZone zone) => IsEnabled(TopicFor(zone));

        // ---- Ungated: the lines a bug report is read for -----------------------------------

        /// <summary>
        /// A milestone for every log, never gated. Anything per frame, entity or command is
        /// <see cref="Detail"/>.
        /// </summary>
        public static void Event(LogTopic topic, string message)
        {
            if (message == null) return;
            Emit(topic, Severity.Event, message);
        }

        /// <summary>A multi-line milestone report. Never gated; see <see cref="Event"/>.</summary>
        public static void Event(LogTopic topic, string headline, IList<string> lines) =>
            EmitReport(topic, Severity.Event, headline, lines);

        /// <summary>Something went wrong and was worked around. Never gated.</summary>
        public static void Warn(LogTopic topic, string message)
        {
            if (message == null) return;
            Emit(topic, Severity.Warn, message);
        }

        /// <summary>A multi-line warning report. Never gated; see <see cref="Warn"/>.</summary>
        public static void Warn(LogTopic topic, string headline, IList<string> lines) =>
            EmitReport(topic, Severity.Warn, headline, lines);

        /// <summary>Something went wrong that the mod could not work around. Never gated.</summary>
        public static void Error(LogTopic topic, string message)
        {
            if (message == null) return;
            Emit(topic, Severity.Error, message);
        }

        /// <summary>
        /// As <see cref="Error(LogTopic,string)"/>: the game log gets the exception chain, the flight log also
        /// the full stack.
        /// </summary>
        public static void Error(LogTopic topic, string message, Exception exception)
        {
            if (exception == null) { Error(topic, message); return; }

            string text = (message ?? "Unhandled exception") + " :: " + Describe(exception);
            Emit(topic, Severity.Error, text);
            FlightRecorder.NoteException(Tag(topic) + " " + (message ?? ""), exception);
        }

        /// <summary>A multi-line error report. Never gated; see <see cref="Error(LogTopic,string)"/>.</summary>
        public static void Error(LogTopic topic, string headline, IList<string> lines) =>
            EmitReport(topic, Severity.Error, headline, lines);

        // ---- Machinery ---------------------------------------------------------------------

        private enum Severity { Detail, Event, Warn, Error }

        private static void Emit(LogTopic topic, Severity severity, string message)
        {
            // Redacted centrally: IO and asset faults quote profile paths.
            string line = Tag(topic) + " " + LogPaths.Redact(message);

            // The game log has its own severity column.
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

            // Every game-log line reaches the flight log; only faults and milestones flush.
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

        /// <summary>The flight log is one flat stream, so it carries the level in the line.</summary>
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

        /// <summary>A short description of an exception and its causes; the stack goes to the flight log.</summary>
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
