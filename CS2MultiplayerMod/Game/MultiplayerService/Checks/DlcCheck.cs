using System;
using System.Collections.Generic;
using Colossal.PSI.Common;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Protocol;

namespace CS2MultiplayerMod.Game
{
    /// <summary>
    /// Owned sync-relevant DLCs for the handshake (idea from CS2M): differing content DLCs mean
    /// differing prefab catalogues. Radio DLCs are music only and excluded.
    /// </summary>
    internal static class DlcCheck
    {
        /// <summary>
        /// Not compared: radio packs (music only) and CS1TreasureHunt, a reward the store cannot turn off,
        /// though its assets can still desync a player who lacks it.
        /// </summary>
        private static readonly string[] IgnoredDlcs =
            {
                "AtmosphericPianoRadio",
                "DeluxeRelaxRadio",
                "FeelgoodFunkRadio",
                "JadeRoadRadio",
                "CloudLoungeFMRadio",
                "ColdWaveRadio",
                "SkyrailRadio",
                "SoftRockRadio",
                "SynthAndSteelRadio",
                "CS1TreasureHunt"
            };

        /// <summary>
        /// Canonical names sorted ordinally, so equal content gives identical lists. Empty on failure, which
        /// the handshake treats as a real empty set.
        /// </summary>
        public static string[] OwnedSyncRelevantDlcs(IModLogger log)
        {
            try
            {
                var names = new List<string>();
                var seenIds = new HashSet<int>();
                foreach (IDlc dlc in PlatformManager.instance.EnumerateDLCs())
                {
                    // One entry per store backend; negative ids are reserved, not content.
                    if (dlc.id.id < 0 || !seenIds.Add(dlc.id.id)) continue;
                    if (!PlatformManager.instance.IsDlcOwned(dlc)) continue;
                    if (IsIgnored(dlc.internalName)) continue;

                    string name = CanonicalName(dlc);
                    if (name.Length > 0) names.Add(name);

                    if (names.Count >= ProtocolConstants.MaxDlcEntries) break;
                }

                names.Sort(StringComparer.Ordinal);
                return names.ToArray();
            }
            catch (Exception ex)
            {
                log.Warn(LogTopic.Startup, "Could not enumerate DLCs (" + ex.Message + "); " +
                    "reporting no DLCs, so peers reporting DLC content will be rejected.");
                return Array.Empty<string>();
            }
        }

        private static bool IsIgnored(string internalName)
        {
            for (int i = 0; i < IgnoredDlcs.Length; i++)
                if (string.Equals(IgnoredDlcs[i], internalName, StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>
        /// <see cref="IDlc.internalName"/> is the only machine-neutral name (store names vary by language and
        /// backend); the id backs up a nameless entry.
        /// </summary>
        private static string CanonicalName(IDlc dlc)
        {
            string name = dlc.internalName;
            if (string.IsNullOrEmpty(name)) return "#" + dlc.id.id;

            name = name.Trim();
            if (name.Length > ProtocolConstants.MaxDlcNameLength)
                name = name.Substring(0, ProtocolConstants.MaxDlcNameLength);
            return name;
        }
    }
}
