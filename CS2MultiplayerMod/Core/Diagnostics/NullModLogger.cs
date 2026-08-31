using System;

namespace CS2MultiplayerMod.Core.Diagnostics
{
    /// <summary>A logger that discards everything. Useful as a default and in tests.</summary>
    public sealed class NullModLogger : IModLogger
    {
        public static readonly NullModLogger Instance = new NullModLogger();

        private NullModLogger() { }

        public bool IsEnabled(LogTopic topic) { return false; }
        public void Detail(LogTopic topic, string message) { }
        public void Trace(LogTopic topic, string message) { }
        public void Event(LogTopic topic, string message) { }
        public void Warn(LogTopic topic, string message) { }
        public void Error(LogTopic topic, string message) { }
        public void Error(LogTopic topic, string message, Exception exception) { }
    }
}
