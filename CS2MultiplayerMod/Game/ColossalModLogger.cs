using System;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Game.Diagnostics;

namespace CS2MultiplayerMod.Game
{
    /// <summary>
    /// The seam where Core's <see cref="IModLogger"/> meets <see cref="SyncLog"/>, so transport and sync
    /// lines share one log, tags, gating and flight-log rules. Stateless.
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
