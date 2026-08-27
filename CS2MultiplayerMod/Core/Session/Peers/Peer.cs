using CS2MultiplayerMod.Core.Networking;

namespace CS2MultiplayerMod.Core.Session
{
    /// <summary>
    /// A participant in the session as seen by the local machine
    /// </summary>
    public sealed class Peer
    {
        public readonly ConnectionId Connection;

        /// <summary>Assigned by the host. 0 until the host assigns one - at the approval
        /// prompt when approval is required, otherwise when the handshake completes.</summary>
        public int PlayerId;

        public string Name;

        public PlayerRole Role = PlayerRole.Builder;

        /// <summary>Host-side permission: true if the peer is in spectator/read-only mode.</summary>
        public bool IsSpectator
        {
            get => Role == PlayerRole.Spectator;
            set { if (value) Role = PlayerRole.Spectator; else if (Role == PlayerRole.Spectator) Role = PlayerRole.Builder; }
        }

        /// <summary>True once the handshake has succeeded for this peer.</summary>
        public bool Handshaked;

        /// <summary>Host-side: the join passed every automatic check and is waiting for the
        /// host to approve or decline it by hand. Never overlaps <see cref="Handshaked"/>.</summary>
        public bool AwaitingApproval;

        /// <summary>Local monotonic timestamp (Unix ms) of the last byte received from this peer.</summary>
        public long LastSeenUnixMs;

        /// <summary>When the underlying connection appeared - pending peers expire on this.</summary>
        public long ConnectedAtUnixMs;

        /// <summary>Most recent round-trip estimate in milliseconds, or -1 if unknown.</summary>
        public int LatencyMs = -1;

        /// <summary>Estimated jitter (RTT variance) in milliseconds.</summary>
        public int JitterMs;

        public double SrttMs = -1.0;
        public double RttVarMs;

        public void RecordRttSample(long rttMs)
        {
            LatencyMs = (int)rttMs;
            if (SrttMs < 0)
            {
                SrttMs = rttMs;
                RttVarMs = rttMs / 2.0;
            }
            else
            {
                double delta = rttMs - SrttMs;
                SrttMs += 0.125 * delta;
                RttVarMs += 0.25 * (System.Math.Abs(delta) - RttVarMs);
            }
            JitterMs = (int)RttVarMs;
        }

        /// <summary>Rolling connection quality: Excellent, Good, Fair, Poor.</summary>
        public string QualityRating
        {
            get
            {
                if (LatencyMs < 0) return "Unknown";
                if (LatencyMs <= 60 && JitterMs <= 15) return "Excellent";
                if (LatencyMs <= 140 && JitterMs <= 35) return "Good";
                if (LatencyMs <= 250) return "Fair";
                return "Poor";
            }
        }

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
