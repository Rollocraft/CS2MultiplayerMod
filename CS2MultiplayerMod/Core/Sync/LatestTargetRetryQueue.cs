using System;

namespace CS2MultiplayerMod.Core.Sync
{
    /// <summary>Bounded latest-per-target retries, with deadlines measured only while progress is allowed.</summary>
    public sealed class LatestTargetRetryQueue<TKey, TValue>
    {
        private sealed class Entry
        {
            public TValue Value;
            public long Deadline;
        }

        private readonly int _capacity;
        private readonly long _timeoutMs;
        private readonly ActiveRetryClock _clock = new ActiveRetryClock();
        private readonly LatestByKeyQueue<TKey, Entry> _entries = new LatestByKeyQueue<TKey, Entry>();

        public LatestTargetRetryQueue(int capacity, long timeoutMs)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            if (timeoutMs <= 0) throw new ArgumentOutOfRangeException(nameof(timeoutMs));
            _capacity = capacity;
            _timeoutMs = timeoutMs;
        }

        private bool _held;

        public int Count => _entries.Count;
        public void Observe(long nowMs, bool held)
        {
            _held = held;
            _clock.Observe(nowMs, held);
        }

        /// <summary>False means the oldest different target was evicted; the new value is retained.</summary>
        public bool SetLatest(TKey key, TValue value)
        {
            var entry = new Entry { Value = value, Deadline = _clock.NowMs + _timeoutMs };
            if (_entries.TrySetLatest(key, entry, _capacity)) return true;
            _entries.TryTake(out _, out _);
            _entries.TrySetLatest(key, entry, _capacity);
            return false;
        }

        public void Pump(Func<TValue, bool> tryApply, Action<TValue> onExpired)
        {
            _entries.Visit((key, entry) =>
            {
                if (tryApply(entry.Value)) _entries.Remove(key);
                else if (!_held && _clock.NowMs >= entry.Deadline)
                {
                    _entries.Remove(key);
                    onExpired(entry.Value);
                }
            });
        }

        public bool Remove(TKey key) => _entries.Remove(key);

        public void Clear()
        {
            _entries.Clear();
            _clock.Reset();
            _held = false;
        }
    }
}
