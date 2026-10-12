using CS2MultiplayerMod.Core.Diagnostics;

namespace CS2MultiplayerMod.Game.Diagnostics
{
    /// <summary>
    /// Developer per-topic detail switches; players have only Verbose Logging. Uncomment a topic in
    /// <see cref="Enabled"/> or set fields from a debugger (not <c>const</c> for that reason). Ship false.
    /// </summary>
    internal static class LogTopics
    {
        /// <summary>Topics whose <see cref="SyncLog.Detail"/> lines are written as if verbose were on for them.</summary>
        private static readonly LogTopic[] Enabled =
        {
            // LogTopic.Nets,
            // LogTopic.Buildings,
            // LogTopic.Residential,
        };

        /// <summary>Every topic at once - the developer-side equivalent of the player's switch.</summary>
        private static readonly bool AllTopics = false;

        /// <summary>
        /// Whether <see cref="SyncLog.Trace"/> lines reach the game log. Off even when verbose: traces would
        /// swamp it. They are always in the flight log.
        /// </summary>
        private static readonly bool TracesInGameLog = false;

        /// <summary>Whether this build asks for <see cref="SyncLog.Detail"/> on this topic.</summary>
        public static bool DetailEnabled(LogTopic topic)
        {
            if (AllTopics) return true;
            for (int i = 0; i < Enabled.Length; i++)
                if (Enabled[i] == topic) return true;
            return false;
        }

        /// <summary>Whether a <see cref="SyncLog.Trace"/> on this topic is mirrored to the game log.</summary>
        public static bool TraceInGameLog(LogTopic topic) => TracesInGameLog || DetailEnabled(topic);
    }
}
