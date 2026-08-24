using System;
using System.Collections.Concurrent;

namespace CS2MultiplayerMod.Core.Diagnostics
{
    /// <summary>
    /// Real-time bandwidth and packet profiler tracking throughput per command ID.
    /// </summary>
    public static class NetworkProfiler
    {
        private static readonly ConcurrentDictionary<ushort, CommandStats> Stats =
            new ConcurrentDictionary<ushort, CommandStats>();

        public static void RecordSent(ushort commandId, int bytes)
        {
            CommandStats stat = Stats.GetOrAdd(commandId, _ => new CommandStats());
            stat.SentCount++;
            stat.SentBytes += bytes;
        }

        public static void RecordReceived(ushort commandId, int bytes)
        {
            CommandStats stat = Stats.GetOrAdd(commandId, _ => new CommandStats());
            stat.RecvCount++;
            stat.RecvBytes += bytes;
        }

        public static long GetTotalBytesSent()
        {
            long total = 0;
            foreach (var s in Stats.Values) total += s.SentBytes;
            return total;
        }

        public static long GetTotalBytesReceived()
        {
            long total = 0;
            foreach (var s in Stats.Values) total += s.RecvBytes;
            return total;
        }

        public sealed class CommandStats
        {
            public long SentCount;
            public long SentBytes;
            public long RecvCount;
            public long RecvBytes;
        }
    }
}
