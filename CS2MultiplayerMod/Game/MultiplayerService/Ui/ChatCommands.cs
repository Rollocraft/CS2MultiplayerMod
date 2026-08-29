using System;
using CS2MultiplayerMod.Core.Session;

namespace CS2MultiplayerMod.Game
{
    public sealed partial class MultiplayerService
    {
        /// <summary>
        /// Slash commands typed into the chat box.
        ///
        /// Returns true when the line was a command and has been dealt with, in which case the
        /// caller must not also send it as chat. An unrecognised word starting with "/" is not
        /// claimed here - it goes out as ordinary text, because refusing it would make a typo
        /// vanish silently, and people do type "/" mid-sentence.
        ///
        /// "/sync" is deliberately absent: it is handled by the session, which broadcasts the
        /// request and reports the outcome from the host's side.
        /// </summary>
        private bool TryHandleChatCommand(string text)
        {
            if (string.IsNullOrEmpty(text) || text[0] != '/') return false;

            string verb, argument;
            SplitCommand(text, out verb, out argument);

            switch (verb)
            {
                case "/help":
                case "/commands":
                    ShowCommandHelp();
                    return true;

                case "/clear":
                    ClearChatLog();
                    return true;

                case "/ping":
                    if (!GameplaySyncReady)
                    {
                        AppendSystemChat("Ping needs a live session with the city loaded.");
                        return true;
                    }
                    SendMapPing(argument);
                    return true;

                case "/goto":
                    HandleGoto(argument);
                    return true;

                case "/follow":
                    HandleFollow(argument);
                    return true;

                case "/unfollow":
                    if (FollowPlayerId < 0) AppendSystemChat("Not following anyone.");
                    else
                    {
                        AppendSystemChat("Stopped following " + PlayerDisplayName(FollowPlayerId) + ".");
                        StopFollowing();
                    }
                    return true;

                case "/lock":
                case "/unlock":
                    HandleLobbyLock(verb == "/lock");
                    return true;

                case "/banlist":
                    HandleBanList();
                    return true;

                case "/unban":
                    HandleUnban(argument);
                    return true;

                default:
                    return false;
            }
        }

        /// <summary>Split "/verb rest of the line" into a lowercased verb and its argument.</summary>
        private static void SplitCommand(string text, out string verb, out string argument)
        {
            int space = text.IndexOf(' ');
            if (space < 0)
            {
                verb = text.ToLowerInvariant();
                argument = string.Empty;
                return;
            }
            verb = text.Substring(0, space).ToLowerInvariant();
            argument = text.Substring(space + 1).Trim();
        }

        private void ShowCommandHelp()
        {
            AppendSystemChat("Commands:");
            AppendSystemChat("  /ping [note]      drop a marker where you are looking");
            AppendSystemChat("  /goto <player>    jump the camera to a player ('/goto ping' for the last ping)");
            AppendSystemChat("  /follow <player>  keep the camera on a player; move your camera to stop");
            AppendSystemChat("  /unfollow         stop following");
            AppendSystemChat("  /sync             ask for a fresh copy of the city");
            AppendSystemChat("  /clear            clear this chat log (yours only)");
            if (_session.Role == SessionRole.Host)
            {
                AppendSystemChat("Host only:");
                AppendSystemChat("  /lock, /unlock    refuse or allow new players");
                AppendSystemChat("  /banlist          list addresses banned this session");
                AppendSystemChat("  /unban <address>  lift one of those bans");
            }
        }

        private void ClearChatLog()
        {
            lock (_chatLock)
            {
                _chatLog.Clear();
                _chatLogJson = "[]";
            }
            AppendSystemChat("Chat cleared. This only clears your own log.");
        }

        private void HandleGoto(string argument)
        {
            if (argument.Length == 0)
            {
                AppendSystemChat("Usage: /goto <player>, or /goto ping.");
                return;
            }

            if (argument.Equals("ping", StringComparison.OrdinalIgnoreCase))
            {
                Unity.Mathematics.float3 lastPing;
                if (!TryGetLastPing(out lastPing))
                {
                    AppendSystemChat("No pings yet this session.");
                    return;
                }
                StopFollowing();
                RequestCameraJump(lastPing);
                AppendSystemChat("Moved to the last ping.");
                return;
            }

            RemotePlayer target = FindRemotePlayerByName(argument);
            if (target == null)
            {
                AppendSystemChat("No player matches '" + argument + "'. Try their exact name or id.");
                return;
            }

            StopFollowing();
            RequestCameraJump(new Unity.Mathematics.float3(target.X, target.Y, target.Z));
            AppendSystemChat("Moved to " + PlayerDisplayName(target.PlayerId) + ".");
        }

        private void HandleFollow(string argument)
        {
            if (argument.Length == 0)
            {
                AppendSystemChat("Usage: /follow <player>.");
                return;
            }

            RemotePlayer target = FindRemotePlayerByName(argument);
            if (target == null)
            {
                AppendSystemChat("No player matches '" + argument + "'. Try their exact name or id.");
                return;
            }

            RequestCameraJump(new Unity.Mathematics.float3(target.X, target.Y, target.Z));
            StartFollowing(target.PlayerId);
            AppendSystemChat("Following " + PlayerDisplayName(target.PlayerId) +
                             ". Move your camera to stop.");
        }

        private void HandleLobbyLock(bool locked)
        {
            if (_session.Role != SessionRole.Host)
            {
                AppendSystemChat("Only the host can lock the session.");
                return;
            }
            if (_session.IsLobbyLocked == locked)
            {
                AppendSystemChat(locked ? "Already locked." : "Already unlocked.");
                return;
            }
            _session.IsLobbyLocked = locked;
            AppendSystemChat(locked
                ? "Locked. New players are refused; everyone already here stays connected."
                : "Unlocked. New players can join again.");
        }

        private void HandleBanList()
        {
            if (_session.Role != SessionRole.Host)
            {
                AppendSystemChat("Only the host holds the ban list.");
                return;
            }
            var bans = _session.BannedAddresses;
            if (bans.Count == 0)
            {
                AppendSystemChat("No addresses banned this session.");
                return;
            }
            AppendSystemChat("Banned this session: " + string.Join(", ", bans));
        }

        private void HandleUnban(string argument)
        {
            if (_session.Role != SessionRole.Host)
            {
                AppendSystemChat("Only the host holds the ban list.");
                return;
            }
            if (argument.Length == 0)
            {
                AppendSystemChat("Usage: /unban <address>. Use /banlist to see them.");
                return;
            }
            AppendSystemChat(_session.UnbanAddress(argument)
                ? "Unbanned " + argument + "."
                : "'" + argument + "' is not in the ban list.");
        }
    }
}
