using System;
using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Session;

namespace CS2MultiplayerMod.Game
{
    public sealed partial class MultiplayerService
    {
        /// <summary>
        /// Chat send from the hub panel. The session never echoes our own line back
        /// (the host only relays, a client only uploads), so the local copy is added
        /// here - sanitized exactly like the wire copy the other players will see.
        /// "/sync" stays a command and gets its feedback from the host's broadcast notice.
        /// </summary>
        public void SendChatFromUi(string text)
        {
            if (text == null || _session.Status != SessionStatus.Connected) return;
            text = text.Trim();
            if (text.Length == 0) return;

            if (!text.Equals("/sync", StringComparison.OrdinalIgnoreCase))
            {
                string echo = WireGuard.SanitizeText(text, WireGuard.MaxChatLength);
                if (echo.Length == 0) return;
                AppendChatEntry(_session.LocalPlayerName, echo);
            }
            _session.SendChat(text);
        }

        /// <summary>
        /// The in-game chat panel's font has no glyphs for common typographic
        /// punctuation (em/en dashes, ellipsis, curly quotes render as boxes), so
        /// every displayed line is mapped to plain ASCII equivalents first.
        /// </summary>
        private static string NormalizeForChatFont(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            System.Text.StringBuilder sb = null;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                string replacement = null;
                switch (c)
                {
                    case '–': // en dash
                    case '—': // em dash
                    case '―': // horizontal bar
                        replacement = "-"; break;
                    case '‘': // left single quote
                    case '’': // right single quote
                        replacement = "'"; break;
                    case '“': // left double quote
                    case '”': // right double quote
                    case '„': // low double quote
                        replacement = "\""; break;
                    case '…': // ellipsis
                        replacement = "..."; break;
                    case ' ': // no-break space
                        replacement = " "; break;
                }
                if (replacement != null && sb == null)
                {
                    sb = new System.Text.StringBuilder(text.Length + 8);
                    sb.Append(text, 0, i);
                }
                if (sb != null)
                {
                    if (replacement != null) sb.Append(replacement);
                    else sb.Append(c);
                }
            }
            return sb == null ? text : sb.ToString();
        }

        /// <summary><paramref name="sender"/> null marks a system/event line ("X joined.").</summary>
        private void AppendChatEntry(string sender, string text)
        {
            text = NormalizeForChatFont(text);
            sender = NormalizeForChatFont(sender);
            if (string.IsNullOrEmpty(text)) return;
            lock (_chatLock)
            {
                _chatLog.Add(new ChatLogEntry
                {
                    Id = _nextChatId++,
                    Sender = sender,
                    Text = text,
                    Time = DateTime.Now.ToString("HH:mm"),
                });
                if (_chatLog.Count > MaxChatEntries)
                    _chatLog.RemoveRange(0, _chatLog.Count - MaxChatEntries);
                _chatLogJson = BuildChatJson();
            }
        }

        /// <summary>Caller holds <see cref="_chatLock"/>.</summary>
        private string BuildChatJson()
        {
            var sb = new System.Text.StringBuilder(_chatLog.Count * 64 + 2);
            sb.Append('[');
            for (int i = 0; i < _chatLog.Count; i++)
            {
                if (i > 0) sb.Append(',');
                ChatLogEntry entry = _chatLog[i];
                sb.Append("{\"id\":").Append(entry.Id).Append(",\"sender\":");
                if (entry.Sender == null) sb.Append("null");
                else AppendJsonString(sb, entry.Sender);
                sb.Append(",\"text\":");
                AppendJsonString(sb, entry.Text);
                sb.Append(",\"time\":");
                AppendJsonString(sb, entry.Time);
                sb.Append('}');
            }
            sb.Append(']');
            return sb.ToString();
        }

        private static void AppendJsonString(System.Text.StringBuilder sb, string value)
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

    }
}
