using CS2MultiplayerMod.Localization;

namespace CS2MultiplayerMod.Game.Diagnostics
{
    /// <summary>
    /// What a diagnostic line is about. Each one can be turned on by itself, so a player chasing
    /// one problem gets a log about that problem instead of everything at once - which is the
    /// difference between a log someone will actually read and a wall of text.
    /// </summary>
    public enum LogTopic
    {
        /// <summary>Anything without a more specific home. Follows the general verbose switch.</summary>
        General = 0,

        /// <summary>
        /// The production level: always written, never gated by a setting, and deliberately
        /// unprefixed.
        ///
        /// It exists for the handful of lines that have to be in every player's log because they
        /// are what a bug report is read for - why the world was reloaded, what the pipeline was
        /// holding when it decided, what it tried first. A player never turns these on, so they
        /// cannot be missing from the one log that matters, and a reader who does not know the
        /// mod's topic prefixes still reads them as ordinary sentences.
        ///
        /// It is not a dumping ground: anything that would repeat per frame, per entity or per
        /// command belongs to a topic switch or the flight recorder instead.
        /// </summary>
        Prod,

        /// <summary>Frame times and the mod's own main-thread cost, including the per-zone split.</summary>
        Performance,

        /// <summary>Households, residents and their homes.</summary>
        Residential,

        /// <summary>Shops: tenancy, figures and stock.</summary>
        Commercial,

        /// <summary>Factories and extractors: tenancy, figures and stock.</summary>
        Industrial,

        /// <summary>Offices: tenancy, figures and stock.</summary>
        Office,
    }

    /// <summary>
    /// The mod's diagnostic log, split by topic.
    ///
    /// Two rules make this worth having over a single verbose switch. A topic that is off costs
    /// nothing - <see cref="IsEnabled"/> is a field read, so a caller can and should ask before
    /// building the string. And a line always says which topic it belongs to, so a log with
    /// several topics on is still readable and greppable.
    ///
    /// A fault is never a topic. A warning or an error is not a diagnostic a player chooses to
    /// receive, so it is never gated by a switch: it goes out through the production level
    /// (<see cref="Prod"/>, <see cref="ProdWarn"/>, <see cref="ProdError"/>, <see cref="ProdReport"/>),
    /// which writes to the game log and the flight recorder unconditionally and without a prefix.
    /// </summary>
    public static class SyncLog
    {
        private static readonly string[] Prefixes =
        {
            "[MP] ",
            "",             // Prod: deliberately unprefixed.
            "[MP][perf] ",
            "[MP][residential] ",
            "[MP][commercial] ",
            "[MP][industrial] ",
            "[MP][office] ",
        };

        /// <summary>
        /// Whether anything would come of writing to this topic. Ask before <i>computing</i> a
        /// diagnostic, not only before logging one: a counter nobody reads must not cost a frame.
        /// </summary>
        public static bool IsEnabled(LogTopic topic)
        {
            // Asked before the setting exists too: a fault during load still has to be reported,
            // and the production level is the level that is never allowed to be silent.
            if (topic == LogTopic.Prod) return true;
            Setting setting = Mod.Setting;
            if (setting == null) return false;
            switch (topic)
            {
                case LogTopic.Performance: return setting.LogPerformance;
                case LogTopic.Residential: return setting.LogResidential;
                case LogTopic.Commercial: return setting.LogCommercial;
                case LogTopic.Industrial: return setting.LogIndustrial;
                case LogTopic.Office: return setting.LogOffice;
                default: return setting.VerboseLogging;
            }
        }

        /// <summary>Write one line, if its topic is on.</summary>
        public static void Write(LogTopic topic, string message)
        {
            if (!IsEnabled(topic)) return;
            Mod.log.Info(Prefix(topic) + message);
        }

        /// <summary>
        /// Write one line to the topic that matches a workplace zone. Used by the shared company
        /// channel, whose three zones are three separate switches for the reader.
        /// </summary>
        public static void WriteZone(SyncZone zone, string message) => Write(TopicFor(zone), message);

        public static bool IsZoneEnabled(SyncZone zone) => IsEnabled(TopicFor(zone));

        /// <summary>
        /// A line worth keeping whether or not anyone asked for the topic. The flight log takes it
        /// regardless, because a performance report is exactly the case where the log was already
        /// captured before anyone thought to turn a switch on.
        /// </summary>
        public static void Record(LogTopic topic, string message)
        {
            if (IsEnabled(topic)) Mod.log.Info(Prefix(topic) + message);
            FlightRecorder.Note(message);
        }

        /// <summary>
        /// Write one production line: always emitted, no prefix, and always mirrored to the flight
        /// recorder so the structured log carries the same statement as the game log.
        /// </summary>
        public static void Prod(string message)
        {
            if (message == null) return;
            Mod.log.Info(message);
            FlightRecorder.Note(message);
        }

        /// <summary>A production line the player is meant to act on (or explain in a report).</summary>
        public static void ProdWarn(string message)
        {
            if (message == null) return;
            Mod.log.Warn(message);
            FlightRecorder.Note(message);
        }

        /// <summary>A production line for a fault the mod could not work around.</summary>
        public static void ProdError(string message)
        {
            if (message == null) return;
            Mod.log.Error(message);
            FlightRecorder.Note(message);
        }

        /// <summary>
        /// Write a multi-line production report. Each line goes out on its own so the game log
        /// stays one-statement-per-line and greppable; the flight recorder takes the whole report
        /// as a single compact event, because there it is one fact, not many.
        /// </summary>
        public static void ProdReport(string headline, System.Collections.Generic.IList<string> lines)
        {
            if (headline != null) Mod.log.Info(headline);
            if (lines != null)
                for (int i = 0; i < lines.Count; i++)
                    if (lines[i] != null) Mod.log.Info("    " + lines[i]);
            FlightRecorder.Note(FlattenReport(headline, lines));
        }

        private static string FlattenReport(string headline,
            System.Collections.Generic.IList<string> lines)
        {
            var flat = new System.Text.StringBuilder(headline ?? string.Empty);
            if (lines != null)
                for (int i = 0; i < lines.Count; i++)
                {
                    if (lines[i] == null) continue;
                    if (flat.Length > 0) flat.Append(" | ");
                    flat.Append(lines[i]);
                }
            return flat.ToString();
        }

        private static LogTopic TopicFor(SyncZone zone)
        {
            switch (zone)
            {
                case SyncZone.Residential: return LogTopic.Residential;
                case SyncZone.Commercial: return LogTopic.Commercial;
                case SyncZone.Industrial: return LogTopic.Industrial;
                case SyncZone.Office: return LogTopic.Office;
                default: return LogTopic.General;
            }
        }

        private static string Prefix(LogTopic topic)
        {
            int index = (int)topic;
            return index >= 0 && index < Prefixes.Length ? Prefixes[index] : Prefixes[0];
        }
    }
}
