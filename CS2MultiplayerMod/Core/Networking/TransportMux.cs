using System.Collections.Generic;
using System.Diagnostics;

namespace CS2MultiplayerMod.Core.Networking
{
    /// <summary>
    /// One host session over two listeners, e.g. a direct port plus the relay for listed joins. The primary's
    /// connection ids pass through unchanged; the secondary's carry <see cref="SecondaryTag"/>.
    /// </summary>
    public sealed class TransportMux : ITransport, IPlatformFriendLookup, IInboundActivity
    {
        /// <summary>Both inner transports number connections upward from 2, far below this bit.</summary>
        private const int SecondaryTag = 1 << 30;

        private readonly ITransport _primary;
        private readonly ITransport _secondary;
        private readonly List<TransportEvent> _scratch = new List<TransportEvent>();

        public TransportMux(ITransport primary, ITransport secondary)
        {
            _primary = primary;
            _secondary = secondary;
        }

        public bool IsActive => _primary.IsActive || _secondary.IsActive;

        public long PendingSendBytes => _primary.PendingSendBytes + _secondary.PendingSendBytes;

        public void Send(ConnectionId target, byte[] payload) => Route(target, out ConnectionId inner).Send(inner, payload);

        public void Disconnect(ConnectionId connection) => Route(connection, out ConnectionId inner).Disconnect(inner);

        public void DisconnectAfterFlush(ConnectionId connection) =>
            Route(connection, out ConnectionId inner).DisconnectAfterFlush(inner);

        public string GetRemoteAddress(ConnectionId connection) =>
            Route(connection, out ConnectionId inner).GetRemoteAddress(inner);

        public byte[] GetChannelBinding(ConnectionId connection) =>
            Route(connection, out ConnectionId inner).GetChannelBinding(inner);

        public bool IsPlatformFriend(ConnectionId connection) =>
            Route(connection, out ConnectionId inner) is IPlatformFriendLookup lookup && lookup.IsPlatformFriend(inner);

        public long LastInboundActivityMs(ConnectionId connection) =>
            Route(connection, out ConnectionId inner) is IInboundActivity activity
                ? activity.LastInboundActivityMs(inner)
                : long.MinValue;

        public int Poll(IList<TransportEvent> sink)
        {
            int count = _primary.Poll(sink);
            _scratch.Clear();
            _secondary.Poll(_scratch);
            foreach (TransportEvent evt in _scratch)
                sink.Add(evt.WithConnection(new ConnectionId(evt.Connection.Value | SecondaryTag)));
            return count + _scratch.Count;
        }

        public void Shutdown()
        {
            _primary.Shutdown();
            _secondary.Shutdown();
        }

        /// <summary>Both drain within the one budget.</summary>
        public void ShutdownAfterFlush(int timeoutMs)
        {
            var elapsed = Stopwatch.StartNew();
            _primary.ShutdownAfterFlush(timeoutMs);
            int left = timeoutMs - (int)elapsed.ElapsedMilliseconds;
            _secondary.ShutdownAfterFlush(left > 0 ? left : 0);
        }

        public void Dispose() => Shutdown();

        private ITransport Route(ConnectionId outer, out ConnectionId inner)
        {
            if ((outer.Value & SecondaryTag) == 0)
            {
                inner = outer;
                return _primary;
            }
            inner = new ConnectionId(outer.Value & ~SecondaryTag);
            return _secondary;
        }
    }
}
