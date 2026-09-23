using System;

namespace CS2MultiplayerMod.Core.Networking.Steam
{
    /// <summary>Tracks acknowledged bytes while the session keeps replenishing the outbox.</summary>
    internal sealed class RelaySendFeedback
    {
        private long _acknowledged;

        public long Sample(long acceptedBytes, long outstandingBytes)
        {
            long acknowledged = Math.Max(0L, acceptedBytes - Math.Max(0L, outstandingBytes));
            long moved = Math.Max(0L, acknowledged - _acknowledged);
            _acknowledged = Math.Max(_acknowledged, acknowledged);
            return moved;
        }

        // Quality and ping describe a window seconds old; a rate acknowledged near its own pace is not
        // congested now, so the complaint holds the rate instead of cutting it.
        public static bool IsDelivering(long goodput, int sendRate, float share) =>
            sendRate > 0 && goodput >= (long)(sendRate * share);
    }
}
