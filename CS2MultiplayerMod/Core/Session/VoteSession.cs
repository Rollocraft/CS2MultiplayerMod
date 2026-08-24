using System;
using System.Collections.Concurrent;

namespace CS2MultiplayerMod.Core.Session
{
    /// <summary>
    /// Manages active democratic vote-kick sessions in multiplayer lobbies.
    /// </summary>
    public sealed class VoteSession
    {
        public int TargetPlayerId { get; private set; }
        public string TargetPlayerName { get; private set; }
        public string InitiatorName { get; private set; }
        public long ExpireMs { get; private set; }
        public bool IsActive => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < ExpireMs;

        private readonly ConcurrentDictionary<int, bool> _votes =
            new ConcurrentDictionary<int, bool>();

        public void StartVote(int targetPlayerId, string targetName, string initiatorName, int durationSeconds = 30)
        {
            TargetPlayerId = targetPlayerId;
            TargetPlayerName = targetName;
            InitiatorName = initiatorName;
            ExpireMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + (durationSeconds * 1000L);
            _votes.Clear();
        }

        public void CastVote(int voterPlayerId, bool voteYes)
        {
            if (!IsActive) return;
            _votes[voterPlayerId] = voteYes;
        }

        public (int yesVotes, int noVotes) GetTally()
        {
            int yes = 0, no = 0;
            foreach (var v in _votes.Values)
            {
                if (v) yes++;
                else no++;
            }
            return (yes, no);
        }

        public void Clear()
        {
            ExpireMs = 0;
            _votes.Clear();
        }
    }
}
