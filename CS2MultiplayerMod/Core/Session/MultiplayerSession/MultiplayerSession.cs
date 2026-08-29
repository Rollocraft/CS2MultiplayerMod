using System.Collections.Generic;
using System.Security.Cryptography.X509Certificates;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Networking;
using CS2MultiplayerMod.Core.Protocol;

namespace CS2MultiplayerMod.Core.Session
{
    /// <summary>
    /// The multiplayer core session manager. Owns transport, handshake, peer list,
    /// keep-alives, and message routing. Host-authoritative: clients talk only to host,
    /// which relays commands in canonical order. Challenge-response auth, rate budgeting,
    /// and protocol violations trigger disconnects. All public methods run on game thread.
    /// See <see cref="Update"/>, <see cref="HandshakeAuth"/>, <see cref="ITransport.Poll"/>.
    /// </summary>
    public sealed partial class MultiplayerSession
    {
        private const int HeartbeatIntervalMs = 2000;
        private const int PeerTimeoutMs = 10000;
        private const int HandshakeTimeoutMs = 10000;

        /// <summary>A join awaiting the host's manual approval is auto-declined after this
        /// long, so an absent host never leaves the would-be player waiting forever and a
        /// pre-handshake socket is never held open indefinitely.</summary>
        private const int JoinApprovalTimeoutMs = 120000;

        public int AverageLatencyMs
        {
            get
            {
                int sum = 0, count = 0;
                foreach (var p in _peers.Values)
                {
                    if (p.Handshaked && p.LatencyMs >= 0)
                    {
                        sum += p.LatencyMs;
                        count++;
                    }
                }
                return count > 0 ? sum / count : -1;
            }
        }

        public int HandshakedPeerCount()
        {
            int count = 0;
            foreach (var p in _peers.Values)
                if (p.Handshaked) count++;
            return count;
        }

        public int AverageJitterMs
        {
            get
            {
                int sum = 0, count = 0;
                foreach (var p in _peers.Values)
                {
                    if (p.Handshaked && p.LatencyMs >= 0)
                    {
                        sum += p.JitterMs;
                        count++;
                    }
                }
                return count > 0 ? sum / count : 0;
            }
        }

        public string AverageQualityRating
        {
            get
            {
                int lat = AverageLatencyMs;
                if (lat < 0) return "Unknown";
                int jit = AverageJitterMs;
                if (lat <= 60 && jit <= 15) return "Excellent";
                if (lat <= 140 && jit <= 35) return "Good";
                if (lat <= 250) return "Fair";
                return "Poor";
            }
        }

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
        private readonly FailedAuthTracker _failedAuth = new FailedAuthTracker();
        private readonly CommandDeduplicator _commandDeduplicator = new CommandDeduplicator();

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

        /// <summary>How the active session reaches its peers (Direct before the first session).</summary>
        public TransportMode Transport => _config != null ? _config.Transport : TransportMode.Direct;

        /// <summary>True when the active session runs over a relay rather than a direct socket.</summary>
        public bool UsesRelay => _config != null && _config.Transport == TransportMode.SteamRelay;

        /// <summary>TCP port of the active session's config (0 before the first session).</summary>
        public int Port => _config != null ? _config.Port : 0;

        /// <summary>
        /// What the router made of opening this host's port. Null whenever nothing was
        /// asked: a client, a relay session, or a LAN-only host, none of which need one.
        /// </summary>
        public PortForwardState? PortForwardStatus =>
            _portForward != null ? _portForward.State : (PortForwardState?)null;

        /// <summary>The public address the router reported, or null if it never told us.</summary>
        public string PortForwardAddress => _portForward != null ? _portForward.ExternalAddress : null;

        /// <summary>Bytes queued in the transport but not yet on the wire (0 when idle).</summary>
        public long PendingSendBytes => _transport != null ? _transport.PendingSendBytes : 0;

        /// <summary>Channel of the blob currently being received, or null. For progress UX.</summary>
        public string IncomingBlobChannel { get; private set; }
        public int IncomingBlobReceived { get; private set; }
        public int IncomingBlobTotal { get; private set; }
        public long IncomingBlobTransferId { get; private set; }

        /// <summary>
        /// True between a world-sync Begin and its matching Resume/Abort. Gameplay traffic is
        /// rejected at the session boundary during this interval, in addition to game-layer gates.
        /// </summary>
        public bool WorldSyncSuspended => _worldSyncSuspended;
        public long WorldSyncEpoch => _worldSyncEpoch;

