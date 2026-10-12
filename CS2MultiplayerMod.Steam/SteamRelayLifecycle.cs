using System;
using System.Collections.Concurrent;
using System.Threading;
using CS2MultiplayerMod.Core.Diagnostics;
using Steamworks;

namespace CS2MultiplayerMod.Core.Networking.Steam
{
    // Disconnect and shutdown (including flush-then-close) and the per-peer Endpoint.
    public sealed partial class SteamRelayTransport
    {
        // ---- teardown -------------------------------------------------------------

        public void Disconnect(ConnectionId connection)
        {
            Endpoint endpoint;
            lock (_gate)
            {
                if (!_byId.TryGetValue(connection.Value, out endpoint)) return;
            }
            Close(endpoint, LocalCloseReason, linger: false);
        }

        public void DisconnectAfterFlush(ConnectionId connection)
        {
            Endpoint endpoint;
            lock (_gate)
            {
                if (!_byId.TryGetValue(connection.Value, out endpoint)) return;
            }
            // Linger lets Steam drain the queue, carrying a rejection reason out.
            Close(endpoint, LocalCloseReason, linger: true);
        }

        // Steam hands this text to the other side as the close reason.
        private string LocalCloseReason => _isHost ? "disconnected by host" : "disconnected by client";

        public string GetRemoteAddress(ConnectionId connection)
        {
            Endpoint endpoint;
            lock (_gate)
            {
                if (!_byId.TryGetValue(connection.Value, out endpoint)) return null;
            }
            return endpoint.RemoteAddress;
        }

        public long LastInboundActivityMs(ConnectionId connection)
        {
            Endpoint endpoint;
            lock (_gate)
            {
                if (!_byId.TryGetValue(connection.Value, out endpoint)) return long.MinValue;
            }
            return endpoint.LastInboundMs;
        }

