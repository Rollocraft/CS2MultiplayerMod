using System;
using Colossal.Logging;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Game.Diagnostics;

namespace CS2MultiplayerMod.Game
{
    /// <summary>
    /// Adapts the core's game-agnostic <see cref="IModLogger"/> onto Colossal's
    /// <see cref="ILog"/>. This is the single seam where the portable core meets the
    /// game's logging; nothing under <c>Core/</c> references Colossal types.
    /// </summary>
    public sealed class ColossalModLogger : IModLogger
    {
        private readonly ILog _log;

        public ColossalModLogger(ILog log)
        {
            _log = log;
        }

        // Redacted here rather than at each call site: IO and asset faults quote the
        // offending path, which sits under the player's profile.
        public void Debug(string message) => _log.Debug(LogPaths.Redact(message));
        public void Info(string message) => _log.Info(LogPaths.Redact(message));
        public void Warn(string message) => _log.Warn(LogPaths.Redact(message));
        public void Error(string message) => _log.Error(LogPaths.Redact(message));
        public void Error(string message, Exception exception) => _log.Error(LogPaths.Redact(message + " :: " + exception));
    }
}
