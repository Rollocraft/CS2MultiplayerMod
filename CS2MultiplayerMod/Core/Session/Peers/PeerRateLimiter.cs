namespace CS2MultiplayerMod.Core.Session
{
    /// <summary>
    /// Per-connection host budgets on what a client sends: a sustained rate plus a burst reservoir,
    /// metered at arrival time, so tool bursts and host hitches pass while a tight send loop is cut
    /// off. Commands are metered but never fatal: bandwidth and message budgets stop floods, and the
    /// command budget names a too-chatty sync system in the log.
    /// </summary>
    public sealed class PeerRateLimiter
    {
        public const int MaxMessagesPerSecond = 3000;
        public const int MaxMessageBurst = 6000;
        public const int MaxBytesPerSecond = 4 * 1024 * 1024;
        public const int MaxByteBurst = 12 * 1024 * 1024;
        public const int MaxChatPerSecond = 5;
        public const int MaxChatBurst = 10;
        public const int MaxResyncPerMinute = 2;
        public const int MaxResyncBurst = 2;

        /// <summary>Sustained command rate above which the overrun line is worth printing.</summary>
        public const int MaxCommandsPerSecond = 1500;

        /// <summary>How far past the sustained command rate an ordinary editing burst reaches.</summary>
        public const int MaxCommandBurst = 3000;

        /// <summary>Minimum gap between overrun lines, so a busy peer is described, not narrated.</summary>
        private const long OverrunReportIntervalMs = 30000;

        /// <summary>Command ids tallied for the overrun line. Past this the rest are pooled.</summary>
        private const int TalliedCommandIds = 8;

        private Bucket _messages;
        private Bucket _bytes;
        private Bucket _commands;
        private Bucket _chat;
        private Bucket _resyncs;

        private readonly int[] _tallyId = new int[TalliedCommandIds];
        private readonly int[] _tallyCount = new int[TalliedCommandIds];
        private int _tallied;
        private int _overrunCommands;
        private bool _overrunOpen;
        private long _overrunSinceMs;
        private bool _overrunReported;
        private long _lastOverrunReportMs;

        /// <summary>
        /// Accounts one message at its arrival time. Null when within budget, else the broken budget's name,
        /// which disconnects.
        /// </summary>
        public string Account(long nowMs, int payloadBytes, int commandId, bool isChat, bool isResync)
        {
            double messages = _messages.Add(nowMs, 1, MaxMessagesPerSecond);
            double bytes = _bytes.Add(nowMs, payloadBytes, MaxBytesPerSecond);

            if (commandId >= 0) AccountCommand(nowMs, commandId);
            double chat = isChat ? _chat.Add(nowMs, 1, MaxChatPerSecond) : 0;
            double resyncs = isResync ? _resyncs.Add(nowMs, 1, MaxResyncPerMinute / 60d) : 0;

            if (messages > MaxMessageBurst) return "messages/sec (" + Rate(messages, MaxMessageBurst, MaxMessagesPerSecond) + ")";
            if (bytes > MaxByteBurst) return "bytes/sec (" + Rate(bytes, MaxByteBurst, MaxBytesPerSecond) + ")";
            if (chat > MaxChatBurst) return "chat/sec (" + Rate(chat, MaxChatBurst, MaxChatPerSecond) + ")";
            if (resyncs > MaxResyncBurst) return "resyncs/min (" + (int)resyncs + ")";
            return null;
        }

        /// <summary>A command overrun line, or null; taking it starts a new window.</summary>
        public string TakeCommandOverrun(long nowMs)
        {
            if (!_overrunOpen) return null;

            // Let the burst finish; a peer that stays over is described once per interval.
            long over = nowMs - _overrunSinceMs;
            if (over < 1000) return null;
            if (_overrunReported && nowMs - _lastOverrunReportMs < OverrunReportIntervalMs)
                return null;

            string top = null;
            int topCount = 0;
            for (int i = 0; i < _tallied; i++)
            {
                if (_tallyCount[i] <= topCount) continue;
                topCount = _tallyCount[i];
                top = "id " + _tallyId[i];
            }

            string line = _overrunCommands + " command(s) in " + over + " ms past the " +
                          MaxCommandsPerSecond + "/s budget" +
                          (top != null ? ", mostly " + top + " (" + topCount + ")" : "") +
                          ". Nobody is disconnected for this; it means a sync system is " +
                          "sending more than the session is meant to carry.";

            _overrunReported = true;
            _lastOverrunReportMs = nowMs;
            _overrunOpen = false;
            _overrunCommands = 0;
            _tallied = 0;
            return line;
        }

        private void AccountCommand(long nowMs, int commandId)
        {
            if (_commands.Add(nowMs, 1, MaxCommandsPerSecond) <= MaxCommandBurst) return;

            if (!_overrunOpen)
            {
                _overrunOpen = true;
                _overrunSinceMs = nowMs;
            }
            _overrunCommands++;
            Tally(commandId);
        }

        private void Tally(int commandId)
        {
            for (int i = 0; i < _tallied; i++)
            {
                if (_tallyId[i] != commandId) continue;
                _tallyCount[i]++;
                return;
            }
            if (_tallied >= TalliedCommandIds) return;
            _tallyId[_tallied] = commandId;
            _tallyCount[_tallied] = 1;
            _tallied++;
        }

        /// <summary>How far a reservoir was pushed past what a sustained rate could drain.</summary>
        private static string Rate(double level, double burst, double perSecond) =>
            (int)level + " over a " + (int)burst + " burst at " + (int)perSecond + "/s";

        /// <summary>
        /// Fills with what a peer sends and drains at the sustained rate; the level is seconds past the
        /// rate, so short bursts are free and floods are fatal.
        /// </summary>
        private struct Bucket
        {
            private double _level;
            private long _lastMs;
            private bool _started;

            public double Add(long nowMs, double amount, double perSecond)
            {
                if (!_started)
                {
                    _started = true;
                    _lastMs = nowMs;
                }

                long elapsed = nowMs - _lastMs;
                if (elapsed > 0)
                {
                    _lastMs = nowMs;
                    _level -= perSecond * (elapsed / 1000d);
                    if (_level < 0) _level = 0;
                }
                else if (elapsed < 0)
                {
                    // The caller's clock moved, not the peer's traffic: hold the level.
                    _lastMs = nowMs;
                }

                _level += amount;
                return _level;
            }
        }
    }
}
