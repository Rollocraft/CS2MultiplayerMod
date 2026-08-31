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

        /// <summary>True once the handshake has succeeded for this peer.</summary>
        public bool Handshaked;

        /// <summary>Host-side: the join passed every automatic check and is waiting for the
        /// host to approve or decline it by hand. Never overlaps <see cref="Handshaked"/>.</summary>
        public bool AwaitingApproval;

        /// <summary>Local monotonic timestamp (Unix ms) of the last byte received from this peer.</summary>
        public long LastSeenUnixMs;

        /// <summary>When the underlying connection appeared - pending peers expire on this.</summary>
        public long ConnectedAtUnixMs;

        /// <summary>
        /// Smoothed round-trip estimate in milliseconds, or -1 before the first sample.
        /// This is the number to show a player: a single sample swings with whatever the OS
        /// was doing when the echo landed, and a readout that flickers between 20 and 90 tells
        /// nobody anything.
        /// </summary>
        public int LatencyMs = -1;

        /// <summary>Round-trip variation in milliseconds - the "is it steady" half of the story.</summary>
        public int JitterMs;

        // Jacobson/Karels, the same estimator TCP uses for its retransmit timer: a smoothed
        // round-trip time and a smoothed mean deviation, each pulled a fixed fraction of the
        // way towards the newest sample. The 1/8 and 1/4 gains are the standard ones.
        private const double SrttGain = 0.125;
        private const double RttVarGain = 0.25;

        private double _srttMs = -1.0;
        private double _rttVarMs;

        /// <summary>Fold one measured round-trip into the estimate.</summary>
        public void RecordRttSample(long rttMs)
        {
            if (_srttMs < 0)
            {
                // First sample: seed the estimator with it, and half of it as the deviation.
                _srttMs = rttMs;
                _rttVarMs = rttMs / 2.0;
            }
            else
            {
                double delta = rttMs - _srttMs;
                _srttMs += SrttGain * delta;
                _rttVarMs += RttVarGain * (System.Math.Abs(delta) - _rttVarMs);
            }

            LatencyMs = (int)System.Math.Round(_srttMs);
            JitterMs = (int)System.Math.Round(_rttVarMs);
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
