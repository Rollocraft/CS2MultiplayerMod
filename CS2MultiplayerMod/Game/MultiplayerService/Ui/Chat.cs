using System;
using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Session;

namespace CS2MultiplayerMod.Game
{
    public sealed partial class MultiplayerService
    {
        public static event Action<Unity.Mathematics.float3, string, string, int> OnMapPingReceived;
        public static Unity.Mathematics.float3 LastMapPingPosition;
        public static bool HasMapPingPosition;

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

            text = text.Replace(":thumb:", "[Thumb]")
                       .Replace(":warn:", "[Warning]")
                       .Replace(":build:", "[Build]")
                       .Replace(":fire:", "[Alert]")
                       .Replace(":idea:", "[Idea]")
                       .Replace(":heart:", "[Heart]")
                       .Replace(":car:", "[Car]")
                       .Replace(":train:", "[Train]");

            if (text.Equals("/ping", StringComparison.OrdinalIgnoreCase) || text.StartsWith("/ping ", StringComparison.OrdinalIgnoreCase))
            {
                string label = text.Length > 5 ? text.Substring(5).Trim() : "";
                Unity.Mathematics.float3 pivot = Unity.Mathematics.float3.zero;
                var camera = Unity.Entities.World.DefaultGameObjectInjectionWorld?.GetExistingSystemManaged<global::Game.Rendering.CameraUpdateSystem>();
                if (camera?.gamePlayController != null)
                {
                    pivot = camera.gamePlayController.pivot;
                }
                else if (camera != null)
                {
                    pivot = camera.position;
                }

                SendPing(pivot, label);
                return;
            }

            if (text.Equals("/clear", StringComparison.OrdinalIgnoreCase) || text.Equals("/cls", StringComparison.OrdinalIgnoreCase))
            {
                lock (_chatLock)
                {
                    _chatLog.Clear();
                    _chatLogJson = "[]";
                }
                AppendChatEntry(null, "Chat cleared.");
                return;
            }

            if (text.Equals("/help", StringComparison.OrdinalIgnoreCase) || text.Equals("/?", StringComparison.OrdinalIgnoreCase) || text.Equals("/commands", StringComparison.OrdinalIgnoreCase))
            {
                AppendChatEntry(null, "=== Multiplayer Commands ===");
                AppendChatEntry(null, "- /ping [msg] - Ping map location with coordinates");
                AppendChatEntry(null, "- /goto <player> - Teleport camera to a player");
                AppendChatEntry(null, "- /goto ping - Teleport camera to latest ping");
                AppendChatEntry(null, "- /follow <player> - Follow a player in real-time");
                AppendChatEntry(null, "- /unfollow - Stop following a player");
                AppendChatEntry(null, "- /sync - Manually trigger simulation resync");
                AppendChatEntry(null, "- /clear - Clear chat messages");
                if (_session.Role == SessionRole.Host)
                {
                    AppendChatEntry(null, "--- Host Commands ---");
                    AppendChatEntry(null, "- /spectator <player> [on/off] - Toggle spectator mode for player");
                    AppendChatEntry(null, "- /lock - Lock lobby from new joins");
                    AppendChatEntry(null, "- /unlock - Unlock lobby for new joins");
                    AppendChatEntry(null, "- /motd [msg] - Set/clear message of the day");
                    AppendChatEntry(null, "- /banlist - View banned IP addresses");
                    AppendChatEntry(null, "- /unban <ip> - Unban an IP address");
                }
                return;
            }

            if (text.Equals("/goto", StringComparison.OrdinalIgnoreCase) || text.Equals("/goto ping", StringComparison.OrdinalIgnoreCase))
            {
                if (HasMapPingPosition)
                {
                    Sync.Players.PlayerCursorSyncSystem.FollowPlayerId = -1;
                    Sync.Players.PlayerCursorSyncSystem.TeleportCameraTo(LastMapPingPosition);
                    AppendChatEntry(null, "Teleported camera to last map ping.");
                }
                else
                {
                    AppendChatEntry(null, "No map pings yet. Use '/ping' or '/goto <player>'.");
                }
                return;
            }

