using System;
using System.Collections.Generic;

namespace CS2MultiplayerMod.Game.Sync.Infrastructure
{
    /// <summary>
    /// FIFO work queue that keeps only the newest value for each key. Replacing queued work does
    /// not move it to the back, so a frequently updated key cannot starve older distinct keys.
    /// </summary>
    internal sealed class LatestByKeyQueue<TKey, TValue>
    {
        private sealed class Entry
        {
            public TValue Value;
            public LinkedListNode<TKey> Node;
        }

        private readonly LinkedList<TKey> _order = new LinkedList<TKey>();
        private readonly Dictionary<TKey, Entry> _entries = new Dictionary<TKey, Entry>();

        public int Count => _entries.Count;

        public bool ContainsKey(TKey key) => _entries.ContainsKey(key);

        /// <summary>
        /// Add a new key or replace its queued value. Existing keys can always be replaced even
        /// when the queue is at capacity.
        /// </summary>
        public bool TrySetLatest(TKey key, TValue value, int capacity)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));

            Entry existing;
            if (_entries.TryGetValue(key, out existing))
            {
                existing.Value = value;
                return true;
            }

            if (_entries.Count >= capacity) return false;

            var node = _order.AddLast(key);
            _entries.Add(key, new Entry { Value = value, Node = node });
            return true;
        }

        public bool TryTake(out TKey key, out TValue value)
        {
            LinkedListNode<TKey> node = _order.First;
            if (node == null)
            {
                key = default(TKey);
                value = default(TValue);
                return false;
            }

            key = node.Value;
            Entry entry = _entries[key];
            value = entry.Value;
            _order.Remove(node);
            _entries.Remove(key);
            return true;
        }

        public bool Remove(TKey key)
        {
            Entry entry;
            if (!_entries.TryGetValue(key, out entry)) return false;
            _order.Remove(entry.Node);
            _entries.Remove(key);
            return true;
        }

        public void Clear()
        {
            _order.Clear();
            _entries.Clear();
        }
    }
}
