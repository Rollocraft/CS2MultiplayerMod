using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using CS2MultiplayerMod.Core.Protocol;

namespace CS2MultiplayerMod.Core.Networking.Tcp
{
    /// <summary>
    /// Wraps a single TCP socket and turns its byte stream into discrete payloads,
    /// optionally upgrading to TLS first.
    ///
    /// Wire framing is a 4-byte little-endian length prefix followed by that many
    /// payload bytes. A dedicated background thread performs the TLS handshake (if
    /// enabled), raises <see cref="OnReady"/>, then blocks on reads and raises
    /// <see cref="OnData"/>/<see cref="OnClosed"/>; writes are queued to a dedicated
    /// send thread (the sole socket writer), so any thread can send without blocking
    /// behind a slow peer. Closing is idempotent and always raises
    /// <see cref="OnClosed"/> exactly once.
    ///
    /// Shared by both <see cref="TcpServerTransport"/> and <see cref="TcpClientTransport"/>
    /// so the framing logic lives in one place.
    /// </summary>
    internal sealed class FramedConnection
    {
        public readonly ConnectionId Id;

        /// <summary>Remote IP address (no port), captured at construction. Null if unavailable.</summary>
        public readonly string RemoteAddress;

        private readonly TcpClient _client;
        private readonly X509Certificate2 _serverCertificate; // server-side TLS; null otherwise
        private readonly bool _clientTls;                     // client-side TLS upgrade

        // Outgoing payloads are queued and written by a dedicated send thread, so the game
        // thread never blocks on a slow/backpressured socket. A 50 MB world send to a slow
        // client used to block the main thread for ~30 s — long enough that Windows reports
        // the game as "Not Responding". The send thread is the sole writer (no write lock),
        // and _pendingSendBytes (the unsent backlog) drives the host's "Sending world %".
        private readonly BlockingCollection<byte[]> _sendQueue = new BlockingCollection<byte[]>();
        private long _pendingSendBytes;
        private Thread _sendThread;

        /// <summary>Hard cap on the unsent backlog before a too-slow peer is dropped.</summary>
        private const long MaxPendingSendBytes = 256L * 1024 * 1024;

        // Separate prefix buffers: the send thread and read thread run concurrently, so a
        // shared buffer would let an incoming frame's length overwrite an outgoing one.
        private readonly byte[] _sendPrefix = new byte[4];
        private readonly byte[] _readPrefix = new byte[4];

        private volatile Stream _stream; // set once the connection (incl. TLS) is ready
        private volatile string _gracefulCloseReason; // non-null = close after the queue drains
        private byte[] _channelBinding = Array.Empty<byte>();
        private Thread _readThread;
        private int _closed; // 0 = open, 1 = closed (Interlocked guarded)

        /// <summary>Raised on the read thread once the connection is usable (TLS done).</summary>
        public Action<ConnectionId> OnReady;

        /// <summary>Raised on the read thread with a complete payload.</summary>
        public Action<ConnectionId, byte[]> OnData;

        /// <summary>Raised once when the connection ends. Reason is human-readable.</summary>
        public Action<ConnectionId, string> OnClosed;

        /// <summary>SHA-256 of the TLS certificate securing this connection; empty when plaintext.</summary>
        public byte[] ChannelBinding => _channelBinding;

        /// <summary>Bytes queued for sending but not yet written to the socket (the drain backlog).</summary>
        public long PendingSendBytes => Interlocked.Read(ref _pendingSendBytes);

        public FramedConnection(ConnectionId id, TcpClient client,
                                X509Certificate2 serverCertificate = null, bool clientTls = false)
        {
            Id = id;
            _client = client;
            _serverCertificate = serverCertificate;
            _clientTls = clientTls;
            _client.NoDelay = true; // low latency matters more than packing for a co-op session

            try
            {
                var endpoint = client.Client.RemoteEndPoint as IPEndPoint;
                RemoteAddress = endpoint != null ? endpoint.Address.ToString() : null;
            }
            catch { RemoteAddress = null; }
        }

        public void Start()
        {
            _readThread = new Thread(ReadLoop)
            {
                IsBackground = true,
                Name = "mp-recv-" + Id.Value,
            };
            _readThread.Start();
        }

        /// <summary>
        /// Queue a payload for sending. Non-blocking: the actual (blocking) socket write
        /// happens on the send thread, so the game thread never stalls behind a slow peer.
        /// </summary>
        public void Send(byte[] payload)
        {
            if (Volatile.Read(ref _closed) != 0 || payload == null) return;

            long pending = Interlocked.Add(ref _pendingSendBytes, payload.Length);
            if (pending > MaxPendingSendBytes)
            {
                // The peer cannot keep up (or is stalling). Shedding it beats letting the
                // host's memory grow without bound.
                Interlocked.Add(ref _pendingSendBytes, -payload.Length);
                Close("send backlog exceeded " + (MaxPendingSendBytes >> 20) + " MiB");
                return;
            }

            try { _sendQueue.Add(payload); }
            catch { Interlocked.Add(ref _pendingSendBytes, -payload.Length); } // queue completed: closing
        }

