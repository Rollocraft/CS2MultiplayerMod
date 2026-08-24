using System;
using System.Collections.Concurrent;

namespace CS2MultiplayerMod.Core.Protocol
{
    /// <summary>
    /// Sliding-window 64-bit idempotency filter that detects and discards duplicate
    /// simulation command sequence numbers caused by network retransmission.
    /// </summary>
    public sealed class CommandDeduplicator
    {
        private readonly ConcurrentDictionary<int, PeerHistory> _peerHistories =
            new ConcurrentDictionary<int, PeerHistory>();

        public bool ShouldProcess(int playerId, uint sequenceId)
        {
            PeerHistory history = _peerHistories.GetOrAdd(playerId, _ => new PeerHistory());
            lock (history)
            {
                if (sequenceId > history.MaxSequence)
                {
                    uint advance = sequenceId - history.MaxSequence;
                    if (advance >= 64)
                    {
                        history.Bitmask = 1;
                    }
                    else
                    {
                        history.Bitmask = (history.Bitmask << (int)advance) | 1UL;
                    }
                    history.MaxSequence = sequenceId;
                    return true;
                }

                uint diff = history.MaxSequence - sequenceId;
                if (diff >= 64)
                {
                    // Too old, drop
                    return false;
                }

                ulong bit = 1UL << (int)diff;
                if ((history.Bitmask & bit) != 0)
                {
                    // Duplicate sequence!
                    return false;
                }

                history.Bitmask |= bit;
                return true;
            }
        }

        public void Clear()
        {
            _peerHistories.Clear();
        }

        private sealed class PeerHistory
        {
            public uint MaxSequence;
            public ulong Bitmask;
        }
    }
}