        // Host-side "Sending world %": a streamed blob is queued instantly (the send is
        // non-blocking) and then drains off the transport's send thread; these track that
        // drain so the host can show a progress bar instead of appearing frozen.
        private bool _outgoingBlobActive;
        private long _outgoingBlobTotal;
        private long _outgoingBlobSent;
        public bool OutgoingBlobActive => _outgoingBlobActive;
        public long OutgoingBlobTotal => _outgoingBlobTotal;
        public long OutgoingBlobSent => _outgoingBlobSent;

        public IReadOnlyCollection<Peer> Peers => _peers.Values;

        public Peer FindPeer(int playerId)
        {
            foreach (var peer in _peers.Values)
            {
                if (peer.PlayerId == playerId) return peer;
            }
            return null;
        }

        /// <summary>
        /// Client-only: the host acknowledged the join and it is waiting for the host to
        /// approve it by hand. True between the host's HandshakePending and its accept/reject.
        /// </summary>
        public bool AwaitingHostApproval => _awaitingHostApproval;

        /// <summary>
        /// Host-only: joins that passed every automatic check and are waiting for the host
        /// to approve or decline them. Enumerated on the game thread alongside the pump.
        /// </summary>
        public IEnumerable<Peer> PendingJoins
        {
            get
            {
                foreach (var pair in _peers)
                    if (pair.Value.AwaitingApproval) yield return pair.Value;
            }
        }

        public void AddObserver(ISessionObserver observer)
        {
            if (observer != null && !_observers.Contains(observer)) _observers.Add(observer);
        }

        public void RemoveObserver(ISessionObserver observer) => _observers.Remove(observer);

        // ---- Authorization registries (filled by the game layer at startup) -----

        /// <summary>
        /// Declare a blob channel clients may receive with size ceiling. Unregistered blobs are dropped - secure by default.
        /// </summary>
        public void AllowBlobChannel(string channel, int maxBytes)
        {
            if (!string.IsNullOrEmpty(channel) && maxBytes > 0)
                _allowedBlobChannels[channel] = maxBytes;
        }

        /// <summary>
        /// Declare the simulation command ids peers are allowed to send. Once any id is
        /// registered, a command outside the set disconnects its sender.
        /// </summary>
        public void AllowCommands(params ushort[] commandIds)
        {
            if (commandIds == null) return;
            for (int i = 0; i < commandIds.Length; i++) _allowedCommandIds.Add(commandIds[i]);
        }

        // ---- Lifecycle --------------------------------------------------------





        // ---- Per-tick pump ----------------------------------------------------

        /// <summary>
        /// Advance the session: drain transport events, dispatch messages, send
        /// keep-alives, and reap timed-out peers. <paramref name="nowUnixMs"/> is the
        /// caller's monotonic clock so the core stays free of <c>DateTime.Now</c>.
        /// </summary>
        public void Update(long nowUnixMs)
        {
            if (_transport == null) return;

            _eventBuffer.Clear();
            _transport.Poll(_eventBuffer);
            for (int i = 0; i < _eventBuffer.Count; i++)
                HandleEvent(_eventBuffer[i], nowUnixMs);

            if (Status == SessionStatus.Connected)
            {
                PumpHeartbeats(nowUnixMs);
                ReapTimedOutPeers(nowUnixMs);
                SweepStalledBlobs(nowUnixMs);
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
            long sent = _outgoingBlobTotal - pending;
            _outgoingBlobSent = sent < 0 ? 0 : (sent > _outgoingBlobTotal ? _outgoingBlobTotal : sent);

            // Drained to a trickle (only small keep-alives/commands left): the world is sent.
            if (pending < 65536)
            {
                _outgoingBlobSent = _outgoingBlobTotal;
                _outgoingBlobActive = false;
            }
        }








        // ---- Handshake --------------------------------------------------------








        // ---- Keep-alive -------------------------------------------------------




        // ---- Chat & commands --------------------------------------------------



        // ---- On-demand world resync (/sync) ------------------------------------





        // ---- Replicated state -------------------------------------------------





        // ---- Player positions -------------------------------------------------



        // ---- Large blobs (e.g. savegame for map sync) -------------------------







        // ---- Send helpers -----------------------------------------------------



        // ---- Observer fan-out -------------------------------------------------
        // Every callback is isolated: one observer throwing must never kill the pump,
        // stop the remaining observers, or take the session down.












    }
}
