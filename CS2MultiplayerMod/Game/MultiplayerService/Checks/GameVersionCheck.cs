using System;
using CS2MultiplayerMod.Localization;

namespace CS2MultiplayerMod.Game
{
    /// <summary>Flags a game build this mod was not tested against, as a non-blocking banner.</summary>
    public static class GameVersionCheck
    {
        /// <summary>Tested game builds, as the game reports them (e.g. "1.6.0f1").</summary>
        public static readonly string[] TestedVersions =
        {
            "1.6.0f1",
            "1.6.2f1",
        };

        /// <summary>The running game build, or "" if it cannot be read.</summary>
        public static string CurrentVersion
        {
            get
            {
                try { return UnityEngine.Application.version ?? ""; }
                catch (Exception) { return ""; }
            }
        }

        /// <summary>Comma-separated tested builds, for display in the warning text.</summary>
        public static string TestedVersionsText => string.Join(", ", TestedVersions);

        /// <summary>An unknown or empty version reads as untested.</summary>
        public static bool IsUntested
        {
            get
            {
                string current = CurrentVersion;
                if (string.IsNullOrEmpty(current)) return true;
                foreach (string v in TestedVersions)
                    if (string.Equals(v, current, StringComparison.OrdinalIgnoreCase))
                        return false;
                return true;
            }
        }

        /// <summary>Localized banner text, or "" when tested (hides the banner).</summary>
        public static string WarningText()
        {
            if (!IsUntested) return "";
            string current = CurrentVersion;
            return L10n.F(L10n.Key.UiVersionWarning,
                string.IsNullOrEmpty(current) ? "?" : current, TestedVersionsText);
        }
    }
}