            if (text.StartsWith("/goto ", StringComparison.OrdinalIgnoreCase))
            {
                string targetName = text.Substring(6).Trim();
                if (targetName.Equals("ping", StringComparison.OrdinalIgnoreCase))
                {
                    if (HasMapPingPosition)
                    {
                        Sync.Players.PlayerCursorSyncSystem.FollowPlayerId = -1;
                        Sync.Players.PlayerCursorSyncSystem.TeleportCameraTo(LastMapPingPosition);
                        AppendChatEntry(null, "Teleported camera to last map ping.");
                    }
                    else
                    {
                        AppendChatEntry(null, "No map pings yet. Use '/ping' or '/goto <player>'.");
                    }
                    return;
                }

                RemotePlayer target = FindRemotePlayerByName(targetName);
                if (target != null)
                {
                    Sync.Players.PlayerCursorSyncSystem.FollowPlayerId = -1;
                    Sync.Players.PlayerCursorSyncSystem.TeleportCameraTo(new Unity.Mathematics.float3(target.X, target.Y, target.Z));
                    AppendChatEntry(null, "Teleported camera to " + (target.Name ?? ("Player #" + target.PlayerId)) + ".");
                    return;
                }

                AppendChatEntry(null, "Player '" + targetName + "' not found.");
                return;
            }

            if (text.StartsWith("/follow ", StringComparison.OrdinalIgnoreCase))
            {
                string targetName = text.Substring(8).Trim();
                RemotePlayer target = FindRemotePlayerByName(targetName);
                if (target != null)
                {
                    Sync.Players.PlayerCursorSyncSystem.FollowPlayerId = target.PlayerId;
                    Sync.Players.PlayerCursorSyncSystem.TeleportCameraTo(new Unity.Mathematics.float3(target.X, target.Y, target.Z));
                    AppendChatEntry(null, "Now following " + (target.Name ?? ("Player #" + target.PlayerId)) + ". Move camera to stop following.");
                }
                else
                {
                    AppendChatEntry(null, "Player '" + targetName + "' not found.");
                }
                return;
            }

            if (text.Equals("/unfollow", StringComparison.OrdinalIgnoreCase))
            {
                Sync.Players.PlayerCursorSyncSystem.FollowPlayerId = -1;
                AppendChatEntry(null, "Stopped following.");
                return;
            }

            if (text.StartsWith("/spectator ", StringComparison.OrdinalIgnoreCase))
            {
                if (_session.Role != SessionRole.Host)
                {
                    AppendChatEntry(null, "Only the host can change player roles.");
                    return;
                }
                string args = text.Substring(11).Trim();
                bool isSpectator = true;
                string targetName = args;

                if (args.EndsWith(" off", StringComparison.OrdinalIgnoreCase) || args.EndsWith(" false", StringComparison.OrdinalIgnoreCase))
                {
                    isSpectator = false;
                    int lastSpace = args.LastIndexOf(' ');
                    targetName = lastSpace > 0 ? args.Substring(0, lastSpace).Trim() : args;
                }
                else if (args.EndsWith(" on", StringComparison.OrdinalIgnoreCase) || args.EndsWith(" true", StringComparison.OrdinalIgnoreCase))
                {
                    isSpectator = true;
                    int lastSpace = args.LastIndexOf(' ');
                    targetName = lastSpace > 0 ? args.Substring(0, lastSpace).Trim() : args;
                }

                RemotePlayer target = FindRemotePlayerByName(targetName);
                if (target != null)
                {
                    SetPlayerRoleFromUi(target.PlayerId, isSpectator: isSpectator);
                }
                else
                {
                    AppendChatEntry(null, "Player '" + targetName + "' not found.");
                }
                return;
            }

            if (text.Equals("/lock", StringComparison.OrdinalIgnoreCase))
            {
                if (_session.Role != SessionRole.Host)
                {
                    AppendChatEntry(null, "Only the host can lock the session.");
                    return;
                }
                _session.IsLobbyLocked = true;
                AppendChatEntry(null, "Session locked. New players cannot join.");
                return;
            }

