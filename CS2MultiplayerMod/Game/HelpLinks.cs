using System;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Game.Diagnostics;

namespace CS2MultiplayerMod.Game
{
    /// <summary>
    /// Stable, allow-listed destinations for help buttons in the game UI. The UI sends
    /// only a relative page identifier; keeping the GitHub root and allow-list here
    /// prevents a compromised or stale UI bundle from opening an arbitrary URL.
    /// </summary>
    internal static class HelpLinks
    {
        // GitHub uses /tree/ for a directory and /blob/ for an individual Markdown
        // file. Open the latter so headings such as #mod-version-issues work too.
        private const string PageRoot =
            "https://github.com/Rollocraft/CS2MultiplayerMod/blob/master/help/";

        public const string ErrorReference = "errors-and-warnings.md";
        public const string Password = "errors-and-warnings.md#the-password-was-not-accepted";
        public const string ModVersion = "troubleshooting.md#mod-version-issues";
        public const string GameVersion = "troubleshooting.md#game-version-issues";
        public const string Dlc = "disable_dlc.md";
        public const string Mods = "mods.md";
        public const string SessionFull = "errors-and-warnings.md#this-multiplayer-session-is-full";
        public const string Relay = "errors-and-warnings.md#steam-relay-is-unavailable-or-the-join-code-is-invalid";
        public const string Address = "troubleshooting.md#connection-issues";
        public const string DirectConnection = "forwarding_troubleshoot.md";
        public const string Removed = "errors-and-warnings.md#the-host-removed-you";
        public const string Declined = "errors-and-warnings.md#the-host-did-not-let-you-in";
        public const string SharedWorldExit = "errors-and-warnings.md#could-not-close-the-shared-city";
        public const string WorldCopy = "errors-and-warnings.md#world-copy-errors";
        public const string Generic = "errors-and-warnings.md#multiplayer-could-not-complete-the-connection";
        public const string Troubleshooting = "troubleshooting.md";

        public static void Open(string page)
        {
            page = Allowed(page) ? page : ErrorReference;
            string url = PageRoot + page;
            try
            {
                UnityEngine.Application.OpenURL(url);
                SyncLog.Detail(LogTopic.Ui, "Opened help page: " + page);
            }
            catch (Exception ex)
            {
                SyncLog.Warn(LogTopic.Ui, "Could not open the help page " + url + ": " +
                    ex.Message);
            }
        }

        private static bool Allowed(string page)
        {
            switch (page)
            {
                case ErrorReference:
                case Password:
                case ModVersion:
                case GameVersion:
                case Dlc:
                case Mods:
                case SessionFull:
                case Relay:
                case Address:
                case DirectConnection:
                case Removed:
                case Declined:
                case SharedWorldExit:
                case WorldCopy:
                case Generic:
                case Troubleshooting:
                    return true;
                default:
                    return false;
            }
        }
    }
}
