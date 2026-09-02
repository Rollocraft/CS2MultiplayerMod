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
    // State, the tuning constants, and starting a relay as host or client.
    //
    // The send-rate governor is in SteamRelayGovernor.cs, connection bookkeeping in
    // SteamRelayConnections.cs, framing and the send/receive path in SteamRelayIo.cs, and
    // shutdown plus the per-peer Endpoint in SteamRelayLifecycle.cs.
    public sealed partial class SteamRelayTransport : ITransport
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

            transport._log.Detail(LogTopic.Transport,
                "Hosting over the Steam relay on virtual port " + virtualPort +
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
            transport._log.Detail(LogTopic.Transport, "Connecting to " + steamId + " over the Steam relay.");
            return transport;
        }

        private void Begin()
        {
            // Warming the relay network here means the first connection does not also pay
            // for fetching the relay topology.
            try { SteamNetworkingUtils.InitRelayNetworkAccess(); }
            catch (Exception ex) { _log.Warn(LogTopic.Transport, "Could not pre-warm the Steam relay network: " + ex.Message); }

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
    }
}
