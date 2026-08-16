namespace CS2MultiplayerMod.Core.Session
{
    /// <summary>
    /// Per-connection traffic budget enforced by host on client sends. One-second buckets
    /// (plus one-minute for resyncs). Cheap, allocation-free, stops floods. Exceeding
    /// any budget disconnects the peer.
    /// </summary>
    public sealed class PeerRateLimiter
    {
        // Generous for honest play, hopeless for floods. The command ceiling has to
        // absorb real editing bursts: a single zone-paint or bulldoze drag emits ONE
        // command per affected block/segment in the same frame, so a normal area edit
        // legitimately produces hundreds of commands in a second. The old cap of 60
        // disconnected players mid-zoning; these values clear honest peaks by a wide
        // margin while a genuine flood (a tight send loop does tens of thousands/sec)
        // still trips instantly. The bytes/sec and messages/sec budgets remain the
        // real backstop against bandwidth/packet floods.
        public const int MaxMessagesPerSecond = 3000;
        public const int MaxBytesPerSecond = 4 * 1024 * 1024;
        public const int MaxCommandsPerSecond = 1500;
        public const int MaxChatPerSecond = 5;
        public const int MaxResyncPerMinute = 2;

        private long _secondStartMs;
        private int _messages;
        private int _bytes;
        private int _commands;
        private int _chat;

        private long _minuteStartMs;
        private int _resyncs;

        /// <summary>Account one received message. Returns null if fine, else the violated budget's name.</summary>
        public string Account(long nowMs, int payloadBytes, bool isCommand, bool isChat, bool isResync)
        {
            if (nowMs - _secondStartMs >= 1000)
            {
                _secondStartMs = nowMs;
                _messages = 0;
                _bytes = 0;
                _commands = 0;
                _chat = 0;
            }

            if (nowMs - _minuteStartMs >= 60000)
            {
                _minuteStartMs = nowMs;
                _resyncs = 0;
            }

            _messages++;
            _bytes += payloadBytes;
            if (isCommand) _commands++;
            if (isChat) _chat++;
            if (isResync) _resyncs++;

            if (_messages > MaxMessagesPerSecond) return "messages/sec (" + _messages + ")";
            if (_bytes > MaxBytesPerSecond) return "bytes/sec (" + _bytes + ")";
            if (_commands > MaxCommandsPerSecond) return "commands/sec (" + _commands + ")";
            if (_chat > MaxChatPerSecond) return "chat/sec (" + _chat + ")";
            if (_resyncs > MaxResyncPerMinute) return "resyncs/min (" + _resyncs + ")";
            return null;
        }
    }
}
