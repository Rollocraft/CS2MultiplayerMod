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
    /// Faults are never routed here. A warning or an error is not a diagnostic a player chooses
    /// to receive; those keep going straight to the game log and the flight recorder.
    /// </summary>
    public static class SyncLog
    {
        private static readonly string[] Prefixes =
        {
            "[MP] ",
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
