using CS2MultiplayerMod.Core.Networking;

namespace CS2MultiplayerMod.Core.Session
{
    /// <summary>
    /// A participant in the session as seen by the local machine
    /// </summary>
    public sealed class Peer
    {
        public readonly ConnectionId Connection;

        /// <summary>Assigned by the host. 0 until the handshake completes.</summary>
        public int PlayerId;

        public string Name;

        /// <summary>True once the handshake has succeeded for this peer.</summary>
        public bool Handshaked;

        /// <summary>Local monotonic timestamp (Unix ms) of the last byte received from this peer.</summary>
        public long LastSeenUnixMs;

        /// <summary>When the underlying connection appeared - pending peers expire on this.</summary>
        public long ConnectedAtUnixMs;

        /// <summary>Most recent round-trip estimate in milliseconds, or -1 if unknown.</summary>
        public int LatencyMs = -1;

        /// <summary>Remote IP for logging/ban bookkeeping. May be null.</summary>
        public string RemoteAddress;

        /// <summary>Host-side: the one-time nonce sent in this peer's handshake challenge.</summary>
        public byte[] ChallengeNonce;

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
