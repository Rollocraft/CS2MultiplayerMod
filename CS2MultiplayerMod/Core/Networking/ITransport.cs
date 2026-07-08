using System;
using System.Collections.Generic;

namespace CS2MultiplayerMod.Core.Networking
{
    /// <summary>
    /// Reliable, ordered, message-oriented transport.
    ///
    /// Implementations deliver whole application payloads (the wire framing is an
    /// implementation detail) and surface connection lifecycle as
    /// <see cref="TransportEvent"/>s. All I/O happens on background threads; the
    /// owner drains events on the game thread through <see cref="Poll"/>.
    ///
    /// The same interface serves both roles. A host accepts many connections; a
    /// client holds a single connection addressed by <see cref="ConnectionId.Server"/>.
    /// Keeping a single abstraction lets the session layer treat host and client
    /// uniformly and makes the TCP implementation swappable for UDP later.
    /// </summary>
    public interface ITransport : IDisposable
    {
        /// <summary>True once started and not yet shut down.</summary>
        bool IsActive { get; }

        /// <summary>
        /// Total bytes queued for sending across all connections that have not yet been
        /// written to their sockets. Drives the host's "Sending world %" progress while a
        /// large blob drains to a peer.
        /// </summary>
        long PendingSendBytes { get; }

        /// <summary>
        /// Queue a payload for reliable delivery to <paramref name="target"/>.
        /// Safe to call from the game thread; the transport buffers and sends on its
        /// own threads. Sending to an unknown/closed connection is a no-op.
        /// </summary>
        void Send(ConnectionId target, byte[] payload);

        /// <summary>Forcibly close a single connection now, abandoning any unsent backlog.</summary>
        void Disconnect(ConnectionId connection);

        /// <summary>
        /// Close connection after sending queued payloads - delivers final message
        /// (e.g. handshake rejection reason) before hanging up without racing
        /// asynchronous send. See <see cref="Disconnect"/>.
        /// </summary>
        void DisconnectAfterFlush(ConnectionId connection);

        /// <summary>
        /// Move all pending events into <paramref name="sink"/> and return the count
        /// added. Must be called regularly from the game thread.
        /// </summary>
        int Poll(IList<TransportEvent> sink);

        /// <summary>
        /// Remote IP address of a connection (no port), or null if unknown.
        /// Used for ban tracking and logging - never for trust decisions.
        /// </summary>
        string GetRemoteAddress(ConnectionId connection);

        /// <summary>
        /// Channel-binding token for a connection: the SHA-256 hash of the TLS
        /// certificate securing it, as this side saw it. Empty when the connection is
        /// not encrypted. Folding this into the password proof makes a TLS
        /// man-in-the-middle detectable whenever a password is set.
        /// </summary>
        byte[] GetChannelBinding(ConnectionId connection);

        /// <summary>Stop all I/O and release sockets. Idempotent.</summary>
        void Shutdown();
    }
}
