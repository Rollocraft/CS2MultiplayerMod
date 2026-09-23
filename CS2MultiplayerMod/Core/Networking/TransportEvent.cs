namespace CS2MultiplayerMod.Core.Networking
{
    public enum TransportEventType
    {
        /// <summary>A new connection was established (host: a client joined; client: connected to host).</summary>
        Connected,

        /// <summary>A connection was closed, by either side or due to an error.</summary>
        Disconnected,

        /// <summary>A complete application payload arrived on a connection.</summary>
        Data,
    }

    /// <summary>
    /// A transport occurrence, produced on I/O threads and consumed on the game thread via
    /// <see cref="ITransport.Poll"/>; no game state is touched from an I/O thread.
    /// </summary>
    public readonly struct TransportEvent
    {
        public readonly TransportEventType Type;
        public readonly ConnectionId Connection;

        /// <summary>For <see cref="TransportEventType.Data"/>: the payload bytes (owned by the consumer). Otherwise null.</summary>
        public readonly byte[] Payload;

        /// <summary>Optional human-readable reason, e.g. a disconnect cause. May be null.</summary>
        public readonly string Detail;

        /// <summary>
        /// Stamped on the I/O thread at arrival: budgets measure the wire, not a stalled frame.
        /// </summary>
        public readonly long ReceivedAtMs;

        private TransportEvent(TransportEventType type, ConnectionId connection, byte[] payload,
            string detail, long receivedAtMs)
        {
            Type = type;
            Connection = connection;
            Payload = payload;
            Detail = detail;
            ReceivedAtMs = receivedAtMs;
        }

        public static TransportEvent Connected(ConnectionId connection) =>
            new TransportEvent(TransportEventType.Connected, connection, null, null, MonotonicClock.NowMs);

        public static TransportEvent Disconnected(ConnectionId connection, string detail) =>
            new TransportEvent(TransportEventType.Disconnected, connection, null, detail, MonotonicClock.NowMs);

        public static TransportEvent Data(ConnectionId connection, byte[] payload) =>
            new TransportEvent(TransportEventType.Data, connection, payload, null, MonotonicClock.NowMs);

        /// <summary>For a transport whose underlying stack supplies the arrival time.</summary>
        public static TransportEvent Data(ConnectionId connection, byte[] payload, long receivedAtMs) =>
            new TransportEvent(TransportEventType.Data, connection, payload, null, receivedAtMs);
    }
}
