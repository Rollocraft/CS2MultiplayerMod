using System;
using System.Collections.Generic;
using System.Text;

namespace CS2MultiplayerMod.Game
{
    /// <summary>
    /// UI history, separate from the wire's chat format. System activity cannot evict player
    /// messages, generate chat unread counts, or repeat the same notice in a short burst.
    /// </summary>
    internal sealed class SessionFeed
    {
        internal const int MessageLimit = 120;
        internal const int ActivityLimit = 60;
        private readonly object _gate = new object();
        private readonly List<Entry> _entries = new List<Entry>();
        private int _nextId = 1;
        private string _json = "[]";
        private string _lastActivityText;
        private string _lastActivityKind;
        private DateTime _lastActivityAt;

        public string Json { get { lock (_gate) return _json; } }

        public void AddMessage(string sender, string text, bool isLocal) =>
            Add("message", sender, text, isLocal);

        public void AddActivity(string text, bool error = false) =>
            Add(error ? "error" : "activity", null, text, false);

        public void Clear()
        {
            lock (_gate)
            {
                _entries.Clear();
                _json = "[]";
                _lastActivityText = null;
                _lastActivityKind = null;
                // IDs remain increasing so a cleared session cannot reuse the UI's read marker.
            }
        }

        private void Add(string kind, string sender, string text, bool isLocal)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            bool message = kind == "message";
            lock (_gate)
            {
                DateTime now = DateTime.UtcNow;
                if (!message)
                {
                    if (_lastActivityText == text && _lastActivityKind == kind &&
                        (now - _lastActivityAt).TotalSeconds < 30) return;
                    _lastActivityText = text;
                    _lastActivityKind = kind;
                    _lastActivityAt = now;
                }

                _entries.Add(new Entry
                {
                    Id = _nextId++, Kind = kind, Sender = sender, Text = text,
                    IsLocal = isLocal, Time = now.ToLocalTime().ToString("HH:mm"),
                    Timestamp = new DateTimeOffset(now).ToUnixTimeMilliseconds(),
                });
                int count = 0;
                int first = -1;
                for (int i = 0; i < _entries.Count; i++)
                {
                    if ((_entries[i].Kind == "message") != message) continue;
                    if (first < 0) first = i;
                    count++;
                }
                if (count > (message ? MessageLimit : ActivityLimit)) _entries.RemoveAt(first);
                _json = BuildJson();
            }
        }

        private string BuildJson()
        {
            var sb = new StringBuilder(_entries.Count * 110 + 2).Append('[');
            for (int i = 0; i < _entries.Count; i++)
            {
                if (i > 0) sb.Append(',');
                Entry entry = _entries[i];
                sb.Append("{\"id\":").Append(entry.Id).Append(",\"kind\":");
                AppendJsonString(sb, entry.Kind);
                sb.Append(",\"isLocal\":").Append(entry.IsLocal ? "true" : "false");
                sb.Append(",\"sender\":");
                if (entry.Sender == null) sb.Append("null");
                else AppendJsonString(sb, entry.Sender);
                sb.Append(",\"text\":");
                AppendJsonString(sb, entry.Text);
                sb.Append(",\"time\":");
                AppendJsonString(sb, entry.Time);
                sb.Append(",\"timestamp\":").Append(entry.Timestamp);
                sb.Append('}');
            }
            return sb.Append(']').ToString();
        }

        internal static void AppendJsonString(StringBuilder sb, string value)
        {
            sb.Append('"');
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        private struct Entry
        {
            public int Id;
            public long Timestamp;
            public string Kind, Sender, Text, Time;
            public bool IsLocal;
        }
    }
}
