using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Networking.Tcp;
using CS2MultiplayerMod.Core.Protocol;
using Steamworks;

namespace CS2MultiplayerMod.Core.Networking.Steam
{
    // Disconnecting and shutting down - including the flush-then-close path a clean leave uses -
    // and the per-peer Endpoint that holds one connection's buffers and rate state.
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
            Close(endpoint, "disconnected by host", linger: false);
        }

        public void DisconnectAfterFlush(ConnectionId connection)
        {
            Endpoint endpoint;
            lock (_gate)
            {
                if (!_byId.TryGetValue(connection.Value, out endpoint)) return;
            }
            // Linger hands the queued bytes to Steam's own drain, which is what carries a
            // rejection reason out before the connection goes.
            Close(endpoint, "disconnected by host", linger: true);
        }

        public string GetRemoteAddress(ConnectionId connection)
        {
            Endpoint endpoint;
            lock (_gate)
            {
                if (!_byId.TryGetValue(connection.Value, out endpoint)) return null;
            }
            return endpoint.RemoteAddress;
        }

        /// <summary>
        /// Empty: the relay authenticates and encrypts every connection itself, so there
        /// is no certificate for the password proof to bind to.
        /// </summary>
        public byte[] GetChannelBinding(ConnectionId connection)
        {
            return Array.Empty<byte>();
        }

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

            _log.Info("Steam relay transport stopped.");
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

            // Steam services its sockets on its own thread, so its share drains while we
            // wait here - but the outbox only moves when we pump it, and nothing else will
            // during teardown. Bounded because this runs on the game thread.
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            while (PendingSendBytes > 0 && deadline.ElapsedMilliseconds < timeoutMs)
            {
                PumpAllSends();
                Thread.Sleep(5);
            }

            long left = PendingSendBytes;
            if (left > 0)
                _log.Warn("Steam relay stopping with " + left + " byte(s) still queued after " +
                          timeoutMs + " ms; closing anyway.");

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

            // The maps are empty now, so this only tears down the listen socket, the poll
            // group and the callback.
            Shutdown();
        }

        public void Dispose()
        {
            Shutdown();
        }

        /// <summary>
        /// One relay connection: the frames still waiting for room in Steam's send buffer,
        /// and the partial payload currently arriving.
        /// </summary>
        private sealed class Endpoint
        {
            public readonly ConnectionId Id;
            public readonly HSteamNetConnection Handle;
            public readonly string RemoteAddress;

            /// <summary>True once <see cref="TransportEventType.Connected"/> has been published.</summary>
            public bool Announced;

            public byte[] Incoming;
            public int Filled;

            /// <summary>Paced rate currently set on the connection, in bytes/sec.</summary>
            public int SendRate;

            /// <summary>
            /// Highest rate this path has carried without complaint. Starts at the ceiling
            /// because nothing is known yet, which is what makes the first climb a search;
            /// it survives idle periods so later transfers start from the answer.
            /// </summary>
            public int SafeRate = SendRateCeilingBytesPerSecond;

            /// <summary>Seconds left holding the current rate after a cut.</summary>
            public int HoldTicks;

            /// <summary>Consecutive seconds this path has reported congestion.</summary>
            public int Strikes;

            /// <summary>Best ping seen on this connection - the baseline congestion is measured against.</summary>
            public int PingFloorMs = int.MaxValue;

            private long _lastOutstanding = -1;
            private readonly System.Diagnostics.Stopwatch _bulk = new System.Diagnostics.Stopwatch();
            private long _bulkMoved;

            // Written and drained on the game thread only (Send and Poll both run there).
            private readonly ConcurrentQueue<byte[]> _outbox = new ConcurrentQueue<byte[]>();
            private long _queuedBytes;

            /// <summary>Bytes accepted from the session that Steam has not taken yet.</summary>
            public long QueuedBytes
            {
                get { return Interlocked.Read(ref _queuedBytes); }
            }

            public Endpoint(ConnectionId id, HSteamNetConnection handle, ulong steamId)
            {
                Id = id;
                Handle = handle;
                // The session uses this for ban tracking and logging only. A Steam ID is a
                // steadier key for that than an address behind the relay.
                RemoteAddress = "steam:" + steamId;
            }

            /// <summary>
            /// Bytes the peer acknowledged since the last call. This is the only honest
            /// throughput number: Steam's send rate is what we told it to push, not what
            /// arrived.
            /// </summary>
            public long MeasureGoodput(long outstanding, int intervalMs)
            {
                long previous = _lastOutstanding;
                _lastOutstanding = outstanding;
                if (previous < 0 || outstanding > previous) return 0;

                long moved = previous - outstanding;
                if (_bulk.IsRunning) _bulkMoved += moved;
                return moved * 1000L / intervalMs;
            }

            /// <summary>Start timing a bulk transfer, or let one already running continue.</summary>
            public void BeginBulk()
            {
                if (!_bulk.IsRunning) { _bulkMoved = 0; _bulk.Restart(); }
            }

            /// <summary>
            /// Close out a finished bulk transfer and describe what it cost, or null when
            /// none was running. This average is the number to compare against the uplink
            /// when judging whether a world sync was as fast as the connection allows.
            /// </summary>
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
            }

            public bool TryPeek(out byte[] frame)
            {
                return _outbox.TryPeek(out frame);
            }

            /// <summary>Drop the head frame once Steam has actually accepted it.</summary>
            public void Commit(int frameLength)
            {
                byte[] sent;
                if (_outbox.TryDequeue(out sent))
                    Interlocked.Add(ref _queuedBytes, -frameLength);
            }
        }
    }
}
