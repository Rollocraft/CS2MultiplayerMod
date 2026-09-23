using System;
using System.Collections.Generic;

namespace CS2MultiplayerMod.Core.Sync
{
    /// <summary>
    /// Time-bounded idempotence for operation replays; keys are remembered only after commit, so a
    /// failed operation stays retryable.
    /// </summary>
    public sealed class OperationReplayWindow<TKey>
    {
        private readonly Dictionary<TKey, long> _completed;
        private readonly List<TKey> _expired = new List<TKey>();
        // A lower bound: may be early, never delays another key's expiry.
        private long _nextExpiry = long.MaxValue;

        public OperationReplayWindow() : this(null) { }

        public OperationReplayWindow(IEqualityComparer<TKey> comparer)
        {
            _completed = new Dictionary<TKey, long>(comparer ?? EqualityComparer<TKey>.Default);
        }

        public int Count => _completed.Count;

        public bool Contains(TKey key, long now)
        {
            if (!_completed.TryGetValue(key, out long expires)) return false;
            if (expires > now) return true;
            _completed.Remove(key);
            return false;
        }

        public void Remember(TKey key, long now, long duration)
        {
            if (duration <= 0) throw new ArgumentOutOfRangeException(nameof(duration));
            long expires = now > long.MaxValue - duration ? long.MaxValue : now + duration;
            _completed[key] = expires;
            if (expires < _nextExpiry) _nextExpiry = expires;
        }

        public void Prune(long now)
        {
            if (_completed.Count == 0 || now < _nextExpiry) return;
            _nextExpiry = long.MaxValue;
            foreach (KeyValuePair<TKey, long> pair in _completed)
            {
                if (pair.Value <= now) _expired.Add(pair.Key);
                else if (pair.Value < _nextExpiry) _nextExpiry = pair.Value;
            }
            for (int i = 0; i < _expired.Count; i++) _completed.Remove(_expired[i]);
            _expired.Clear();
        }

        public void Clear()
        {
            _completed.Clear();
            _expired.Clear();
            _nextExpiry = long.MaxValue;
        }
    }
}
