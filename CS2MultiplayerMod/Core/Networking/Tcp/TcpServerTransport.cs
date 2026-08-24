using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using CS2MultiplayerMod.Core.Diagnostics;

namespace CS2MultiplayerMod.Core.Networking.Tcp
{
    /// <summary>
    /// Host-side transport. Listens on a port, accepts clients on a background thread,
    /// and tracks each as a <see cref="FramedConnection"/>. Lifecycle and data events
    /// from all connections funnel into one thread-safe queue drained by
    /// <see cref="Poll"/> on the game thread.
    ///
    /// Exposure controls live here: in LAN-only mode connections from non-private
    /// addresses are closed at accept time, the number of sockets that have not yet
    /// completed the handshake is capped, and a bounded event queue means a flooding
    /// client gets disconnected instead of ballooning host memory.
    /// </summary>
    public sealed partial class TcpServerTransport : ITransport
    {
        /// <summary>Sockets allowed to sit in the pre-session (pre-handshake) state at once.</summary>
        public const int MaxPendingConnections = 8;

        /// <summary>Queued transport events before the producing connection is dropped.</summary>
        public const int MaxQueuedEvents = 10000;

        private readonly IModLogger _log;
        private readonly ConcurrentQueue<TransportEvent> _events = new ConcurrentQueue<TransportEvent>();
        private readonly ConcurrentDictionary<int, FramedConnection> _connections =
            new ConcurrentDictionary<int, FramedConnection>();

        private TcpListener _listener;
        private Thread _acceptThread;
        private X509Certificate2 _certificate;
        private bool _lanOnly;
        private int _nextConnectionId = ConnectionId.Server.Value + 1; // 0=None, 1=Server reserved
        private int _queuedEvents;
        private volatile bool _active;

        public TcpServerTransport(IModLogger log)
        {
            _log = log ?? NullModLogger.Instance;
        }

        public bool IsActive => _active;

        public long PendingSendBytes
        {
            get
            {
                long sum = 0;
                foreach (var pair in _connections) sum += pair.Value.PendingSendBytes;
                return sum;
            }
        }

        /// <summary>
        /// Start listening. <paramref name="lanOnly"/> refuses non-private remote
        /// addresses; <paramref name="certificate"/> enables TLS for every connection
        /// (null = plaintext). The certificate is owned by the caller.
        /// </summary>
        public void Start(int port, bool lanOnly = true, X509Certificate2 certificate = null)
        {
            if (_active) throw new InvalidOperationException("Server already started.");

            _lanOnly = lanOnly;
            _certificate = certificate;
            if (Socket.OSSupportsIPv6)
            {
                _listener = new TcpListener(IPAddress.IPv6Any, port);
                try { _listener.Server.DualMode = true; } catch { }
            }
            else
            {
                _listener = new TcpListener(IPAddress.Any, port);
            }
            _listener.Start();
            _active = true;

            _acceptThread = new Thread(AcceptLoop)
            {
                IsBackground = true,
                Name = "mp-accept",
            };
            _acceptThread.Start();

            _log.Info("Host listening on " + _listener.LocalEndpoint + " (" + (lanOnly ? "LAN-only" : "PUBLIC") + ", " +
                      (certificate != null ? "TLS" : "PLAINTEXT") + ").");
            LogReachability(port, lanOnly);
        }


        private void AcceptLoop()
        {
            while (_active)
            {
                TcpClient client;
                try
                {
                    client = _listener.AcceptTcpClient();
                }
                catch (Exception)
                {
                    // Listener stopped during Shutdown, or a transient accept error.
                    if (_active) continue;
                    return;
                }

                if (!Admit(client)) continue;

                string remote = "?";
                try { remote = client.Client.RemoteEndPoint.ToString(); } catch { /* socket already dead */ }

                var id = new ConnectionId(Interlocked.Increment(ref _nextConnectionId));
                _log.Info("Accepted TCP connection " + id + " from " + remote +
                          (_certificate != null ? "; starting TLS handshake." : "."));
                var connection = new FramedConnection(id, client, _certificate)
                {
                    // Connected is announced only once the connection is actually usable
                    // (after the TLS handshake), so the session never talks to a socket
                    // that is still negotiating.
                    OnReady = cid => Enqueue(TransportEvent.Connected(cid), cid),
                    OnData = (cid, payload) => Enqueue(TransportEvent.Data(cid, payload), cid),
                    OnClosed = HandleClosed,
                };

                _connections[id.Value] = connection;
                connection.Start();
            }
        }

