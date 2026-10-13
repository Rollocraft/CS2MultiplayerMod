using System;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Game.Diagnostics;

namespace CS2MultiplayerMod.Game.Sync.Infrastructure
{
    internal static class CommandDecode
    {
        /// <summary>
        /// Decode a peer command, or log and drop it. A malformed peer command is a peer problem,
        /// not local corruption, so it never requests a resync on its own.
        /// </summary>
        public static bool TryDecode<T>(SimulationCommandMessage message, Func<byte[], T> decode,
            LogTopic topic, string owner, out T command, string what = "command")
        {
            try
            {
                command = decode(message.Body);
                return true;
            }
            catch (Exception ex)
            {
                SyncLog.Warn(topic, owner + ": dropping malformed " + what + ": " + ex.Message);
                command = default(T);
                return false;
            }
        }
    }
}
