using System.Collections.Generic;
using System.Security.Cryptography.X509Certificates;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Networking;
using CS2MultiplayerMod.Core.Protocol;

namespace CS2MultiplayerMod.Core.Session
{
    /// <summary>
    /// The core session: transport, handshake, peers, keep-alives and routing. Host-authoritative:
    /// clients talk only to the host, which relays commands in canonical order. All public methods run on
    /// the game thread.
    /// </summary>
    public sealed partial class MultiplayerSession
    {
        private const int HeartbeatIntervalMs = 2000;
        private const int PeerTimeoutMs = 10000;
        private const int HandshakeTimeoutMs = 10000;

        /// <summary>
        /// Longest a peer may go without a whole payload while its traffic still arrives (a lossy reliable
        /// stream waiting for a resend).
        /// </summary>
        private const int StalledPeerTimeoutMs = 60000;

        /// <summary>A join awaiting approval is auto-declined after this.</summary>
        private const int JoinApprovalTimeoutMs = 120000;

        private const int HostPlayerId = 1;

        /// <summary>Reassembling blobs allowed at once on a client.</summary>
        private const int MaxActiveBlobs = 4;

        /// <summary>A blob that receives no chunk for this long is abandoned.</summary>
        private const int BlobStallTimeoutMs = 60000;

        /// <summary>Minimum gap between accepted /sync requests - save+stream is expensive, kept short so post-join syncs aren't silently ignored.</summary>
        private const long ResyncRequestCooldownMs = 5000;

        private readonly IModLogger _log;
        private readonly MessageCodec _codec;
        private readonly List<ISessionObserver> _observers = new List<ISessionObserver>();
        private readonly List<TransportEvent> _eventBuffer = new List<TransportEvent>();
        private readonly Dictionary<int, Peer> _peers = new Dictionary<int, Peer>();
        private readonly Dictionary<string, BlobReassembler> _blobs = new Dictionary<string, BlobReassembler>();
        private readonly Dictionary<string, long> _blobTransferIds = new Dictionary<string, long>();
        private readonly Dictionary<string, int> _allowedBlobChannels = new Dictionary<string, int>();
        private readonly HashSet<ushort> _allowedCommandIds = new HashSet<ushort>();
        private readonly HashSet<int> _administrativeRemovals = new HashSet<int>();
        private readonly HashSet<string> _hostBannedAddresses = new HashSet<string>();
        // Already told to go: frames queued behind a flood are not dispatched or logged.
        private readonly HashSet<int> _puntedConnections = new HashSet<int>();
        // Why this machine closed a connection; the transport's text is the same for every local close.
        private readonly Dictionary<int, string> _localCloseReasons = new Dictionary<int, string>();
        private readonly FailedAuthTracker _failedAuth = new FailedAuthTracker();

        private ITransport _transport;
        private MultiplayerConfig _config;
        private X509Certificate2 _certificate;
        private PortForward _portForward;
        private int _nextPlayerId = HostPlayerId + 1;
        private long _lastHeartbeatMs;
        private long _lastBlobSweepMs;
        private long _lastAuthSweepMs;
        private long _lastResyncAcceptedUnixMs;
        private bool _challengeAnswered;
        private bool _awaitingHostApproval;
        private bool _worldSyncSuspended;
        private long _worldSyncEpoch;

        public MultiplayerSession(IModLogger log, MessageCodec codec = null)
        {
            _log = log ?? NullModLogger.Instance;
            _codec = codec ?? MessageCodec.CreateDefault();
        }

        public SessionRole Role { get; private set; } = SessionRole.None;
        public SessionStatus Status { get; private set; } = SessionStatus.Offline;
        public int LocalPlayerId { get; private set; }
        public string LocalPlayerName { get; private set; } = "Player";

        /// <summary>True when the transport is actually running TLS.</summary>
        public bool EncryptionActive { get; private set; }

        /// <summary>True when this session requires a password.</summary>
        public bool PasswordProtected => _config != null && _config.Password.Length > 0;

        /// <summary>True when hosting beyond the local network (LAN filter off).</summary>
        public bool PublicExposure => Role == SessionRole.Host && _config != null && !_config.LanOnly;

        /// <summary>
        /// The host's config, or on a client what the host announced; both must hold the same half of the
        /// simulation.
        /// </summary>
        public bool SimulationSyncEnabled => Role == SessionRole.Client
            ? _hostSimulationSync
            : _config == null || _config.SimulationSync;

        /// <summary>Client-only: the host's answer, defaulted until the accept arrives.</summary>
        private bool _hostSimulationSync = true;

        /// <summary>How the active session reaches its peers (Direct before the first session).</summary>
        public TransportMode Transport => _config != null ? _config.Transport : TransportMode.Direct;

        /// <summary>True when the active session runs over a relay rather than a direct socket.</summary>
        public bool UsesRelay => _config != null && _config.Transport == TransportMode.SteamRelay;

        /// <summary>TCP port of the active session's config (0 before the first session).</summary>
        public int Port => _config != null ? _config.Port : 0;

        /// <summary>Bytes queued in the transport but not yet on the wire (0 when idle).</summary>
        public long PendingSendBytes => _transport != null ? _transport.PendingSendBytes : 0;

