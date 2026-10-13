using System;
using System.Collections.Generic;

namespace CS2MultiplayerMod.Core.Sync
{
    /// <summary>
    /// FIFO keeping the newest value per key; replacing does not move the key back, so a busy key cannot
    /// starve others.
    /// </summary>
    public sealed class LatestByKeyQueue<TKey, TValue>
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

        public bool TryGetValue(TKey key, out TValue value)
        {
            if (_entries.TryGetValue(key, out Entry entry))
            {
                value = entry.Value;
                return true;
            }
            value = default(TValue);
            return false;
        }

        /// <summary>Adds or replaces; replacing works even at capacity.</summary>
        public bool TrySetLatest(TKey key, TValue value, int capacity)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));

            if (_entries.TryGetValue(key, out Entry existing))
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
            if (!_entries.TryGetValue(key, out Entry entry)) return false;
            _order.Remove(entry.Node);
            _entries.Remove(key);
            return true;
        }

        public void Clear()
        {
            _order.Clear();
            _entries.Clear();
        }

        /// <summary>Visit in FIFO order. The visitor may remove the current entry.</summary>
        public void Visit(Action<TKey, TValue> visitor)
        {
            for (var node = _order.First; node != null;)
            {
                var next = node.Next;
                visitor(node.Value, _entries[node.Value].Value);
                node = next;
            }
        }
    }
}
