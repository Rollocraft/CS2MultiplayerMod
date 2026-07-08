using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using CS2MultiplayerMod.Core.Diagnostics;

namespace CS2MultiplayerMod.Core.Networking.Tcp
{
    /// <summary>
    /// Client-side transport. Connects to a host on a background thread (so the game
    /// thread never blocks on DNS/TCP/TLS) and exposes the single host connection under
    /// <see cref="ConnectionId.Server"/>. Mirrors <see cref="TcpServerTransport"/>'s event
    /// model so the session layer is role-agnostic.
    /// </summary>
    public sealed class TcpClientTransport : ITransport
    {
        /// <summary>Queued transport events before the host connection is dropped (mirrors the
        /// server-side cap): if the game thread stops draining, memory must not grow unbounded.</summary>
        public const int MaxQueuedEvents = 10000;

        private readonly IModLogger _log;
        private readonly ConcurrentQueue<TransportEvent> _events = new ConcurrentQueue<TransportEvent>();

        private FramedConnection _connection;
        private volatile TcpClient _dialing; // non-null only while ConnectLoop is dialing
        private Thread _connectThread;
        private int _queuedEvents;
        private volatile bool _active;

        public TcpClientTransport(IModLogger log)
        {
            _log = log ?? NullModLogger.Instance;
        }

        public bool IsActive => _active;

        public long PendingSendBytes
        {
            get { var c = _connection; return c != null ? c.PendingSendBytes : 0; }
        }

        /// <summary>Connect, optionally upgrading to TLS (must match the host's setting).</summary>
        public void Connect(string host, int port, bool useTls = true)
        {
            if (_active) throw new InvalidOperationException("Client already started.");
            _active = true;

            _connectThread = new Thread(() => ConnectLoop(host, port, useTls))
            {
                IsBackground = true,
                Name = "mp-connect",
            };
            _connectThread.Start();
        }

        private void ConnectLoop(string host, int port, bool useTls)
        {
            var elapsed = Stopwatch.StartNew();
            _log.Info("Connecting to " + host + ":" + port + (useTls ? " (TLS)..." : " (plaintext)..."));

            IPAddress literal;
            if (!IPAddress.TryParse(host, out literal))
            {
                // Name the DNS step explicitly: when it fails, Connect would report the
                // same root cause less readably; when it succeeds, the log shows which
                // address is actually being dialed.
                try
                {
                    IPAddress[] resolved = Dns.GetHostAddresses(host);
                    _log.Info("Resolved '" + host + "' to " +
                              string.Join(", ", Array.ConvertAll(resolved, a => a.ToString())) + ".");
                }
                catch (Exception ex)
                {
                    _log.Warn("DNS lookup for '" + host + "' failed: " + ex.Message);
                }
            }

            TcpClient client = new TcpClient();
            _dialing = client; // lets Shutdown() abort a dial that is still in flight
            try
            {
                client.Connect(host, port);
            }
            catch (Exception ex)
            {
                _dialing = null;
                bool canceled = !_active; // Shutdown() closed the socket under us
                _active = false;
                try { client.Close(); } catch { /* ignore */ }
                if (canceled)
                {
                    _log.Info("Join canceled while connecting to " + host + ":" + port + ".");
                    return;
                }
                Enqueue(TransportEvent.Disconnected(ConnectionId.Server, "connect failed: " + ex.Message));
                _log.Warn("Connect to " + host + ":" + port + " failed after " + elapsed.ElapsedMilliseconds +
                          " ms: " + ex.Message + DescribeConnectFailure(ex));
                return;
            }
            _dialing = null;

            // Shutdown() may have run while the dial was in flight (the user canceled the
            // join). The socket must not outlive the transport — a leaked read thread
            // would keep a half-alive connection to the host that nothing can ever close.
            if (!_active)
            {
                try { client.Close(); } catch { /* ignore */ }
                _log.Info("Join canceled while connecting to " + host + ":" + port + ".");
                return;
            }

            string local = "?";
            try { local = client.Client.LocalEndPoint.ToString(); } catch { /* cosmetic only */ }
            _log.Info("TCP connected to " + host + ":" + port + " in " + elapsed.ElapsedMilliseconds +
                      " ms (local endpoint " + local + ")" + (useTls ? "; starting TLS handshake." : "."));

            var connection = new FramedConnection(ConnectionId.Server, client, null, useTls)
            {
                // Connected is announced only after the TLS handshake succeeds, so the
                // session never sends the handshake into a half-established stream.
                OnReady = cid =>
                {
                    Enqueue(TransportEvent.Connected(cid));
                    _log.Info("Connected to host " + host + ":" + port + (useTls ? " (TLS)." : " (PLAINTEXT)."));
                },
                OnData = (cid, payload) => Enqueue(TransportEvent.Data(cid, payload)),
                OnClosed = (cid, reason) =>
                {
                    _active = false;
                    // A disconnect is always delivered, even past the cap — it is the one
                    // event the session must never miss.
                    Interlocked.Increment(ref _queuedEvents);
                    _events.Enqueue(TransportEvent.Disconnected(cid, reason));
                },
            };

            _connection = connection;
            // Re-check after publishing: a Shutdown() racing this assignment either sees
            // _connection and closes it, or is caught here. Close is idempotent, so both
            // sides closing is safe.
            if (!_active)
            {
                connection.Close("client shutting down");
                _connection = null;
                return;
            }
            connection.Start();
        }

