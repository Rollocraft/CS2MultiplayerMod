using System;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Game.Diagnostics;

namespace CS2MultiplayerMod.Game
{
    /// <summary>
    /// Lets the portable core write through the mod's one logger.
    ///
    /// Nothing under <c>Core/</c> may reference a game assembly, so the networking and session code
    /// logs against <see cref="IModLogger"/>. This is the single seam where that interface meets
    /// <see cref="SyncLog"/> - the core names the same <see cref="LogTopic"/> values as the game
    /// layer, so a transport line and a road line land in one log, tagged the same way, gated the
    /// same way, and mirrored to the flight log by the same rules.
    ///
    /// It holds no state: the destinations are the mod's static log and flight recorder.
    /// </summary>
    public sealed class ColossalModLogger : IModLogger
    {
        public static readonly ColossalModLogger Instance = new ColossalModLogger();

        private ColossalModLogger() { }

        public bool IsEnabled(LogTopic topic) => SyncLog.IsEnabled(topic);
        public void Detail(LogTopic topic, string message) => SyncLog.Detail(topic, message);
        public void Trace(LogTopic topic, string message) => SyncLog.Trace(topic, message);
        public void Event(LogTopic topic, string message) => SyncLog.Event(topic, message);
        public void Warn(LogTopic topic, string message) => SyncLog.Warn(topic, message);
        public void Error(LogTopic topic, string message) => SyncLog.Error(topic, message);

        public void Error(LogTopic topic, string message, Exception exception) =>
            SyncLog.Error(topic, message, exception);
    }
}