        /// <summary>
        /// Steam authenticated the remote identity, so this is safe for approval rules. A missing endpoint or
        /// friends API is never trusted.
        /// </summary>
        public bool IsPlatformFriend(ConnectionId connection)
        {
            Endpoint endpoint;
            lock (_gate)
            {
                if (!_byId.TryGetValue(connection.Value, out endpoint)) return false;
            }

            try
            {
                return endpoint.SteamId != 0 && SteamFriends.HasFriend(
                    new CSteamID(endpoint.SteamId), EFriendFlags.k_EFriendFlagImmediate);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Empty: the relay encrypts itself, so there is no certificate to bind.</summary>
        public byte[] GetChannelBinding(ConnectionId connection) => Array.Empty<byte>();

        public void Shutdown()
        {
            if (!_active) return;
            _active = false;

            Endpoint[] open;
            lock (_gate)
            {
                open = new Endpoint[_byId.Count];
                _byId.Values.CopyTo(open, 0);
                _byId.Clear();
                _byHandle.Clear();
            }

            foreach (Endpoint endpoint in open)
            {
                try { SteamNetworkingSockets.CloseConnection(endpoint.Handle, 0, "shutting down", false); }
                catch (Exception) { /* already gone */ }
            }

            if (_pollGroup != HSteamNetPollGroup.Invalid)
            {
                try { SteamNetworkingSockets.DestroyPollGroup(_pollGroup); } catch (Exception) { }
                _pollGroup = HSteamNetPollGroup.Invalid;
            }

            if (_listenSocket != HSteamListenSocket.Invalid)
            {
                try { SteamNetworkingSockets.CloseListenSocket(_listenSocket); } catch (Exception) { }
                _listenSocket = HSteamListenSocket.Invalid;
            }

            if (_statusCallback != null)
            {
                try { _statusCallback.Dispose(); } catch (Exception) { }
                _statusCallback = null;
            }

            _log.Detail(LogTopic.Transport, "Steam relay transport stopped.");
        }

        public void ShutdownAfterFlush(int timeoutMs)
        {
            if (!_active) { Shutdown(); return; }

            Endpoint[] open;
            lock (_gate)
            {
                open = new Endpoint[_byId.Count];
                _byId.Values.CopyTo(open, 0);
            }

            foreach (Endpoint endpoint in open)
            {
                try { SteamNetworkingSockets.FlushMessagesOnConnection(endpoint.Handle); }
                catch (Exception) { /* already gone */ }
            }

            // Steam drains its share on its own thread, but the outbox moves only when pumped. Bounded: game thread.
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            while (PendingSendBytes > 0 && deadline.ElapsedMilliseconds < timeoutMs)
            {
                PumpAllSends();
                Thread.Sleep(5);
            }

            long left = PendingSendBytes;
            if (left > 0)
                _log.Warn(LogTopic.Transport, "Steam relay stopping with " + left +
                    " byte(s) still queued after " + timeoutMs + " ms; closing anyway.");

            // Linger lets Steam make a final attempt after the handles leave our maps.
            lock (_gate)
            {
                _byId.Clear();
                _byHandle.Clear();
            }
            foreach (Endpoint endpoint in open)
            {
                try { SteamNetworkingSockets.CloseConnection(endpoint.Handle, 0, "leaving the session", true); }
                catch (Exception) { /* already gone */ }
            }

            // Maps are empty: this tears down the listen socket, poll group and callback.
            Shutdown();
        }

        public void Dispose() => Shutdown();

        /// <summary>One connection's pending frames and the partial payload arriving.</summary>
        private sealed class Endpoint
        {
            public readonly ConnectionId Id;
            public readonly HSteamNetConnection Handle;
            public readonly ulong SteamId;
            public readonly string RemoteAddress;

            /// <summary>True once <see cref="TransportEventType.Connected"/> has been published.</summary>
            public bool Announced;

            public byte[] Incoming;
            public int Filled;

            /// <summary>Paced rate currently set on the connection, in bytes/sec.</summary>
            public int SendRate;

            /// <summary>
            /// Highest rate carried without complaint. Starts at the ceiling so the first climb is a search, and
            /// survives idle periods.
            /// </summary>
            public int SafeRate = SendRateCeilingBytesPerSecond;

            /// <summary>Seconds left holding the current rate after a cut.</summary>
            public int HoldTicks;

            /// <summary>Consecutive seconds this path has reported congestion.</summary>
            public int Strikes;

            /// <summary>Best ping seen on this connection - the baseline congestion is measured against.</summary>
            public int PingFloorMs = int.MaxValue;

            /// <summary><see cref="MonotonicClock"/> reading of the last inbound traffic; game thread only.</summary>
            public long LastInboundMs = long.MinValue;

            private readonly RelaySendFeedback _delivery = new RelaySendFeedback();
            private long _acceptedBytes;
            private readonly System.Diagnostics.Stopwatch _bulk = new System.Diagnostics.Stopwatch();
            private long _bulkMoved;

            // Written and drained on the game thread only (Send and Poll both run there).
            private readonly ConcurrentQueue<byte[]> _outbox = new ConcurrentQueue<byte[]>();
            private long _queuedBytes;

            /// <summary>Bytes accepted from the session that Steam has not taken yet.</summary>
            public long QueuedBytes => Interlocked.Read(ref _queuedBytes);

            public Endpoint(ConnectionId id, HSteamNetConnection handle, ulong steamId)
            {
                Id = id;
                Handle = handle;
                SteamId = steamId;
                // Bans and logs only; a Steam ID is steadier than an address behind the relay.
                RemoteAddress = "steam:" + steamId;
            }

            /// <summary>Bytes acknowledged since the last call; Steam's send rate is only what we asked for.</summary>
            public long MeasureGoodput(long outstanding, long intervalMs)
            {
                long moved = _delivery.Sample(Interlocked.Read(ref _acceptedBytes), outstanding);
                if (_bulk.IsRunning) _bulkMoved += moved;
                return moved * 1000L / Math.Max(1L, intervalMs);
            }

            /// <summary>Start timing a bulk transfer, or let one already running continue.</summary>
            public void BeginBulk()
            {
                if (!_bulk.IsRunning) { _bulkMoved = 0; _bulk.Restart(); }
            }

            /// <summary>Closes a finished bulk transfer with its average rate, or null when none ran.</summary>
            public string FinishBulk()
            {
                if (!_bulk.IsRunning) return null;

                long seconds = Math.Max(1L, _bulk.ElapsedMilliseconds / 1000L);
                long moved = _bulkMoved;
                _bulk.Reset();
                _bulkMoved = 0;

                if (moved < BulkBacklogBytes) return null;
                return "transfer finished: " + (moved >> 20) + " MB in " + seconds + " s, " +
                       (moved / seconds / 1024) + " KB/s average,";
            }

            public void Enqueue(byte[] frame)
            {
                _outbox.Enqueue(frame);
                Interlocked.Add(ref _queuedBytes, frame.Length);
                Interlocked.Add(ref _acceptedBytes, frame.Length);
            }

            public bool TryPeek(out byte[] frame) => _outbox.TryPeek(out frame);

            /// <summary>Drop the head frame once Steam has actually accepted it.</summary>
            public void Commit(int frameLength)
            {
                if (_outbox.TryDequeue(out byte[] sent))
                    Interlocked.Add(ref _queuedBytes, -frameLength);
            }
        }
    }
}
