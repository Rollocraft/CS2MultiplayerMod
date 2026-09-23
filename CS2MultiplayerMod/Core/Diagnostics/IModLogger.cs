namespace CS2MultiplayerMod.Core.Diagnostics
{
    /// <summary>
    /// Core logging abstraction, free of game assemblies so Core stays testable (tests pass
    /// <see cref="NullModLogger"/>). Same shape as the game logger: <see cref="Detail"/> is gated,
    /// <see cref="Trace"/> always reaches the crash log, <see cref="Event"/> and above are never gated.
    /// </summary>
    public interface IModLogger
    {
        /// <summary>Whether a <see cref="Detail"/> line is written; ask before computing a diagnostic.</summary>
        bool IsEnabled(LogTopic topic);

        /// <summary>Troubleshooting detail. Written only while the topic is switched on.</summary>
        void Detail(LogTopic topic, string message);

        /// <summary>A breadcrumb: always in the crash log, in the readable log only when the topic is on.</summary>
        void Trace(LogTopic topic, string message);

        /// <summary>A milestone worth having in every player's log. Never gated.</summary>
        void Event(LogTopic topic, string message);

        /// <summary>Something went wrong but the mod worked around it. Never gated.</summary>
        void Warn(LogTopic topic, string message);

        /// <summary>Something went wrong that the mod could not work around. Never gated.</summary>
        void Error(LogTopic topic, string message);

        /// <summary>As <see cref="Error(LogTopic,string)"/>, with the exception that caused it.</summary>
        void Error(LogTopic topic, string message, System.Exception exception);
    }
}
