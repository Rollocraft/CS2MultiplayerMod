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
    /// Listens and accepts on a background thread; every connection's events funnel into one queue
    /// drained by <see cref="Poll"/>. Exposure controls: LAN-only refuses non-private addresses at
    /// accept, pre-handshake sockets are capped, and a flooding client overflows its queue and is dropped.
    /// </summary>
    public sealed partial class TcpServerTransport : ITransport, IInboundActivity
    {
        /// <summary>Sockets allowed to sit in the pre-session (pre-handshake) state at once.</summary>
        public const int MaxPendingConnections = 8;

        /// <summary>Queued transport events before the producing connection is dropped.</summary>
        public const int MaxQueuedEvents = TransportEventQueue.Capacity;

        private readonly IModLogger _log;
        private readonly TransportEventQueue _events = new TransportEventQueue();
        private readonly ConcurrentDictionary<int, FramedConnection> _connections =
            new ConcurrentDictionary<int, FramedConnection>();

        private TcpListener _listener;
        private Thread _acceptThread;
        private X509Certificate2 _certificate;
        private bool _lanOnly;
        private int _nextConnectionId = ConnectionId.Server.Value + 1; // 0=None, 1=Server reserved
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
        /// <paramref name="lanOnly"/> refuses non-private addresses; <paramref name="certificate"/> (caller
        /// owned) enables TLS, null is plaintext.
        /// </summary>
        public void Start(int port, bool lanOnly = true, X509Certificate2 certificate = null)
        {
            if (_active) throw new InvalidOperationException("Server already started.");

            _lanOnly = lanOnly;
            _certificate = certificate;
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();
            _active = true;

            _acceptThread = new Thread(AcceptLoop)
            {
                IsBackground = true,
                Name = "mp-accept",
            };
            _acceptThread.Start();

            _log.Event(LogTopic.Transport, "Host listening on " + _listener.LocalEndpoint + " (" +
                (lanOnly ? "LAN-only" : "PUBLIC") + ", " +
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
                _log.Detail(LogTopic.Transport, "Accepted TCP connection " + id + " from " + remote +
                    (_certificate != null ? "; starting TLS handshake." : "."));
                var connection = new FramedConnection(id, client, _certificate)
                {
                    InboundBudget = _events.Budget,
                    // Connected only after TLS.
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
                _log.Warn(LogTopic.Transport, "Refused connection from " + remote +
                    ": session is LAN-only.");
                try { client.Close(); } catch { }
                return false;
            }

            if (_events.Count >= MaxQueuedEvents ||
                _connections.Count >= MaxPendingConnections + 16)
            {
                // Handshaked peers are bounded by the player limit; growth here is a pending-socket flood.
                _log.Warn(LogTopic.Transport, "Refused connection from " + remote +
                    ": too many open connections.");
                try { client.Close(); } catch { }
                return false;
            }

            return true;
        }

        private void Enqueue(TransportEvent evt, ConnectionId from)
        {
            if (_events.TryEnqueue(evt)) return;
            // Undrained or flooding: shed the producer.
            _log.Warn(LogTopic.Transport, "Transport event queue full; dropping connection " + from.Value + ".");
            if (_connections.TryGetValue(from.Value, out FramedConnection connection))
                connection.Close("event queue overflow");
        }

        private void HandleClosed(ConnectionId id, string reason)
        {
            _connections.TryRemove(id.Value, out FramedConnection removed);
            _events.EnqueueAlways(TransportEvent.Disconnected(id, reason));
        }

        public void Send(ConnectionId target, byte[] payload)
        {
            if (_connections.TryGetValue(target.Value, out FramedConnection connection))
                connection.Send(payload);
        }

        public void Disconnect(ConnectionId connection)
        {
            if (_connections.TryGetValue(connection.Value, out FramedConnection found))
                found.Close("disconnected by host");
        }

        public void DisconnectAfterFlush(ConnectionId connection)
        {
            if (_connections.TryGetValue(connection.Value, out FramedConnection found))
                found.CloseAfterFlush("disconnected by host");
        }

        public string GetRemoteAddress(ConnectionId connection) =>
            _connections.TryGetValue(connection.Value, out FramedConnection found) ? found.RemoteAddress : null;

        public long LastInboundActivityMs(ConnectionId connection) =>
            _connections.TryGetValue(connection.Value, out FramedConnection found) ? found.LastInboundMs : long.MinValue;

        public byte[] GetChannelBinding(ConnectionId connection)
        {
            return _connections.TryGetValue(connection.Value, out FramedConnection found)
                ? found.ChannelBinding
                : Array.Empty<byte>();
        }

        public int Poll(IList<TransportEvent> sink) => _events.Drain(sink);

        public void Shutdown()
        {
            if (!_active) return;
            _active = false;

            try { _listener.Stop(); } catch { /* ignore */ }

            foreach (var pair in _connections)
                pair.Value.Close("host shutting down");
            _connections.Clear();

            _log.Detail(LogTopic.Transport, "Host stopped.");
        }

        public void ShutdownAfterFlush(int timeoutMs)
        {
            if (!_active) { Shutdown(); return; }
            _active = false;

            // Stop accepting first, or a mid-drain accept adds a connection nobody waits for.
            try { _listener.Stop(); } catch { /* ignore */ }

            foreach (var pair in _connections)
                pair.Value.CloseAfterFlush("host left the session");

            // Connections leave the map once drained and closed, so empty means everything went out.
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            while (!_connections.IsEmpty && deadline.ElapsedMilliseconds < timeoutMs)
                Thread.Sleep(5);

            if (!_connections.IsEmpty)
                _log.Warn(LogTopic.Transport, "Host stopping with " + _connections.Count +
                    " connection(s) still draining after " + timeoutMs + " ms; closing them now.");

            foreach (var pair in _connections)
                pair.Value.Close("host shutting down");
            _connections.Clear();

            _log.Detail(LogTopic.Transport, "Host stopped.");
        }

        public void Dispose() => Shutdown();
    }
}
