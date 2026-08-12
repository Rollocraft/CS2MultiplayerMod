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
    /// <summary>
    /// Transport over Steam's relay network. Peers address each other by join code
    /// (the host's Steam ID) rather than by address, so the host needs no reachable
    /// port and no forwarding. The relay carries the traffic and already encrypts and
    /// authenticates it end to end.
    ///
    /// Unlike the TCP transport this owns no threads: Steam services its own sockets,
    /// connection-state callbacks arrive on the thread that pumps the Steam API (the
    /// game's main thread), and received messages are drained in <see cref="Poll"/> on
    /// that same thread. The event queue is still lock-guarded because the pump thread
    /// is the platform's choice, not ours.
    /// </summary>
    public sealed class SteamRelayTransport : ITransport
    {
        /// <summary>
        /// Payload bytes per relay message. Steam refuses a reliable send above
        /// <c>k_cbMaxSteamNetworkingSocketsMessageSizeSend</c> (512 KiB), so anything
        /// larger is split across messages and rejoined by the receiver.
        /// </summary>
        private const int FrameBytes = 480 * 1024;

        private const int FrameHeaderBytes = 4;

        /// <summary>Per-connection send buffer requested from Steam (default is 512 KiB).</summary>
        private const int SendBufferBytes = 8 * 1024 * 1024;

        /// <summary>
        /// Steam drops a connection whose peer has gone quiet for this long. The 10 s
        /// default is short enough that one stalled second of a 50 MB transfer ends it.
        /// </summary>
        private const int ConnectedTimeoutMs = 30000;

        /// <summary>
        /// Bounds for the paced send rate. Steam's send rate is a clamp, not an estimate -
        /// its own documentation says to set the min and the max to the same value to pick
        /// a rate - so whatever goes in here is what gets pushed at the wire, congestion or
        /// not. <see cref="Govern"/> moves it, opening at four times Steam's 256 KiB/s
        /// default because no broadband uplink is troubled by 1 MiB/s and every second
        /// spent climbing to it is a second of the transfer.
        /// </summary>
        private const int SendRateFloorBytesPerSecond = 128 * 1024;
        private const int SendRateStartBytesPerSecond = 1024 * 1024;
        private const int SendRateCeilingBytesPerSecond = 16 * 1024 * 1024;

        /// <summary>Additive probe above the rate already known to hold, per second.</summary>
        private const int SendRateStepBytesPerSecond = 192 * 1024;

        /// <summary>How often the rate is revisited, and how often a backlog reports itself.</summary>
        private const int GovernIntervalMs = 1000;
        private const int ThroughputProbeMs = 3000;

        /// <summary>
        /// Congestion signal for a path that queues: a ping standing more than this above
        /// the connection's own best. Scaled by the baseline so a distant peer is not
        /// permanently "congested".
        /// </summary>
        private const int CongestedPingExcessMs = 40;

        /// <summary>
        /// Below this the peer is dropping enough of what we send to act on. A saturated
        /// path loses a percent or two as a matter of course and the reliable layer just
        /// resends it, so a threshold set near perfect fires on healthy traffic and spends
        /// the whole transfer backing away from a wire that was never the problem.
        /// </summary>
        private const float HealthyRemoteQuality = 0.90f;

        /// <summary>
        /// Floor on a single congestion-driven cut. A quality reading describes a window
        /// several seconds old, so one sample must not be able to gut the rate - but it is
        /// paired with <see cref="StrikesBeforeBackoff"/>, and a complaint confirmed twice
        /// deserves a decisive answer rather than a timid one.
        /// </summary>
        private const float MaxSingleBackoff = 0.5f;

        /// <summary>
        /// Consecutive seconds a path must complain before the rate moves. One reading is
        /// as likely to be the tail of a cut already made as it is fresh congestion.
        /// </summary>
        private const int StrikesBeforeBackoff = 2;

        /// <summary>
        /// Seconds to hold a rate after cutting it, so the peer's quality window has
        /// refreshed before the next judgement and the same congestion is not punished
        /// several times over. That window has been observed lagging its own event by
        /// 6-9 s, and this plus <see cref="StrikesBeforeBackoff"/> is what has to cover it:
        /// the strikes are counted after the hold expires, so cuts stay 7 s apart.
        /// </summary>
        private const int BackoffHoldTicks = 5;

        /// <summary>
        /// Share of the rate that was flowing when the path complained which still counts
        /// as "known to carry". See <see cref="Backoff"/> - this is what decays a repeatedly
        /// congested estimate downwards without letting one bad second become permanent.
        /// </summary>
        private const float SafeRateShare = 0.9f;

        /// <summary>
        /// A backlog under this is ordinary gameplay traffic. Nothing that small says
        /// anything about what the path would carry, and a rate left high while idle would
        /// become the opening burst of the next world transfer.
        /// </summary>
        private const int BulkBacklogBytes = 256 * 1024;

        /// <summary>Messages drained per receive call before looping for more.</summary>
        private const int ReceiveBatch = 64;

        /// <summary>Coarse cap mirroring the TCP transport: a flood is shed, not buffered.</summary>
        private const int MaxConnections = TcpServerTransport.MaxPendingConnections + 16;

        private readonly IModLogger _log;
        private readonly bool _isHost;
        private readonly ConcurrentQueue<TransportEvent> _events = new ConcurrentQueue<TransportEvent>();
        private readonly object _gate = new object();

        /// <summary>Live connections both ways: our ids are what the session speaks.</summary>
        private readonly Dictionary<int, Endpoint> _byId = new Dictionary<int, Endpoint>();
        private readonly Dictionary<uint, Endpoint> _byHandle = new Dictionary<uint, Endpoint>();

        private readonly IntPtr[] _receiveBuffer = new IntPtr[ReceiveBatch];

        private readonly System.Diagnostics.Stopwatch _probe = System.Diagnostics.Stopwatch.StartNew();
        private readonly System.Diagnostics.Stopwatch _govern = System.Diagnostics.Stopwatch.StartNew();

        private Callback<SteamNetConnectionStatusChangedCallback_t> _statusCallback;
        private HSteamListenSocket _listenSocket;
        private HSteamNetPollGroup _pollGroup;
        private int _nextConnectionId = ConnectionId.Server.Value + 1; // 0=None, 1=Server reserved
        private bool _active;

        private SteamRelayTransport(IModLogger log, bool isHost)
        {
            _log = log ?? NullModLogger.Instance;
            _isHost = isHost;
        }

        public bool IsActive
        {
            get { return _active; }
        }

        /// <summary>
        /// Everything not yet acknowledged by the peer: what Steam still holds plus what is
        /// still queued here waiting for room. Drives the host's "Sending world %" exactly
        /// as the socket backlog does on TCP - counting only Steam's share would read
        /// complete the moment its buffer drained, with most of the world still to go.
        /// </summary>
        public long PendingSendBytes
        {
            get
            {
                long sum = 0;
                lock (_gate)
                {
                    foreach (var pair in _byId)
                    {
                        sum += pair.Value.QueuedBytes;

                        var status = new SteamNetConnectionRealTimeStatus_t();
                        var lanes = new SteamNetConnectionRealTimeLaneStatus_t();
                        try
                        {
                            SteamNetworkingSockets.GetConnectionRealTimeStatus(
                                pair.Value.Handle, ref status, 0, ref lanes);
                        }
                        catch (Exception)
                        {
                            continue; // a connection closing underneath the poll is not an error
                        }
                        sum += status.m_cbPendingReliable + status.m_cbSentUnackedReliable;
                    }
                }
                return sum;
            }
        }

        // ---- construction ---------------------------------------------------------

        /// <summary>Open a relay listen socket. Clients reach it with the local join code.</summary>
        public static SteamRelayTransport StartHost(IModLogger log, int virtualPort)
        {
            var transport = new SteamRelayTransport(log, true);
            transport.Begin();

            SteamNetworkingConfigValue_t[] options = CreationOptions();
            transport._listenSocket = SteamNetworkingSockets.CreateListenSocketP2P(
                virtualPort, options.Length, options);
            if (transport._listenSocket == HSteamListenSocket.Invalid)
            {
                transport.Shutdown();
                throw new InvalidOperationException(
                    "Steam refused to open a relay listen socket. Restart Steam and try again.");
            }

            // Every client's traffic is read through this one group, so without it the host
            // would accept connections and then never see a byte from any of them.
            transport._pollGroup = SteamNetworkingSockets.CreatePollGroup();
            if (transport._pollGroup == HSteamNetPollGroup.Invalid)
            {
                transport.Shutdown();
                throw new InvalidOperationException(
                    "Steam refused to create a relay poll group. Restart Steam and try again.");
            }

            transport._log.Info("Hosting over the Steam relay on virtual port " + virtualPort +
                                "; join code " + SteamRelayProvider.LocalSteamId() + ".");
            return transport;
        }

        /// <summary>Dial a host by join code. Steam picks the route; there is no address to reach.</summary>
        public static SteamRelayTransport Connect(IModLogger log, string joinCode, int virtualPort)
        {
            ulong steamId;
            if (!ulong.TryParse((joinCode ?? "").Trim(), out steamId) || steamId == 0)
                throw new InvalidOperationException(
                    "'" + joinCode + "' is not a Steam join code. Ask the host for the code shown on their Host screen.");

            var transport = new SteamRelayTransport(log, false);
            transport.Begin();

            var identity = new SteamNetworkingIdentity();
            identity.SetSteamID64(steamId);

            SteamNetworkingConfigValue_t[] options = CreationOptions();
            HSteamNetConnection connection =
                SteamNetworkingSockets.ConnectP2P(ref identity, virtualPort, options.Length, options);
            if (connection == HSteamNetConnection.Invalid)
            {
                transport.Shutdown();
                throw new InvalidOperationException(
                    "Steam could not start a relay connection to " + steamId + ".");
            }

            // The client's single connection is the session's well-known Server id, bound
            // before any callback can fire so the first status change already resolves.
            transport.Bind(ConnectionId.Server, connection, steamId);
            transport._log.Info("Connecting to " + steamId + " over the Steam relay.");
            return transport;
        }

        private void Begin()
        {
            // Warming the relay network here means the first connection does not also pay
            // for fetching the relay topology.
            try { SteamNetworkingUtils.InitRelayNetworkAccess(); }
            catch (Exception ex) { _log.Warn("Could not pre-warm the Steam relay network: " + ex.Message); }

            ConfigureForBulkTransfer();

            _statusCallback = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(OnConnectionStatusChanged);
            _active = true;
        }

        /// <summary>
        /// Steam's defaults are tuned for a game's small, steady packets, not for handing
        /// over a 50 MB world. The send buffer (512 KiB) holds barely one frame, so the
        /// transfer would crawl forward one frame per rendered frame; and the 10 s
        /// connected timeout ends a transfer that pauses once.
        ///
        /// The send rate is deliberately not set here - it belongs to the connection, and
        /// <see cref="Govern"/> owns it.
        /// </summary>
        private void ConfigureForBulkTransfer()
        {
            SetInt32(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendBufferSize,
                     ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Global, IntPtr.Zero,
                     SendBufferBytes, "send buffer");
            SetInt32(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_TimeoutConnected,
                     ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Global, IntPtr.Zero,
                     ConnectedTimeoutMs, "connected timeout");
        }

        /// <summary>
        /// Options applied to a connection as it is created. Route negotiation begins with
        /// the connection, so this has to be asked for here rather than set afterwards.
        ///
        /// A direct peer-to-peer route is limited by the two players' own uplinks; a Valve
        /// relay is a shared hop that polices what crosses it, and on a 50 MB world the
        /// difference is minutes. Steam still chooses the route and still falls back to the
        /// relay when no direct path forms - this only puts the direct candidate on the
        /// ballot, which it is not by default. Peers on a direct route learn each other's
        /// addresses, exactly as they already do on this mod's direct-connection mode.
        /// </summary>
        private static SteamNetworkingConfigValue_t[] CreationOptions()
        {
            var ice = new SteamNetworkingConfigValue_t
            {
                m_eValue = ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_P2P_Transport_ICE_Enable,
                m_eDataType = ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32,
            };
            ice.m_val.m_int32 = Constants.k_nSteamNetworkingConfig_P2P_Transport_ICE_Enable_All;
            return new[] { ice };
        }

        /// <summary>
        /// Which path the traffic is taking. The first thing to read when a transfer
        /// disappoints: a relayed route explains a rate the uplink could beat on its own.
        /// </summary>
        private static string RouteOf(Endpoint endpoint)
        {
            try
            {
                SteamNetConnectionInfo_t info;
                if (!SteamNetworkingSockets.GetConnectionInfo(endpoint.Handle, out info))
                    return "unknown";
                return (info.m_nFlags & Constants.k_nSteamNetworkConnectionInfoFlags_Relayed) != 0
                    ? "relayed"
                    : "direct";
            }
            catch (Exception)
            {
                return "unknown";
            }
        }

        private bool SetInt32(ESteamNetworkingConfigValue setting, ESteamNetworkingConfigScope scope,
                              IntPtr scopeObject, int value, string description)
        {
            GCHandle pin = default(GCHandle);
            try
            {
                var boxed = new int[] { value };
                pin = GCHandle.Alloc(boxed, GCHandleType.Pinned);
                bool ok = SteamNetworkingUtils.SetConfigValue(
                    setting,
                    scope,
                    scopeObject,
                    ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32,
                    pin.AddrOfPinnedObject());
                if (!ok)
                    _log.Warn("Steam refused the relay " + description + " setting; transfers may be slow.");
                return ok;
            }
            catch (Exception ex)
            {
                // Non-fatal: the transfer still completes, just slower.
                _log.Warn("Could not set the relay " + description + " (" + ex.Message + ").");
                return false;
            }
            finally
            {
                if (pin.IsAllocated) pin.Free();
            }
        }

        /// <summary>
        /// Drop to a measured rate and stop reacting for a while. The clamp bounds how much
        /// one reading may take away, so a pessimistic sample costs some throughput rather
        /// than the transfer.
        /// </summary>
        private void Backoff(Endpoint endpoint, int target)
        {
            int least = (int)(endpoint.SendRate * MaxSingleBackoff);
            int rate = Math.Max(SendRateFloorBytesPerSecond, Math.Max(least, Math.Min(endpoint.SendRate, target)));

            // The rate known to hold is the one that was flowing when the path complained,
            // shaded down - not the one just cut to. Setting it to the cut made every
            // backoff permanent: the fast climb only runs below SafeRate, so a connection
            // that backed off once crawled upward in single steps for the rest of the
            // session, and the idle clamp then handed that crawl to the next transfer as
            // its starting rate. Shading is what still walks a repeatedly congested
            // estimate downwards instead of oscillating around a level it never carried.
            endpoint.SafeRate = Math.Max(
                rate, (int)(Math.Min(endpoint.SafeRate, endpoint.SendRate) * SafeRateShare));
            endpoint.HoldTicks = BackoffHoldTicks;
            endpoint.Strikes = 0;
            if (rate != endpoint.SendRate) ApplySendRate(endpoint, rate);
        }

        /// <summary>
        /// Pin the connection's paced rate. Min and max are set together because Steam
        /// documents them that way: nothing estimates the bandwidth for us, so a min above
        /// what the path carries is a floor the sender cannot come down from.
        /// </summary>
        private void ApplySendRate(Endpoint endpoint, int bytesPerSecond)
        {
            var scopeObject = new IntPtr(endpoint.Handle.m_HSteamNetConnection);
            SetInt32(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMin,
                     ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Connection, scopeObject,
                     bytesPerSecond, "minimum send rate");
            SetInt32(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax,
                     ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Connection, scopeObject,
                     bytesPerSecond, "maximum send rate");
            endpoint.SendRate = bytesPerSecond;
        }

        /// <summary>
        /// Congestion control for the rate Steam will not work out on its own: climb while
        /// the path is quiet, fall back when it complains twice running, and probe upwards
        /// from there a step at a time. <see cref="Endpoint.SafeRate"/> remembers what held,
        /// so the expensive overshoot happens once per session rather than once per cycle -
        /// and recovering to it is the fast climb, which is why <see cref="Backoff"/> must
        /// not collapse the two together.
        ///
        /// Two different things throttle this path and only one of them shows up as delay.
        /// A queue that fills raises the ping within a second. A rate limiter - Valve's
        /// relays police their traffic - just discards the excess, so the ping stays flat
        /// at 60 ms while the peer receives half of what was sent. Watching delay alone is
        /// blind to that, which is how a run once sat at 2400 KB/s paced, 48% received and
        /// 180 KB/s of actual progress for three minutes.
        ///
        /// So loss cuts the rate too, and it sizes its own cut: the share the peer received
        /// of what went out is the share of the wire rate that fits, and multiplying gives
        /// the limit directly instead of feeling for it 25% at a time. Both readings
        /// describe a window several seconds old, hence the hold after every cut - without
        /// it the same congestion is punished repeatedly and the rate walks to the floor.
        /// </summary>
        private void Govern()
        {
            if (_govern.ElapsedMilliseconds < GovernIntervalMs) return;
            _govern.Restart();

            bool report = _probe.ElapsedMilliseconds >= ThroughputProbeMs;
            if (report) _probe.Restart();

            lock (_gate)
            {
                foreach (var pair in _byId)
                {
                    Endpoint endpoint = pair.Value;
                    var status = new SteamNetConnectionRealTimeStatus_t();
                    var lanes = new SteamNetConnectionRealTimeLaneStatus_t();
                    try
                    {
                        SteamNetworkingSockets.GetConnectionRealTimeStatus(
                            endpoint.Handle, ref status, 0, ref lanes);
                    }
                    catch (Exception) { continue; }

                    long outstanding = endpoint.QueuedBytes +
                                       status.m_cbPendingReliable + status.m_cbSentUnackedReliable;
                    bool bulk = outstanding >= BulkBacklogBytes;
                    if (bulk) endpoint.BeginBulk();
                    long goodput = endpoint.MeasureGoodput(outstanding, GovernIntervalMs);

                    if (!bulk)
                    {
                        string finished = endpoint.FinishBulk();
                        if (finished != null)
                            _log.Info("[relay] " + endpoint.Id + " " + finished + " over a " +
                                      RouteOf(endpoint) + " route.");

                        int idle = Math.Min(SendRateStartBytesPerSecond, endpoint.SafeRate);
                        if (endpoint.SendRate != idle) ApplySendRate(endpoint, idle);
                        endpoint.HoldTicks = 0;
                        endpoint.Strikes = 0;
                        continue;
                    }

                    // Steam reports no ping until traffic has flowed; folding that in would
                    // pin the baseline at zero and read every real ping as congestion. The
                    // floor then creeps up so a path that genuinely got slower re-baselines.
                    bool pingKnown = status.m_nPing > 0;
                    if (pingKnown)
                    {
                        // Rises a millisecond at a time so a plateau of mild congestion
                        // cannot quietly become this connection's idea of normal.
                        if (status.m_nPing < endpoint.PingFloorMs) endpoint.PingFloorMs = status.m_nPing;
                        else endpoint.PingFloorMs++;
                    }

                    int pingBudget = endpoint.PingFloorMs +
                                     Math.Max(CongestedPingExcessMs, endpoint.PingFloorMs / 2);
                    float quality = status.m_flConnectionQualityRemote; // negative until the peer reports

                    bool queueing = pingKnown && status.m_nPing > pingBudget;
                    bool losing = quality >= 0f && quality < HealthyRemoteQuality;

                    if (endpoint.HoldTicks > 0)
                    {
                        endpoint.HoldTicks--;
                    }
                    else if (queueing || losing)
                    {
                        if (++endpoint.Strikes >= StrikesBeforeBackoff)
                        {
                            // What the peer actually received is what the path will carry,
                            // so fall straight to it rather than stepping down and
                            // overshooting. A queue that fills has no such measurement
                            // behind it and only says "less than this".
                            float wire = status.m_flOutBytesPerSec;
                            float carrying = wire > 0f ? wire : endpoint.SendRate;
                            Backoff(endpoint, losing
                                ? (int)(carrying * quality * 0.95f)
                                : (int)(endpoint.SendRate * 0.75f));
                        }
                    }
                    else
                    {
                        endpoint.Strikes = 0;

                        // Below what already held, climb back to it; above it, feel the way
                        // up one step at a time.
                        int rate = endpoint.SendRate;
                        int next = rate < endpoint.SafeRate
                            ? Math.Min(endpoint.SafeRate, rate + Math.Max(rate / 6, SendRateStepBytesPerSecond))
                            : Math.Min(SendRateCeilingBytesPerSecond, rate + SendRateStepBytesPerSecond);
                        if (next != rate) ApplySendRate(endpoint, next);
                    }

                    if (!report) continue;
                    _log.Info("[relay] " + endpoint.Id + " sending: " + (outstanding / 1024) +
                              " KB left at " + (goodput / 1024) + " KB/s (paced " +
                              (endpoint.SendRate / 1024) + " KB/s, held " +
                              (endpoint.SafeRate / 1024) + " KB/s, wire " +
                              ((int)status.m_flOutBytesPerSec / 1024) + " KB/s), ping " +
                              status.m_nPing + " of " + pingBudget + " ms, peer received " +
                              (quality < 0f ? "?" : ((int)(quality * 100)).ToString()) + "%, " +
                              RouteOf(endpoint) + " route.");
                }
            }
        }

        // ---- connection lifecycle -------------------------------------------------

        private Endpoint Bind(ConnectionId id, HSteamNetConnection handle, ulong steamId)
        {
            var endpoint = new Endpoint(id, handle, steamId);
            lock (_gate)
            {
                _byId[id.Value] = endpoint;
                _byHandle[handle.m_HSteamNetConnection] = endpoint;
            }
            ApplySendRate(endpoint, SendRateStartBytesPerSecond);
            return endpoint;
        }

        private Endpoint Find(uint handle)
        {
            lock (_gate)
            {
                Endpoint found;
                return _byHandle.TryGetValue(handle, out found) ? found : null;
            }
        }

        private void Drop(Endpoint endpoint)
        {
            lock (_gate)
            {
                _byId.Remove(endpoint.Id.Value);
                _byHandle.Remove(endpoint.Handle.m_HSteamNetConnection);
            }
        }

        private void OnConnectionStatusChanged(SteamNetConnectionStatusChangedCallback_t evt)
        {
            // The callback is process-wide: it also carries connections belonging to the
            // game itself or to another transport instance. Anything we did not open is
            // not ours to answer.
            if (!_active) return;

            Endpoint endpoint = Find(evt.m_hConn.m_HSteamNetConnection);
            ESteamNetworkingConnectionState state = evt.m_info.m_eState;

            switch (state)
            {
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting:
                    if (endpoint == null) AcceptIncoming(evt);
                    break;

                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected:
                    if (endpoint != null && !endpoint.Announced)
                    {
                        endpoint.Announced = true;
                        _log.Info("Steam relay connection " + endpoint.Id + " established with " +
                                  endpoint.RemoteAddress + ".");
                        Enqueue(TransportEvent.Connected(endpoint.Id));
                    }
                    break;

                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer:
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally:
                    if (endpoint != null) Close(endpoint, DescribeClose(evt), linger: false);
                    break;
            }
        }

        private void AcceptIncoming(SteamNetConnectionStatusChangedCallback_t evt)
        {
            // Only inbound connections carry our listen socket; a client's own outbound
            // dial reports Connecting too and must not be accepted.
            if (!_isHost) return;
            if (evt.m_info.m_hListenSocket != _listenSocket) return;

            ulong steamId = evt.m_info.m_identityRemote.GetSteamID64();

            int open;
            lock (_gate) { open = _byId.Count; }
            if (open >= MaxConnections)
            {
                _log.Warn("Refused Steam relay connection from " + steamId + ": too many open connections.");
                SteamNetworkingSockets.CloseConnection(evt.m_hConn, 0, "too many connections", false);
                return;
            }

            EResult accepted = SteamNetworkingSockets.AcceptConnection(evt.m_hConn);
            if (accepted != EResult.k_EResultOK)
            {
                _log.Warn("Could not accept Steam relay connection from " + steamId + ": " + accepted + ".");
                SteamNetworkingSockets.CloseConnection(evt.m_hConn, 0, "accept failed", false);
                return;
            }

            var id = new ConnectionId(_nextConnectionId++);
            Endpoint endpoint = Bind(id, evt.m_hConn, steamId);
            if (!SteamNetworkingSockets.SetConnectionPollGroup(evt.m_hConn, _pollGroup))
            {
                // Outside the poll group this connection is deaf; better to refuse it than
                // to leave a peer that handshakes and then goes quiet forever.
                _log.Warn("Could not add Steam relay connection " + id + " from " + steamId +
                          " to the poll group; refusing it.");
                Close(endpoint, "poll group rejected the connection", linger: false);
                return;
            }

            _log.Info("Accepted Steam relay connection " + id + " from " + steamId + ".");
            // Connected is announced on the Connected state, so the session never talks to
            // a connection that is still negotiating.
        }

        private static string DescribeClose(SteamNetConnectionStatusChangedCallback_t evt)
        {
            string debug = evt.m_info.m_szEndDebug;
            bool byPeer = evt.m_info.m_eState ==
                          ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer;
            string prefix = byPeer ? "closed by peer" : "connection problem";
            return string.IsNullOrEmpty(debug) ? prefix : prefix + ": " + debug;
        }

        /// <summary>
        /// Steam's own account of a connection. Worth the call on a failed send: closing
        /// from the send path unhooks the endpoint, so the status callback that would have
        /// carried this reason arrives to nothing and the log ends with no cause at all.
        /// </summary>
        private static string DescribeConnection(Endpoint endpoint)
        {
            try
            {
                SteamNetConnectionInfo_t info;
                if (!SteamNetworkingSockets.GetConnectionInfo(endpoint.Handle, out info))
                    return "Steam no longer knows the connection.";
                return "Steam reports state=" + info.m_eState + " endReason=" + info.m_eEndReason +
                       " \"" + info.m_szEndDebug + "\".";
            }
            catch (Exception ex)
            {
                return "Steam could not describe the connection (" + ex.Message + ").";
            }
        }

        private void Close(Endpoint endpoint, string reason, bool linger)
        {
            Drop(endpoint);
            try { SteamNetworkingSockets.CloseConnection(endpoint.Handle, 0, reason, linger); }
            catch (Exception) { /* already gone */ }
            if (endpoint.Announced || !_isHost)
                Enqueue(TransportEvent.Disconnected(endpoint.Id, reason));
        }

        private void Enqueue(TransportEvent evt)
        {
            _events.Enqueue(evt);
        }

        // ---- sending --------------------------------------------------------------

        /// <summary>
        /// Queue a payload. Frames go into the endpoint's outbox and are handed to Steam as
        /// fast as it will take them, which is the whole point: the session hands a world
        /// over as ~200 chunks in a single frame, and a Steam connection's send buffer is
        /// bounded, so pushing them all straight in earns k_EResultLimitExceeded and drops
        /// the peer. TCP never showed this because its transport queues without limit.
        /// </summary>
        public void Send(ConnectionId target, byte[] payload)
        {
            if (payload == null) return;

            Endpoint endpoint;
            lock (_gate)
            {
                if (!_byId.TryGetValue(target.Value, out endpoint)) return;
            }

            // Header carries the whole payload's length so the receiver can rejoin the
            // frames; Steam delivers them reliably and in order, so no other bookkeeping
            // is needed on the wire.
            int firstBody = Math.Min(payload.Length, FrameBytes - FrameHeaderBytes);
            var first = new byte[FrameHeaderBytes + firstBody];
            first[0] = (byte)payload.Length;
            first[1] = (byte)(payload.Length >> 8);
            first[2] = (byte)(payload.Length >> 16);
            first[3] = (byte)(payload.Length >> 24);
            Buffer.BlockCopy(payload, 0, first, FrameHeaderBytes, firstBody);
            endpoint.Enqueue(first);

            int offset = firstBody;
            while (offset < payload.Length)
            {
                int size = Math.Min(FrameBytes, payload.Length - offset);
                var frame = new byte[size];
                Buffer.BlockCopy(payload, offset, frame, 0, size);
                endpoint.Enqueue(frame);
                offset += size;
            }

            // Push what fits right now; Poll carries the rest over the coming frames.
            PumpSends(endpoint);
        }

        /// <summary>Hand queued frames to Steam until it pushes back or the outbox empties.</summary>
        private void PumpSends(Endpoint endpoint)
        {
            byte[] frame;
            while (endpoint.TryPeek(out frame))
            {
                SendOutcome outcome = SendFrame(endpoint, frame);
                if (outcome == SendOutcome.Backpressure) return; // retry next frame
                if (outcome == SendOutcome.Failed) return;       // Close already ran
                endpoint.Commit(frame.Length);
            }
        }

        private void PumpAllSends()
        {
            Endpoint[] open;
            lock (_gate)
            {
                if (_byId.Count == 0) return;
                open = new Endpoint[_byId.Count];
                _byId.Values.CopyTo(open, 0);
            }
            foreach (Endpoint endpoint in open) PumpSends(endpoint);
        }

        private enum SendOutcome
        {
            Sent,

            /// <summary>Steam's buffer is full. The frame stays queued and is retried.</summary>
            Backpressure,

            Failed,
        }

        private SendOutcome SendFrame(Endpoint endpoint, byte[] frame)
        {
            GCHandle pin = GCHandle.Alloc(frame, GCHandleType.Pinned);
            try
            {
                // NoNagle: every frame we hand over is already a complete unit, so there is
                // nothing to coalesce and the delay would only sit between pumps.
                long messageNumber;
                EResult result = SteamNetworkingSockets.SendMessageToConnection(
                    endpoint.Handle, pin.AddrOfPinnedObject(), (uint)frame.Length,
                    Constants.k_nSteamNetworkingSend_ReliableNoNagle, out messageNumber);

                if (result == EResult.k_EResultOK) return SendOutcome.Sent;

                // Not a failure: the connection is healthy and simply has as much queued as
                // it will hold. Everything else means the peer is gone or the message was
                // rejected, and a half-delivered payload can never be completed.
                if (result == EResult.k_EResultLimitExceeded) return SendOutcome.Backpressure;

                _log.Warn("Steam relay send to " + endpoint.Id + " failed (" + result + "); dropping the connection. " +
                          DescribeConnection(endpoint));
                Close(endpoint, "relay send failed: " + result, linger: false);
                return SendOutcome.Failed;
            }
            catch (Exception ex)
            {
                _log.Warn("Steam relay send to " + endpoint.Id + " threw (" + ex.Message + "); dropping the connection.");
                Close(endpoint, "relay send error", linger: false);
                return SendOutcome.Failed;
            }
            finally
            {
                pin.Free();
            }
        }

        // ---- receiving ------------------------------------------------------------

        public int Poll(IList<TransportEvent> sink)
        {
            if (_active)
            {
                // Drain the outbox first: a world transfer is carried entirely by these
                // per-frame pumps once the initial burst filled Steam's buffer.
                PumpAllSends();
                Govern();
                Receive();
            }

            int count = 0;
            TransportEvent evt;
            while (_events.TryDequeue(out evt))
            {
                sink.Add(evt);
                count++;
            }
            return count;
        }

        private void Receive()
        {
            while (true)
            {
                int received;
                try
                {
                    received = _isHost
                        ? SteamNetworkingSockets.ReceiveMessagesOnPollGroup(_pollGroup, _receiveBuffer, ReceiveBatch)
                        : ReceiveOnClientConnection();
                }
                catch (Exception ex)
                {
                    _log.Warn("Steam relay receive failed: " + ex.Message);
                    return;
                }

                if (received <= 0) return;

                for (int i = 0; i < received; i++)
                {
                    IntPtr pointer = _receiveBuffer[i];
                    if (pointer == IntPtr.Zero) continue;
                    try
                    {
                        SteamNetworkingMessage_t message = SteamNetworkingMessage_t.FromIntPtr(pointer);
                        var frame = new byte[message.m_cbSize];
                        if (message.m_cbSize > 0)
                            Marshal.Copy(message.m_pData, frame, 0, message.m_cbSize);
                        Accept(message.m_conn.m_HSteamNetConnection, frame);
                    }
                    finally
                    {
                        SteamNetworkingMessage_t.Release(pointer);
                    }
                }

                if (received < ReceiveBatch) return;
            }
        }

        private int ReceiveOnClientConnection()
        {
            Endpoint endpoint;
            lock (_gate)
            {
                if (!_byId.TryGetValue(ConnectionId.Server.Value, out endpoint)) return 0;
            }
            return SteamNetworkingSockets.ReceiveMessagesOnConnection(endpoint.Handle, _receiveBuffer, ReceiveBatch);
        }

        /// <summary>Rejoin frames into whole payloads and publish each completed one.</summary>
        private void Accept(uint handle, byte[] frame)
        {
            Endpoint endpoint = Find(handle);
            if (endpoint == null) return;

            int offset = 0;
            while (offset < frame.Length)
            {
                if (endpoint.Incoming == null)
                {
                    if (frame.Length - offset < FrameHeaderBytes)
                    {
                        Close(endpoint, "truncated relay frame header", linger: false);
                        return;
                    }

                    int total = frame[offset]
                                | (frame[offset + 1] << 8)
                                | (frame[offset + 2] << 16)
                                | (frame[offset + 3] << 24);
                    offset += FrameHeaderBytes;

                    if (total < 0 || total > ProtocolConstants.MaxPayloadBytes)
                    {
                        // Refuse to allocate on a peer's say-so.
                        Close(endpoint, "relay payload of " + total + " bytes exceeds the protocol limit", linger: false);
                        return;
                    }

                    if (total == 0)
                    {
                        Enqueue(TransportEvent.Data(endpoint.Id, Array.Empty<byte>()));
                        continue;
                    }

                    endpoint.Incoming = new byte[total];
                    endpoint.Filled = 0;
                }

                int wanted = endpoint.Incoming.Length - endpoint.Filled;
                int available = Math.Min(wanted, frame.Length - offset);
                Buffer.BlockCopy(frame, offset, endpoint.Incoming, endpoint.Filled, available);
                endpoint.Filled += available;
                offset += available;

                if (endpoint.Filled == endpoint.Incoming.Length)
                {
                    byte[] payload = endpoint.Incoming;
                    endpoint.Incoming = null;
                    endpoint.Filled = 0;
                    Enqueue(TransportEvent.Data(endpoint.Id, payload));
                }
            }
        }

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
