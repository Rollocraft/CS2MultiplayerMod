namespace CS2MultiplayerMod.Core.Diagnostics
{
    /// <summary>
    /// Logging abstraction for the multiplayer core.
    ///
    /// The core deliberately does not reference Colossal.Logging (or any game assembly) so it
    /// stays portable and unit-testable. The game layer supplies a concrete adapter; tests can
    /// pass <see cref="NullModLogger"/>.
    ///
    /// The shape mirrors the game layer's logger exactly, so there is one vocabulary across the
    /// whole mod: every line names a <see cref="LogTopic"/>, and the severity decides whether the
    /// topic's switch is consulted at all. <see cref="Detail"/> is troubleshooting chatter and is
    /// gated; <see cref="Trace"/> is kept in the crash log either way; and everything from
    /// <see cref="Event"/> upwards is written to both logs whatever the switches say, because a
    /// player cannot be expected to have turned on the right switch before the thing they are
    /// reporting happened.
    /// </summary>
    public interface IModLogger
    {
        /// <summary>
        /// Whether a <see cref="Detail"/> line on this topic would be written. Ask before
        /// <i>computing</i> a diagnostic, not only before logging one: a counter nobody reads
        /// must not cost a frame.
        /// </summary>
        bool IsEnabled(LogTopic topic);

        /// <summary>Troubleshooting detail. Written only while the topic is switched on.</summary>
        void Detail(LogTopic topic, string message);

        /// <summary>
        /// A short breadcrumb: always kept in the crash log, shown in the readable log only while
        /// the topic is switched on.
        /// </summary>
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
