using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Networking.Tcp;
using Steamworks;

namespace CS2MultiplayerMod.Core.Networking.Steam
{
    // State, tuning and startup. Governor, connections, I/O and lifecycle live in the other
    // SteamRelay*.cs partials.
    public sealed partial class SteamRelayTransport : ITransport, IPlatformFriendLookup, IInboundActivity
    {
        /// <summary>
        /// Below Steam's 512 KiB reliable-send limit (<c>k_cbMaxSteamNetworkingSocketsMessageSizeSend</c>);
        /// larger payloads are split and rejoined.
        /// </summary>
        private const int FrameBytes = 480 * 1024;

        private const int FrameHeaderBytes = 4;

        /// <summary>Per-connection send buffer requested from Steam (default is 512 KiB).</summary>
        private const int SendBufferBytes = 8 * 1024 * 1024;

        /// <summary>Steam's 10 s default ends a large transfer on one stalled second.</summary>
        private const int ConnectedTimeoutMs = 30000;

        /// <summary>
        /// Paced-rate bounds. Steam's rate is a clamp, not an estimate, so this is what hits the wire;
        /// <see cref="Govern"/> moves it, opening at four times Steam's 256 KiB/s default.
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
        /// Queueing signal: ping this far above the connection's best, scaled so a distant peer is not always
        /// congested.
        /// </summary>
        private const int CongestedPingExcessMs = 40;

        /// <summary>
        /// Loss worth acting on. A saturated path loses a percent or two that the reliable layer resends; a
        /// near-perfect threshold backs off healthy traffic.
        /// </summary>
        private const float HealthyRemoteQuality = 0.90f;

        /// <summary>
        /// Acknowledged share of the paced rate above which a complaint is stale: cutting a rate that
        /// delivers in full walks a working transfer to the floor.
        /// </summary>
        private const float DeliveredShare = 0.9f;

        /// <summary>
        /// Acknowledged share needed before the peer's first quality report (which can take 20 s) for a
        /// climb to count as carried. Below <see cref="DeliveredShare"/>: acknowledgements trail a rising rate.
        /// </summary>
        private const float StarvedShare = 0.5f;

        /// <summary>Inbound rate that means the peer is still sending; keep-alives stay far below.</summary>
        private const float InboundActivityBytesPerSecond = 16 * 1024;

        /// <summary>
        /// Floor on one cut, so a lagging sample cannot gut the rate; with
        /// <see cref="StrikesBeforeBackoff"/> a confirmed complaint still gets a decisive cut.
        /// </summary>
        private const float MaxSingleBackoff = 0.5f;

        /// <summary>Consecutive complaining seconds before the rate moves.</summary>
        private const int StrikesBeforeBackoff = 2;

        /// <summary>
        /// Hold after a cut so the peer's quality window (lagging 6-9 s) refreshes; strikes count after the
        /// hold, keeping cuts 7 s apart.
        /// </summary>
        private const int BackoffHoldTicks = 5;

        /// <summary>
        /// Share of the complaining rate still counted as known-good (see <see cref="Backoff"/>).
        /// </summary>
        private const float SafeRateShare = 0.9f;

        /// <summary>
        /// Smaller backlogs are gameplay traffic: they say nothing about capacity, and a high idle rate would
        /// open the next transfer.
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

        /// <summary>Steam's message clock offset from ours; see <c>ArrivalOf</c>.</summary>
        private long _steamClockOffsetMs;
        private bool _steamClockAligned;

        private SteamRelayTransport(IModLogger log, bool isHost)
        {
            _log = log ?? NullModLogger.Instance;
            _isHost = isHost;
        }

        public bool IsActive => _active;

        /// <summary>
        /// Everything unacknowledged: Steam's share plus our outbox. Drives "Sending world %"; Steam's share
        /// alone reads complete while most of the world is still queued here.
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

            // Every client's traffic is read through this one group.
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
            if (!ulong.TryParse((joinCode ?? "").Trim(), out ulong steamId) || steamId == 0)
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

            // Bound before any callback, so the first status change resolves.
            transport.Bind(ConnectionId.Server, connection, steamId);
            transport._log.Detail(LogTopic.Transport, "Connecting to " + steamId + " over the Steam relay.");
            return transport;
        }

        private void Begin()
        {
            // Warm the relay network so the first connection does not also fetch the topology.
            try { SteamNetworkingUtils.InitRelayNetworkAccess(); }
            catch (Exception ex) { _log.Warn(LogTopic.Transport, "Could not pre-warm the Steam relay network: " + ex.Message); }

            ConfigureForBulkTransfer();

            _statusCallback = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(OnConnectionStatusChanged);
            _active = true;
        }

        /// <summary>
        /// Steam's defaults suit small steady packets: a 512 KiB send buffer holds about one frame, and a
        /// 10 s timeout ends a transfer that pauses once. The send rate belongs to <see cref="Govern"/>.
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
        /// Creation-time options (route negotiation starts with the connection). Offers a direct route, limited
        /// only by the players' uplinks, besides the shared, policed relay; Steam still chooses and falls back
        /// to the relay. Direct peers learn each other's addresses, as in direct mode.
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