        /// <summary>Channel of the blob currently being received, or null. For progress UX.</summary>
        public string IncomingBlobChannel { get; private set; }
        public int IncomingBlobReceived { get; private set; }
        public int IncomingBlobTotal { get; private set; }
        public long IncomingBlobTransferId { get; private set; }

        /// <summary>Between Begin and Resume/Abort; gameplay traffic is rejected here too.</summary>
        public bool WorldSyncSuspended => _worldSyncSuspended;

        // "Sending world %": tracks the blob draining off the send thread.
        private bool _outgoingBlobActive;
        private long _outgoingBlobTotal;
        private long _outgoingBlobSent;
        public bool OutgoingBlobActive => _outgoingBlobActive;
        public long OutgoingBlobTotal => _outgoingBlobTotal;
        public long OutgoingBlobSent => _outgoingBlobSent;

        public IReadOnlyCollection<Peer> Peers => _peers.Values;

        /// <summary>Client only: between the host's HandshakePending and its verdict.</summary>
        public bool AwaitingHostApproval => _awaitingHostApproval;

        /// <summary>Host only: joins awaiting approval; game thread.</summary>
        public IEnumerable<Peer> PendingJoins
        {
            get
            {
                foreach (var pair in _peers)
                    if (pair.Value.AwaitingApproval) yield return pair.Value;
            }
        }

        /// <summary>Host-only client world-sync requests waiting for an allow/deny answer.</summary>
        public IEnumerable<Peer> PendingResyncRequests
        {
            get
            {
                foreach (var pair in _peers)
                    if (pair.Value.AwaitingResyncApproval) yield return pair.Value;
            }
        }

        public void AddObserver(ISessionObserver observer)
        {
            if (observer != null && !_observers.Contains(observer)) _observers.Add(observer);
        }

        public void RemoveObserver(ISessionObserver observer) => _observers.Remove(observer);

        // ---- Authorization registries (filled by the game layer at startup) -----

        /// <summary>A blob channel clients may receive, with a size ceiling; others are dropped.</summary>
        public void AllowBlobChannel(string channel, int maxBytes)
        {
            if (!string.IsNullOrEmpty(channel) && maxBytes > 0)
                _allowedBlobChannels[channel] = maxBytes;
        }

        /// <summary>Allowed command ids; once any is registered, others disconnect their sender.</summary>
        public void AllowCommands(params ushort[] commandIds)
        {
            if (commandIds == null) return;
            for (int i = 0; i < commandIds.Length; i++) _allowedCommandIds.Add(commandIds[i]);
        }

        // ---- Per-tick pump ----------------------------------------------------

        /// <summary>
        /// Drains events, dispatches, sends keep-alives and reaps timed-out peers. <paramref name="nowUnixMs"/>
        /// is the caller's monotonic clock.
        /// </summary>
        public void Update(long nowUnixMs)
        {
            if (_transport == null) return;

            _eventBuffer.Clear();
            _transport.Poll(_eventBuffer);

            // Handle each event at its arrival time: a long frame drains a second of honest traffic at once,
            // which metered at drain time reads as a flood. Only the distance back from now is carried over.
            long monotonicNow = MonotonicClock.NowMs;
            for (int i = 0; i < _eventBuffer.Count; i++)
            {
                TransportEvent evt = _eventBuffer[i];
                long waited = monotonicNow - evt.ReceivedAtMs;
                HandleEvent(evt, waited > 0 ? nowUnixMs - waited : nowUnixMs);
            }

            if (Status == SessionStatus.Connected)
            {
                PumpHeartbeats(nowUnixMs);
                ReportCommandOverruns(nowUnixMs);
                ReapTimedOutPeers(nowUnixMs);
                SweepStalledBlobs(nowUnixMs);
                PumpOutgoingBlobs();
                UpdateOutgoingBlobProgress();

                // The ban book only grows on failed auths, so a sparse sweep is plenty.
                if (nowUnixMs - _lastAuthSweepMs >= 60000)
                {
                    _lastAuthSweepMs = nowUnixMs;
                    _failedAuth.Prune(nowUnixMs);
                }
            }
        }

        /// <summary>Track how much of a streamed world has drained off the send thread.</summary>
        private void UpdateOutgoingBlobProgress()
        {
            if (!_outgoingBlobActive || _transport == null) return;

            long pending = _transport.PendingSendBytes;
            long remaining = 0;
            foreach (OutgoingBlob blob in _outgoingBlobs) remaining += blob.Data.Length - blob.Offset;
            long sent = _outgoingBlobTotal - remaining - pending;
            _outgoingBlobSent = sent < 0 ? 0 : (sent > _outgoingBlobTotal ? _outgoingBlobTotal : sent);

            // Down to keep-alives and commands: the world is sent. Gameplay keeps the queue non-empty.
            if (_outgoingBlobs.Count == 0 && pending < 65536)
            {
                _outgoingBlobSent = _outgoingBlobTotal;
                _outgoingBlobActive = false;
            }
        }

        // ---- Observer fan-out -------------------------------------------------
        // Each callback is isolated: a throwing observer never stops the pump or the others.
    }
}
