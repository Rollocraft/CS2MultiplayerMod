using CS2MultiplayerMod.Core.Networking;

namespace CS2MultiplayerMod.Core.Session
{
    /// <summary>A session participant as seen locally.</summary>
    public sealed class Peer
    {
        public readonly ConnectionId Connection;

        /// <summary>Host-assigned; 0 until the approval prompt or the completed handshake.</summary>
        public int PlayerId;

        public string Name;

        /// <summary>True once the handshake has succeeded for this peer.</summary>
        public bool Handshaked;

        /// <summary>Host side: awaiting manual approval. Never overlaps <see cref="Handshaked"/>.</summary>
        public bool AwaitingApproval;

        /// <summary>Host-side: a client world-sync request waiting for an explicit host answer.</summary>
        public bool AwaitingResyncApproval;
        public string PendingResyncReason;
        public bool PendingResyncAutomatic;

        /// <summary>Local monotonic timestamp (Unix ms) of the last whole payload received from this peer.</summary>
        public long LastSeenUnixMs;

        /// <summary>The <see cref="LastSeenUnixMs"/> a stalled-but-alive notice was already logged for.</summary>
        internal long StallReportedForSeenMs = long.MinValue;

        /// <summary>When the underlying connection appeared - pending peers expire on this.</summary>
        public long ConnectedAtUnixMs;

        /// <summary>Most recent round-trip estimate in milliseconds, or -1 if unknown.</summary>
        public int LatencyMs = -1;

        /// <summary>Remote IP for logging/ban bookkeeping. May be null.</summary>
        public string RemoteAddress;

        /// <summary>Host-side: the one-time nonce sent in this peer's handshake challenge.</summary>
        public byte[] ChallengeNonce;

        /// <summary>The peer's mod build from its handshake, for the accept line. Null until handshaked.</summary>
        public string ModVersion;

        /// <summary>The peer's source commit, for the same reason as <see cref="ModVersion"/>.</summary>
        public string BuildId;

        /// <summary>The peer's game version, for the same reason as <see cref="ModVersion"/>.</summary>
        public string GameVersion;

        /// <summary>Host-side: traffic budgets for everything this peer sends.</summary>
        public readonly PeerRateLimiter RateLimiter = new PeerRateLimiter();

        public Peer(ConnectionId connection)
        {
            Connection = connection;
        }

        public override string ToString()
        {
            string name = string.IsNullOrEmpty(Name) ? "<pending>" : Name;
            string addr = string.IsNullOrEmpty(RemoteAddress) ? "" : ", " + RemoteAddress;
            return name + " (#" + PlayerId + ", " + Connection + addr + ")";
        }
    }
}