            if (text.Equals("/unlock", StringComparison.OrdinalIgnoreCase))
            {
                if (_session.Role != SessionRole.Host)
                {
                    AppendChatEntry(null, "Only the host can unlock the session.");
                    return;
                }
                _session.IsLobbyLocked = false;
                AppendChatEntry(null, "Session unlocked. New players can join.");
                return;
            }

            if (text.StartsWith("/motd", StringComparison.OrdinalIgnoreCase))
            {
                if (_session.Role != SessionRole.Host)
                {
                    AppendChatEntry(null, "Only the host can configure MOTD.");
                    return;
                }
                string msg = text.Length > 5 ? text.Substring(5).Trim() : "";
                _session.Motd = msg;
                if (!string.IsNullOrEmpty(msg))
                {
                    AppendChatEntry(null, "MOTD updated: " + msg);
                    _session.SendChat("MOTD: " + msg);
                }
                else
                {
                    AppendChatEntry(null, "MOTD cleared.");
                }
                return;
            }

            if (text.Equals("/banlist", StringComparison.OrdinalIgnoreCase))
            {
                if (_session.Role != SessionRole.Host)
                {
                    AppendChatEntry(null, "Only the host can view the ban list.");
                    return;
                }
                var bans = _session.BannedAddresses;
                if (bans == null || bans.Count == 0)
                {
                    AppendChatEntry(null, "No active bans.");
                }
                else
                {
                    AppendChatEntry(null, "Active bans: " + string.Join(", ", bans));
                }
                return;
            }

            if (text.StartsWith("/unban ", StringComparison.OrdinalIgnoreCase))
            {
                if (_session.Role != SessionRole.Host)
                {
                    AppendChatEntry(null, "Only the host can unban.");
                    return;
                }
                string addr = text.Substring(7).Trim();
                if (_session.UnbanAddress(addr))
                {
                    AppendChatEntry(null, "Unbanned address: " + addr);
                }
                else
                {
                    AppendChatEntry(null, "Address '" + addr + "' was not in the ban list.");
                }
                return;
            }

            if (text.StartsWith("/chirp ", StringComparison.OrdinalIgnoreCase))
            {
                string chirpText = text.Substring(7).Trim();
                var chirperSys = Unity.Entities.World.DefaultGameObjectInjectionWorld?.GetExistingSystemManaged<Sync.Systems.ChirperSyncSystem>();
                if (chirperSys != null && !string.IsNullOrEmpty(chirpText))
                {
                    chirperSys.PostChirp("Mayor", chirpText);
                    AppendChatEntry(null, "Chirped: \"" + chirpText + "\"");
                }
                return;
            }

            if (text.StartsWith("/audit", StringComparison.OrdinalIgnoreCase))
            {
                var recent = AuditLog.GetRecent(5);
                if (recent.Count == 0)
                {
                    AppendChatEntry(null, "Municipal audit log is empty.");
                }
                else
                {
                    AppendChatEntry(null, "Recent municipal actions:");
                    foreach (var e in recent)
                    {
                        AppendChatEntry(null, $"  • [{e.PlayerName}] {e.Action}: {e.Details}");
                    }
                }
                return;
            }

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
                    case '•': // bullet
                    case '·': // middle dot
                        replacement = "-"; break;
                    case '₡': // colon currency
                        replacement = "$"; break;
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

        public void SendPing(Unity.Mathematics.float3 pivot, string label = "")
        {
            if (_session == null || _session.Role == SessionRole.None) return;
            int localId = _session.LocalPlayerId;
            string wire = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "/ping {0:F1} {1:F1} {2:F1} {3}{4}",
                pivot.x, pivot.y, pivot.z, localId, string.IsNullOrEmpty(label) ? "" : " " + label);

            _session.SendChat(wire);
            LastMapPingPosition = pivot;
            HasMapPingPosition = true;
            OnMapPingReceived?.Invoke(pivot, _session.LocalPlayerName, label, localId);
            string echo = "Pinged map at (" + (int)pivot.x + ", " + (int)pivot.z + ")" +
                          (string.IsNullOrEmpty(label) ? "" : ": " + label);
            AppendChatEntry(_session.LocalPlayerName, echo);
        }
    }
}