        /// <summary>Drains the send queue, doing the blocking socket writes off the game thread.</summary>
        private void SendLoop()
        {
            try
            {
                foreach (byte[] payload in _sendQueue.GetConsumingEnumerable())
                {
                    try
                    {
                        Stream stream = _stream;
                        if (stream == null) continue; // closed before the stream was ready
                        WriteLength(payload.Length);
                        stream.Write(_sendPrefix, 0, 4);
                        stream.Write(payload, 0, payload.Length);
                        stream.Flush();
                    }
                    finally
                    {
                        Interlocked.Add(ref _pendingSendBytes, -payload.Length);
                    }
                }

                // The queue completed and fully drained. If a graceful close was requested
                // (deliver a final message, e.g. a rejection reason, then hang up), do it now.
                string graceful = _gracefulCloseReason;
                if (graceful != null) Close(graceful);
            }
            catch (Exception ex)
            {
                Close("send failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Stop accepting new sends and close once everything already queued has gone out -
        /// used to deliver a final message (e.g. a handshake rejection reason) before
        /// hanging up. Non-blocking: the send thread performs the close after it drains, so
        /// an immediate close cannot race the (asynchronous) send.
        /// </summary>
        public void CloseAfterFlush(string reason)
        {
            if (Volatile.Read(ref _closed) != 0) return;
            _gracefulCloseReason = reason ?? "closed";
            try { _sendQueue.CompleteAdding(); } catch { /* already completing/closed */ }
        }

        public void Close(string reason)
        {
            // Ensure OnClosed fires exactly once even under concurrent close attempts.
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;

            // Unblock the send thread (it either drains and exits, or its current write
            // throws once the stream below is closed).
            try { _sendQueue.CompleteAdding(); } catch { /* ignore */ }

            var stream = _stream;
            if (stream != null) { try { stream.Close(); } catch { /* ignore */ } }
            try { _client.Close(); } catch { /* ignore */ }

            var handler = OnClosed;
            if (handler != null) handler(Id, reason);
        }

        private void ReadLoop()
        {
            try
            {
                if (!Upgrade()) return;

                // The stream (incl. TLS) is ready — stand up the writer before announcing
                // readiness, so a payload sent the instant the session sees Connected has a
                // drain already running.
                _sendThread = new Thread(SendLoop) { IsBackground = true, Name = "mp-send-" + Id.Value };
                _sendThread.Start();

                var ready = OnReady;
                if (ready != null) ready(Id);

                while (Volatile.Read(ref _closed) == 0)
                {
                    if (!ReadExactly(_readPrefix, 4))
                    {
                        Close("remote closed");
                        return;
                    }

                    int length = _readPrefix[0]
                                 | (_readPrefix[1] << 8)
                                 | (_readPrefix[2] << 16)
                                 | (_readPrefix[3] << 24);

                    if (length < 0 || length > ProtocolConstants.MaxPayloadBytes)
                    {
                        Close("invalid frame length: " + length);
                        return;
                    }

                    var payload = new byte[length];
                    if (length > 0 && !ReadExactly(payload, length))
                    {
                        Close("remote closed mid-frame");
                        return;
                    }

                    var handler = OnData;
                    if (handler != null) handler(Id, payload);
                }
            }
            catch (Exception ex)
            {
                Close("receive failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Establish the application stream: plain TCP, or TLS 1.2 when configured.
        /// Runs on the read thread so a slow/hostile TLS handshake never blocks the
        /// accept loop or the game thread. The server presents its ephemeral
        /// certificate; the client accepts any certificate but records its hash as the
        /// channel binding - authentication comes from the password proof, not a CA.
        /// </summary>
        private bool Upgrade()
        {
            NetworkStream raw = _client.GetStream();

            if (_serverCertificate != null)
            {
                raw.ReadTimeout = 15000; // a peer that stalls the TLS handshake gets dropped
                var ssl = new SslStream(raw, false);
                ssl.AuthenticateAsServer(_serverCertificate, false, SslProtocols.Tls12, false);
                raw.ReadTimeout = Timeout.Infinite;
                _channelBinding = TlsCertificate.HashOf(_serverCertificate);
                _stream = ssl;
                return true;
            }

            if (_clientTls)
            {
                var ssl = new SslStream(raw, false, (sender, cert, chain, errors) =>
                {
                    _channelBinding = TlsCertificate.HashOf(cert);
                    return true; // trust is established by the password proof over this hash
                });
                ssl.AuthenticateAsClient("CS2MultiplayerMod", null, SslProtocols.Tls12, false);
                _stream = ssl;
                return true;
            }

            _stream = raw;
            return true;
        }

        /// <summary>Read exactly <paramref name="count"/> bytes; false on clean EOF.</summary>
        private bool ReadExactly(byte[] buffer, int count)
        {
            Stream stream = _stream;
            int read = 0;
            while (read < count)
            {
                int n = stream.Read(buffer, read, count - read);
                if (n <= 0) return false;
                read += n;
            }
            return true;
        }

        private void WriteLength(int length)
        {
            _sendPrefix[0] = (byte)(length & 0xFF);
            _sendPrefix[1] = (byte)((length >> 8) & 0xFF);
            _sendPrefix[2] = (byte)((length >> 16) & 0xFF);
            _sendPrefix[3] = (byte)((length >> 24) & 0xFF);
        }
    }
}
