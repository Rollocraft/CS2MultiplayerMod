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
            string target = NetAddress.FormatEndpoint(host, port);
            _log.Detail(LogTopic.Transport, "Connecting to " + target +
                (useTls ? " (TLS)..." : " (plaintext)..."));

            IPAddress[] addresses;
            try
            {
                addresses = Resolve(host);
            }
            catch (Exception ex)
            {
                _log.Warn(LogTopic.Transport, "DNS lookup for '" + host + "' failed: " + ex.Message);
                EndFailedDial(target, ex, false, elapsed);
                return;
            }

            // Each address on a socket of its own family, in the resolver's order: a default TcpClient is
            // IPv4-only and cannot reach an IPv6 host at all.
            TcpClient client = null;
            IPAddress dialed = null;
            Exception lastError = null;
            for (int i = 0; i < addresses.Length && _active; i++)
            {
                dialed = addresses[i];
                TcpClient attempt = null;
                try
                {
                    attempt = new TcpClient(dialed.AddressFamily);
                    _dialing = attempt; // lets Shutdown() abort a dial that is still in flight
                    attempt.Connect(new IPEndPoint(dialed, port));
                    client = attempt;
                    break;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    if (attempt != null) { try { attempt.Close(); } catch { /* ignore */ } }
                    if (_active && i + 1 < addresses.Length)
                        _log.Detail(LogTopic.Transport, "Connect to " +
                            NetAddress.FormatEndpoint(dialed.ToString(), port) + " failed (" + ex.Message +
                            "); trying " + addresses[i + 1] + " next.");
                }
            }
            _dialing = null;

            if (client == null)
            {
                EndFailedDial(target, lastError ?? new SocketException((int)SocketError.HostNotFound),
                    dialed != null && dialed.AddressFamily == AddressFamily.InterNetworkV6, elapsed);
                return;
            }

            // Shutdown() may have run mid-dial; a leaked socket would keep a connection nothing can close.
            if (!_active)
            {
                try { client.Close(); } catch { /* ignore */ }
                _log.Detail(LogTopic.Transport, "Join canceled while connecting to " + target + ".");
                return;
            }

            string local = "?";
            try { local = client.Client.LocalEndPoint.ToString(); } catch { /* cosmetic only */ }
            _log.Detail(LogTopic.Transport, "TCP connected to " + target +
                (dialed.ToString() == host ? "" : " (" + dialed + ")") + " in " +
                elapsed.ElapsedMilliseconds + " ms (local endpoint " + local + ")" +
                (useTls ? "; starting TLS handshake." : "."));

            var connection = new FramedConnection(ConnectionId.Server, client, null, useTls)
            {
                InboundBudget = _events.Budget,
                // Connected only after TLS, so the handshake never enters a half-open stream.
                OnReady = cid =>
                {
                    Enqueue(TransportEvent.Connected(cid));
                    _log.Event(LogTopic.Transport, "Connected to host " + target +
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

        /// <summary>An IP literal is dialed as is; a name is looked up as its own step, for a readable failure.</summary>
        private IPAddress[] Resolve(string host)
        {
            if (IPAddress.TryParse(host, out IPAddress literal)) return new[] { NetAddress.Normalize(literal) };

            IPAddress[] resolved = Dns.GetHostAddresses(host);
            _log.Detail(LogTopic.Transport, "Resolved '" + host + "' to " +
                string.Join(", ", Array.ConvertAll(resolved, a => a.ToString())) + ".");
            return resolved;
        }

        /// <summary>Ends a dial that never connected: quietly if Shutdown() canceled it, otherwise as a failure.</summary>
        private void EndFailedDial(string target, Exception ex, bool ipv6, Stopwatch elapsed)
        {
            bool canceled = !_active; // Shutdown() closed the socket under us
            _active = false;
            if (canceled)
            {
                _log.Detail(LogTopic.Transport, "Join canceled while connecting to " + target + ".");
                return;
            }

            string errorCode = ex is SocketException socketEx ? " [" + socketEx.SocketErrorCode + "]" : "";
            Enqueue(TransportEvent.Disconnected(ConnectionId.Server,
                "connect failed" + errorCode + ": " + ex.Message));
            _log.Warn(LogTopic.Transport, "Connect to " + target + " failed after " +
                elapsed.ElapsedMilliseconds + " ms: " + ex.Message + DescribeConnectFailure(ex, ipv6));
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
        private static string DescribeConnectFailure(Exception ex, bool ipv6)
        {
            if (ex is not SocketException socketEx) return "";
            switch (socketEx.SocketErrorCode)
            {
                case SocketError.ConnectionRefused:
                    return " [The machine answered but nothing accepts on that port: the host has not " +
                           "started a session, the port is wrong, or the router forwards the port to the wrong device.]";
                case SocketError.TimedOut:
                    return ipv6
                        ? " [No reply at all over IPv6: wrong address, host offline, or a firewall drops this TCP port. " +
                          "IPv6 needs no port forwarding, but the host's router must allow incoming IPv6 connections " +
                          "on this port (many block them by default), and so must the host's Windows Firewall.]"
                        : " [No reply at all: wrong address, host offline, the host's router does not forward " +
                          "this TCP port, or a firewall drops it. Note: joining your OWN public IP from inside " +
                          "the same network does not work on most home routers - use the host's LAN IP instead.]";
                case SocketError.HostNotFound:
                case SocketError.NoData:
                    return " [The address could not be resolved to an IP - check for typos.]";
                case SocketError.NetworkUnreachable:
                case SocketError.HostUnreachable:
                    return ipv6
                        ? " [No route to that IPv6 address - this machine's internet connection may have no IPv6 " +
                          "(test it at test-ipv6.com), or the host's router turned the connection away. Try the host's " +
                          "IPv4 address or the Steam relay instead.]"
                        : " [No route to that address - check this machine's own network/internet connection.]";
                case SocketError.AddressFamilyNotSupported:
                    return " [IPv6 is switched off on this machine - use the host's IPv4 address or the Steam relay instead.]";
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
