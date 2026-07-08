using System;
using CS2MultiplayerMod.Localization;

namespace CS2MultiplayerMod.Game
{
    /// <summary>
    /// Tracks which Cities: Skylines II builds this mod has actually been tested
    /// against. The game's runtime behaviour (and therefore what the sync layer
    /// observes) can change between patches, so a build outside this list is flagged
    /// to the player as untested - a non-blocking warning banner in the Join dialog
    /// and the in-game hub. Multiplayer still works; the player is just told that
    /// things may break and to keep backups.
    /// </summary>
    public static class GameVersionCheck
    {
        /// <summary>
        /// Game builds verified to work with this mod. Update this list whenever the
        /// mod has been exercised against a new patch. Values match the build string
        /// the game reports (e.g. "1.6.0f1"), which is also logged on host/join.
        /// </summary>
        public static readonly string[] TestedVersions =
        {
            "1.6.0f1",
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

        /// <summary>
        /// True when the running build is not one we have tested. An unknown/empty
        /// version reads as untested so the player is warned rather than reassured.
        /// </summary>
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

        /// <summary>
        /// Localized warning sentence for the UI banner, or "" when the build is
        /// tested (the banner is hidden on an empty string). Built here so the
        /// runtime version values are substituted in the game's active language.
        /// </summary>
        public static string WarningText()
        {
            if (!IsUntested) return "";
            string current = CurrentVersion;
            return L10n.F(L10n.Key.UiVersionWarning,
                string.IsNullOrEmpty(current) ? "?" : current, TestedVersionsText);
        }
    }
}
