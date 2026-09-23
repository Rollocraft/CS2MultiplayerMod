using System.Collections.Generic;

namespace CS2MultiplayerMod.Core.Networking.Tcp
{
    /// <summary>
    /// Reserves frame memory before allocation; the event-count cap says nothing about queued bytes.
    /// </summary>
    internal sealed class InboundByteBudget
    {
        internal const long PerConnectionLimit = 32L * 1024 * 1024;
        internal const long AggregateLimit = 64L * 1024 * 1024;

        // Array header, event wrapper and queue node, so many small frames still count.
        private const long EventOverheadBytes = 132;

        private readonly Dictionary<ConnectionId, long> _connections =
            new Dictionary<ConnectionId, long>();
        private readonly object _gate = new object();
        private long _bytes;

        public bool TryReserve(ConnectionId id, int length)
        {
            if (length < 0) return false;
            long cost = length + EventOverheadBytes;
            lock (_gate)
            {
                _connections.TryGetValue(id, out long used);
                if (cost > PerConnectionLimit - used || cost > AggregateLimit - _bytes) return false;
                _connections[id] = used + cost;
                _bytes += cost;
                return true;
            }
        }

        public void Release(ConnectionId id, int length)
        {
            long cost = length + EventOverheadBytes;
            lock (_gate)
            {
                // Only a double release has no reservation; charging it would drift the budget negative.
                if (!_connections.TryGetValue(id, out long used)) return;
                long remaining = used - cost;
                if (remaining <= 0) _connections.Remove(id);
                else _connections[id] = remaining;
                _bytes -= cost < used ? cost : used;
            }
        }
    }
}
