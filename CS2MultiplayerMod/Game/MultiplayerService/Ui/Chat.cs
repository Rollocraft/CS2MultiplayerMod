using System;
using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Session;

namespace CS2MultiplayerMod.Game
{
    public sealed partial class MultiplayerService
    {
        /// <summary>
        /// Chat from the hub. The session never echoes our own line, so the local copy is added here,
        /// sanitized like the wire copy. "/sync" stays a command; "/diag" never leaves this machine.
        /// </summary>
        public void SendChatFromUi(string text)
        {
            if (text == null || _session.Status != SessionStatus.Connected) return;
            text = text.Trim();
            if (text.Length == 0) return;

            if (text.Equals("/diag", StringComparison.OrdinalIgnoreCase))
            {
                ExportDiagnostics("/diag in chat");
                AppendSessionActivity(DiagnosticsStatusText);
                return;
            }

            if (!text.Equals("/sync", StringComparison.OrdinalIgnoreCase))
            {
                string echo = WireGuard.SanitizeText(text, WireGuard.MaxChatLength);
                if (echo.Length == 0) return;
                AppendPlayerChat(_session.LocalPlayerName, echo, isLocal: true);
            }
            _session.SendChat(text);
        }

        /// <summary>The chat font lacks dashes, ellipsis and curly quotes; map them to ASCII.</summary>
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

        private void AppendPlayerChat(string sender, string text, bool isLocal = false) =>
            _sessionFeed.AddMessage(NormalizeForChatFont(sender), NormalizeForChatFont(text), isLocal);

        private void AppendSessionActivity(string text, bool error = false) =>
            _sessionFeed.AddActivity(NormalizeForChatFont(text), error);

        private static void AppendJsonString(System.Text.StringBuilder sb, string value) =>
            SessionFeed.AppendJsonString(sb, value);
    }
}
