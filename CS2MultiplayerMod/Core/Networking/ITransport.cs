using System;
using System.Collections.Generic;

namespace CS2MultiplayerMod.Core.Networking
{
    /// <summary>Platform transports only, for conveniences like auto-approving a friend.</summary>
    public interface IPlatformFriendLookup
    {
        bool IsPlatformFriend(ConnectionId connection);
    }

    /// <summary>
    /// When traffic last arrived, whole payload or not. A reliable stream stalled behind a lost segment
    /// can deliver no payload for longer than the silence timeout while the peer is alive.
    /// </summary>
    public interface IInboundActivity
    {
        /// <summary>Last inbound traffic on <paramref name="connection"/>, or <see cref="long.MinValue"/>.</summary>
        long LastInboundActivityMs(ConnectionId connection);
    }

    /// <summary>
    /// Reliable, ordered, message-oriented transport for both roles (a client's one connection is
    /// <see cref="ConnectionId.Server"/>). I/O runs on background threads; the owner drains
    /// <see cref="TransportEvent"/>s on the game thread through <see cref="Poll"/>.
    /// </summary>
    public interface ITransport : IDisposable
    {
        /// <summary>True once started and not yet shut down.</summary>
        bool IsActive { get; }

        /// <summary>Unsent bytes across all connections; drives "Sending world %".</summary>
        long PendingSendBytes { get; }

        /// <summary>Queues a payload; safe from the game thread. Unknown or closed targets are a no-op.</summary>
        void Send(ConnectionId target, byte[] payload);

        /// <summary>Forcibly close a single connection now, abandoning any unsent backlog.</summary>
        void Disconnect(ConnectionId connection);

        /// <summary>Closes once queued payloads are sent, so a final message (e.g. a rejection) arrives.</summary>
        void DisconnectAfterFlush(ConnectionId connection);

        /// <summary>Moves pending events into <paramref name="sink"/>; call regularly from the game thread.</summary>
        int Poll(IList<TransportEvent> sink);

        /// <summary>Remote IP (no port) or null; for bans and logs, never for trust.</summary>
        string GetRemoteAddress(ConnectionId connection);

        /// <summary>
        /// SHA-256 of the TLS certificate as this side saw it, or empty without TLS. Folded into the
        /// password proof, it exposes a man-in-the-middle.
        /// </summary>
        byte[] GetChannelBinding(ConnectionId connection);

        /// <summary>Stop all I/O and release sockets. Idempotent.</summary>
        void Shutdown();

        /// <summary>
        /// Closes every connection after its queue drains, forcing the rest after
        /// <paramref name="timeoutMs"/>. Blocks, so keep it short; for a final notice on exit, which
        /// <see cref="Shutdown"/> would abandon.
        /// </summary>
        void ShutdownAfterFlush(int timeoutMs);
    }
}