        /// <summary>Accept-time policy: LAN filter and pending-connection cap.</summary>
        private bool Admit(TcpClient client)
        {
            IPAddress remote = null;
            try { remote = ((IPEndPoint)client.Client.RemoteEndPoint).Address; }
            catch { /* socket already dead — fall through to close */ }

            if (remote == null)
            {
                try { client.Close(); } catch { }
                return false;
            }

            if (_lanOnly && !IsPrivateAddress(remote))
            {
                _log.Warn("Refused connection from " + remote + ": session is LAN-only.");
                try { client.Close(); } catch { }
                return false;
            }

            if (_connections.Count >= MaxPendingConnections + 16)
            {
                // Coarse global cap: handshaked peers are bounded by the session's player
                // limit, so runaway growth here means a pending-socket flood.
                _log.Warn("Refused connection from " + remote + ": too many open connections.");
                try { client.Close(); } catch { }
                return false;
            }

            return true;
        }


        private void Enqueue(TransportEvent evt, ConnectionId from)
        {
            if (Interlocked.Increment(ref _queuedEvents) > MaxQueuedEvents)
            {
                Interlocked.Decrement(ref _queuedEvents);
                // The game thread is not draining fast enough or someone is flooding;
                // either way, shedding the producer beats unbounded memory growth.
                _log.Warn("Transport event queue full; dropping connection " + from.Value + ".");
                FramedConnection connection;
                if (_connections.TryGetValue(from.Value, out connection))
                    connection.Close("event queue overflow");
                return;
            }
            _events.Enqueue(evt);
        }

        private void HandleClosed(ConnectionId id, string reason)
        {
            FramedConnection removed;
            _connections.TryRemove(id.Value, out removed);
            Interlocked.Increment(ref _queuedEvents);
            _events.Enqueue(TransportEvent.Disconnected(id, reason));
        }

        public void Send(ConnectionId target, byte[] payload)
        {
            FramedConnection connection;
            if (_connections.TryGetValue(target.Value, out connection))
                connection.Send(payload);
        }

        public void Disconnect(ConnectionId connection)
        {
            FramedConnection found;
            if (_connections.TryGetValue(connection.Value, out found))
                found.Close("disconnected by host");
        }

        public void DisconnectAfterFlush(ConnectionId connection)
        {
            FramedConnection found;
            if (_connections.TryGetValue(connection.Value, out found))
                found.CloseAfterFlush("disconnected by host");
        }

        public string GetRemoteAddress(ConnectionId connection)
        {
            FramedConnection found;
            return _connections.TryGetValue(connection.Value, out found) ? found.RemoteAddress : null;
        }

        public byte[] GetChannelBinding(ConnectionId connection)
        {
            FramedConnection found;
            return _connections.TryGetValue(connection.Value, out found)
                ? found.ChannelBinding
                : Array.Empty<byte>();
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
            if (!_active) return;
            _active = false;

            try { _listener.Stop(); } catch { /* ignore */ }

            foreach (var pair in _connections)
                pair.Value.Close("host shutting down");
            _connections.Clear();

            _log.Info("Host stopped.");
        }

        public void ShutdownAfterFlush(int timeoutMs)
        {
            if (!_active) { Shutdown(); return; }
            _active = false;

            // Stop admitting new clients first: an accept completing mid-drain would add a
            // connection nobody waits for.
            try { _listener.Stop(); } catch { /* ignore */ }

            foreach (var pair in _connections)
                pair.Value.CloseAfterFlush("host left the session");

            // Each connection removes itself from the map once its send thread has drained
            // and closed, so an empty map is the accurate "everything went out" signal.
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            while (!_connections.IsEmpty && deadline.ElapsedMilliseconds < timeoutMs)
                Thread.Sleep(5);

            if (!_connections.IsEmpty)
                _log.Warn("Host stopping with " + _connections.Count +
                          " connection(s) still draining after " + timeoutMs + " ms; closing them now.");

            foreach (var pair in _connections)
                pair.Value.Close("host shutting down");
            _connections.Clear();

            _log.Info("Host stopped.");
        }

        public void Dispose() => Shutdown();
    }
}
