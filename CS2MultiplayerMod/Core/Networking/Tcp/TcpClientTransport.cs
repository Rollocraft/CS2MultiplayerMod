using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using CS2MultiplayerMod.Core.Diagnostics;

namespace CS2MultiplayerMod.Core.Networking.Tcp
{
    /// <summary>
    /// Connects on a background thread (no DNS/TCP/TLS on the game thread) and exposes the host as
    /// <see cref="ConnectionId.Server"/>, with <see cref="TcpServerTransport"/>'s event model.
    /// </summary>
    public sealed class TcpClientTransport : ITransport, IInboundActivity
    {
        /// <summary>Queued events before the host connection is dropped, if the game thread stops draining.</summary>
        public const int MaxQueuedEvents = TransportEventQueue.Capacity;

        private readonly IModLogger _log;
        private readonly TransportEventQueue _events = new TransportEventQueue();

        private FramedConnection _connection;
        private volatile TcpClient _dialing; // non-null only while ConnectLoop is dialing
        private Thread _connectThread;
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
            _log.Detail(LogTopic.Transport, "Connecting to " + host + ":" + port +
                (useTls ? " (TLS)..." : " (plaintext)..."));

            if (!IPAddress.TryParse(host, out IPAddress literal))
            {
                // DNS as its own step, for a readable failure and the dialed address in the log.
                try
                {
                    IPAddress[] resolved = Dns.GetHostAddresses(host);
                    _log.Detail(LogTopic.Transport, "Resolved '" + host + "' to " +
                        string.Join(", ", Array.ConvertAll(resolved, a => a.ToString())) + ".");
                }
                catch (Exception ex)
                {
                    _log.Warn(LogTopic.Transport, "DNS lookup for '" + host + "' failed: " +
                        ex.Message);
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
                    _log.Detail(LogTopic.Transport, "Join canceled while connecting to " + host +
                        ":" + port + ".");
                    return;
                }
                string errorCode = ex is SocketException socketEx ? " [" + socketEx.SocketErrorCode + "]" : "";
                Enqueue(TransportEvent.Disconnected(ConnectionId.Server,
                    "connect failed" + errorCode + ": " + ex.Message));
                _log.Warn(LogTopic.Transport, "Connect to " + host + ":" + port + " failed after " +
                    elapsed.ElapsedMilliseconds + " ms: " + ex.Message +
                    DescribeConnectFailure(ex));
                return;
            }
            _dialing = null;

            // Shutdown() may have run mid-dial; a leaked socket would keep a connection nothing can close.
            if (!_active)
            {
                try { client.Close(); } catch { /* ignore */ }
                _log.Detail(LogTopic.Transport, "Join canceled while connecting to " + host + ":" +
                    port + ".");
                return;
            }

            string local = "?";
            try { local = client.Client.LocalEndPoint.ToString(); } catch { /* cosmetic only */ }
            _log.Detail(LogTopic.Transport, "TCP connected to " + host + ":" + port + " in " +
                elapsed.ElapsedMilliseconds + " ms (local endpoint " + local + ")" +
                (useTls ? "; starting TLS handshake." : "."));

            var connection = new FramedConnection(ConnectionId.Server, client, null, useTls)
            {
                InboundBudget = _events.Budget,
                // Connected only after TLS, so the handshake never enters a half-open stream.
                OnReady = cid =>
                {
                    Enqueue(TransportEvent.Connected(cid));
                    _log.Event(LogTopic.Transport, "Connected to host " + host + ":" + port +
                        (useTls ? " (TLS)." : " (PLAINTEXT)."));
                },
                OnData = (cid, payload) => Enqueue(TransportEvent.Data(cid, payload)),
                OnClosed = (cid, reason) =>
                {
                    _active = false;
                    _events.EnqueueAlways(TransportEvent.Disconnected(cid, reason));
                },
            };

            _connection = connection;
            // Re-check after publishing: a racing Shutdown() either sees _connection or is caught here;
            // Close is idempotent.
            if (!_active)
            {
                connection.Close("client shutting down");
                _connection = null;
                return;
            }
            connection.Start();
        }

        /// <summary>Drops the connection when the queue is not drained, so memory stays bounded.</summary>
        private void Enqueue(TransportEvent evt)
        {
            if (_events.TryEnqueue(evt)) return;
            _log.Warn(LogTopic.Transport, "Transport event queue full; disconnecting from host.");
            var c = _connection;
            if (c != null) c.Close("event queue overflow");
        }

        /// <summary>The common join-failure socket errors, in player terms.</summary>
        private static string DescribeConnectFailure(Exception ex)
        {
            if (ex is not SocketException socketEx) return "";
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

        public long LastInboundActivityMs(ConnectionId connection)
        {
            var c = _connection;
            return c != null ? c.LastInboundMs : long.MinValue;
        }

        public byte[] GetChannelBinding(ConnectionId connection)
        {
            var c = _connection;
            return c != null ? c.ChannelBinding : Array.Empty<byte>();
        }

        public int Poll(IList<TransportEvent> sink) => _events.Drain(sink);

        public void Shutdown()
        {
            if (!_active && _connection == null) return;
            _active = false;

            // Closing the socket aborts a blocking Connect; ConnectLoop's !_active checks do the rest.
            var dialing = _dialing;
            if (dialing != null) { try { dialing.Close(); } catch { /* ignore */ } }

            var connection = _connection;
            if (connection != null) connection.Close("client shutting down");
            _connection = null;

            _log.Detail(LogTopic.Transport, "Client stopped.");
        }

        public void ShutdownAfterFlush(int timeoutMs)
        {
            var connection = _connection;
            if (connection == null) { Shutdown(); return; }

            _active = false;
            connection.CloseAfterFlush("client left the session");

            // The connection's OnClosed runs once the send thread has drained and hung up.
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            while (connection.PendingSendBytes > 0 && deadline.ElapsedMilliseconds < timeoutMs)
                Thread.Sleep(5);

            connection.Close("client shutting down");
            _connection = null;

            _log.Detail(LogTopic.Transport, "Client stopped.");
        }

        public void Dispose() => Shutdown();
    }
}