        /// <summary>
        /// Queue an event for the game thread, dropping the connection if the queue is
        /// not being drained - bounded memory beats a silent balloon when the game
        /// thread stalls or a hostile host floods.
        /// </summary>
        private void Enqueue(TransportEvent evt)
        {
            if (Interlocked.Increment(ref _queuedEvents) > MaxQueuedEvents)
            {
                Interlocked.Decrement(ref _queuedEvents);
                _log.Warn("Transport event queue full; disconnecting from host.");
                var c = _connection;
                if (c != null) c.Close("event queue overflow");
                return;
            }
            _events.Enqueue(evt);
        }

        /// <summary>
        /// Translate the few socket errors that cover practically every failed join
        /// into what they actually mean for a player, so the log answers "why" instead
        /// of just "no".
        /// </summary>
        private static string DescribeConnectFailure(Exception ex)
        {
            var socketEx = ex as SocketException;
            if (socketEx == null) return "";
            switch (socketEx.SocketErrorCode)
            {
                case SocketError.ConnectionRefused:
                    return " [The machine answered but nothing accepts on that port: the host has not " +
                           "started a session, the port is wrong, or the router forwards the port to the wrong device.]";
                case SocketError.TimedOut:
                    return " [No reply at all: wrong address, host offline, the host's router does not forward " +
                           "this TCP port, or a firewall drops it. Note: joining your OWN public IP from inside " +
                           "the same network does not work on most home routers - use the host's LAN IP instead.]";
                case SocketError.HostNotFound:
                case SocketError.NoData:
                    return " [The address could not be resolved to an IP - check for typos.]";
                case SocketError.NetworkUnreachable:
                case SocketError.HostUnreachable:
                    return " [No route to that address - check this machine's own network/internet connection.]";
                default:
                    return " [Socket error: " + socketEx.SocketErrorCode + "]";
            }
        }

        public void Send(ConnectionId target, byte[] payload)
        {
            var connection = _connection;
            if (connection != null) connection.Send(payload);
        }

        public void Disconnect(ConnectionId connection)
        {
            var c = _connection;
            if (c != null) c.Close("disconnected by client");
        }

        public void DisconnectAfterFlush(ConnectionId connection)
        {
            var c = _connection;
            if (c != null) c.CloseAfterFlush("disconnected by client");
        }

        public string GetRemoteAddress(ConnectionId connection)
        {
            var c = _connection;
            return c != null ? c.RemoteAddress : null;
        }

        public byte[] GetChannelBinding(ConnectionId connection)
        {
            var c = _connection;
            return c != null ? c.ChannelBinding : Array.Empty<byte>();
        }

        public int Poll(IList<TransportEvent> sink)
        {
            int count = 0;
            TransportEvent evt;
            while (_events.TryDequeue(out evt))
            {
                Interlocked.Decrement(ref _queuedEvents);
                sink.Add(evt);
                count++;
            }
            return count;
        }

        public void Shutdown()
        {
            if (!_active && _connection == null) return;
            _active = false;

            // Abort a dial that is still in flight: closing the socket makes the blocking
            // Connect throw promptly, and ConnectLoop's !_active checks stop it from
            // standing up a connection nobody owns.
            var dialing = _dialing;
            if (dialing != null) { try { dialing.Close(); } catch { /* ignore */ } }

            var connection = _connection;
            if (connection != null) connection.Close("client shutting down");
            _connection = null;

            _log.Info("Client stopped.");
        }

        public void Dispose() => Shutdown();
    }
}
