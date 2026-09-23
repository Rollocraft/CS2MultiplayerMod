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
    /// One TCP socket as discrete payloads (4-byte little-endian length prefix), optionally over TLS.
    /// A read thread does the handshake and raises <see cref="OnReady"/>, <see cref="OnData"/> and
    /// <see cref="OnClosed"/> (exactly once); a send thread is the only writer, so no caller blocks on
    /// a slow peer. Shared by both TCP transports.
    /// </summary>
    internal sealed class FramedConnection
    {
        public readonly ConnectionId Id;

        /// <summary>Remote IP address (no port), captured at construction. Null if unavailable.</summary>
        public readonly string RemoteAddress;

        private readonly TcpClient _client;
        private readonly X509Certificate2 _serverCertificate; // server-side TLS; null otherwise
        private readonly bool _clientTls;                     // client-side TLS upgrade

        // Written by the send thread only, so a large world send never blocks the game thread.
        // _pendingSendBytes drives "Sending world %".
        private readonly BlockingCollection<byte[]> _sendQueue = new BlockingCollection<byte[]>();
        private long _pendingSendBytes;
        private Thread _sendThread;

        /// <summary>Hard cap on the unsent backlog before a too-slow peer is dropped.</summary>
        private const long MaxPendingSendBytes = 256L * 1024 * 1024;

        // Separate prefix buffers: send and read threads run concurrently.
        private readonly byte[] _sendPrefix = new byte[4];
        private readonly byte[] _readPrefix = new byte[4];
        // Per-type caps checked on the type byte before the body is allocated.
        private static readonly MessageCodec FrameLimits = MessageCodec.CreateDefault();

        private volatile Stream _stream; // set once the connection (incl. TLS) is ready
        private volatile string _gracefulCloseReason; // non-null = close after the queue drains
        private byte[] _channelBinding = Array.Empty<byte>();
        private Thread _readThread;
        private int _closed; // 0 = open, 1 = closed (Interlocked guarded)
        private long _lastInboundMs = long.MinValue;

        /// <summary>Raised on the read thread once the connection is usable (TLS done).</summary>
        public Action<ConnectionId> OnReady;

        /// <summary>Raised on the read thread with a complete payload.</summary>
        public Action<ConnectionId, byte[]> OnData;
        public InboundByteBudget InboundBudget;

        /// <summary>Raised once when the connection ends. Reason is human-readable.</summary>
        public Action<ConnectionId, string> OnClosed;

        /// <summary>SHA-256 of the TLS certificate securing this connection; empty when plaintext.</summary>
        public byte[] ChannelBinding => _channelBinding;

        /// <summary>Bytes queued for sending but not yet written to the socket (the drain backlog).</summary>
        public long PendingSendBytes => Interlocked.Read(ref _pendingSendBytes);

        /// <summary><see cref="MonotonicClock"/> reading of the last bytes read, partial payloads included.</summary>
        public long LastInboundMs => Interlocked.Read(ref _lastInboundMs);

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
                RemoteAddress = client.Client.RemoteEndPoint is IPEndPoint endpoint
                    ? endpoint.Address.ToString()
                    : null;
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

        /// <summary>Queues a payload; the blocking write happens on the send thread.</summary>
        public void Send(byte[] payload)
        {
            if (Volatile.Read(ref _closed) != 0 || payload == null) return;

            long pending = Interlocked.Add(ref _pendingSendBytes, payload.Length + 4L);
            if (pending > MaxPendingSendBytes)
            {
                // A peer that cannot keep up is shed rather than growing host memory.
                Interlocked.Add(ref _pendingSendBytes, -(payload.Length + 4L));
                Close("send backlog exceeded " + (MaxPendingSendBytes >> 20) + " MiB");
                return;
            }

            try { _sendQueue.Add(payload); }
            catch { Interlocked.Add(ref _pendingSendBytes, -(payload.Length + 4L)); } // queue completed: closing
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
                        Interlocked.Add(ref _pendingSendBytes, -(payload.Length + 4L));
                    }
                }

                // Drained: perform a requested graceful close now.
                string graceful = _gracefulCloseReason;
                if (graceful != null) Close(graceful);
            }
            catch (Exception ex)
            {
                Close("send failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Stops new sends and closes after the queue drains; non-blocking, so the close cannot race the
        /// asynchronous send.
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

            // Unblocks the send thread: it drains and exits, or its write throws once the stream closes.
            try { _sendQueue.CompleteAdding(); } catch { /* ignore */ }

            while (_sendQueue.TryTake(out byte[] abandoned))
                Interlocked.Add(ref _pendingSendBytes, -(abandoned.Length + 4L));

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

                // Start the writer before announcing readiness, so an immediate send has a drain running.
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

                    if (length < 1 || length > ProtocolConstants.MaxPayloadBytes)
                    {
                        Close("invalid frame length: " + length);
                        return;
                    }

                    int type = _stream.ReadByte();
                    if (type >= 0) Interlocked.Exchange(ref _lastInboundMs, MonotonicClock.NowMs);
                    if (type < 0 || !FrameLimits.AcceptsFrame((byte)type, length))
                    {
                        Close("invalid message frame size or type");
                        return;
                    }
                    if (InboundBudget != null && !InboundBudget.TryReserve(Id, length))
                    {
                        Close("inbound byte budget exceeded");
                        return;
                    }
                    bool delivered = false;
                    try
                    {
                        var payload = new byte[length];
                        payload[0] = (byte)type;
                        if (length > 1 && !ReadExactly(payload, length, 1))
                        {
                            Close("remote closed mid-frame");
                            return;
                        }
                        var handler = OnData;
                        if (handler != null)
                        {
                            handler(Id, payload);
                            delivered = true;
                        }
                    }
                    finally
                    {
                        if (!delivered) InboundBudget?.Release(Id, length);
                    }
                }
            }
            catch (Exception ex)
            {
                Close("receive failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Plain TCP or TLS 1.2, on the read thread so a slow handshake blocks neither accept loop nor game
        /// thread. The client accepts any certificate and records its hash as channel binding;
        /// authentication is the password proof.
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
        private bool ReadExactly(byte[] buffer, int count, int offset = 0)
        {
            Stream stream = _stream;
            int read = offset;
            while (read < count)
            {
                int n = stream.Read(buffer, read, count - read);
                if (n <= 0) return false;
                Interlocked.Exchange(ref _lastInboundMs, MonotonicClock.NowMs);
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
