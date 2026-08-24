using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace CS2MultiplayerMod.Core.Session
{
    /// <summary>
    /// Rolling municipal action audit log recording player building and demolition activities.
    /// </summary>
    public static class AuditLog
    {
        private const int MaxEntries = 200;

        public struct Entry
        {
            public long TimestampMs;
            public int PlayerId;
            public string PlayerName;
            public string Action;
            public string Details;
        }

        private static readonly ConcurrentQueue<Entry> Entries = new ConcurrentQueue<Entry>();

        public static void Record(int playerId, string playerName, string action, string details)
        {
            var entry = new Entry
            {
                TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                PlayerId = playerId,
                PlayerName = playerName ?? "Player #" + playerId,
                Action = action,
                Details = details ?? ""
            };

            Entries.Enqueue(entry);
            while (Entries.Count > MaxEntries && Entries.TryDequeue(out _)) { }
        }

        public static List<Entry> GetRecent(int count = 20, int filterPlayerId = -1)
        {
            var list = new List<Entry>();
            foreach (var e in Entries)
            {
                if (filterPlayerId == -1 || e.PlayerId == filterPlayerId)
                {
                    list.Add(e);
                }
            }
            if (list.Count > count)
            {
                list.RemoveRange(0, list.Count - count);
            }
            return list;
        }
    }
}
