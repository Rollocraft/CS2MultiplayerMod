using System;
using System.Collections.Generic;
using Colossal.PSI.Common;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Protocol;

namespace CS2MultiplayerMod.Game
{
    /// <summary>
    /// Enumerates the sync-relevant DLCs this machine owns, in canonical form for the
    /// handshake's preconditions check (idea ported from the CS2M project): host and
    /// client must own the same content DLCs, or their prefab catalogues differ and
    /// every placement of a DLC asset desyncs the other side.
    ///
    /// Radio-station DLCs are purely client-side (music only, no prefabs), so they are
    /// excluded - owning different radio packs is fine.
    /// </summary>
    internal static class DlcCheck
    {
        /// <summary>
        /// Not compared. The radio packs are music only, with no effect on the simulation
        /// (CS2M's verified list). CS1TreasureHunt is a Cities: Skylines 1 ownership reward
        /// that the store offers no way to turn off, so blocking on it would leave the two
        /// players nothing to change; it is content, so its assets can still desync a player
        /// who lacks it.
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
        /// The owned, sync-relevant DLC names: canonical, sorted ordinally so host and
        /// client produce byte-identical lists for equal content.
        /// Returns an empty array when enumeration fails. The handshake compares that
        /// as a real empty set, so a peer reporting any DLC is rejected rather than
        /// being admitted with an unverified prefab catalogue.
        /// </summary>
        public static string[] OwnedSyncRelevantDlcs(IModLogger log)
        {
            try
            {
                var names = new List<string>();
                var seenIds = new HashSet<int>();
                foreach (IDlc dlc in PlatformManager.instance.EnumerateDLCs())
                {
                    // The same DLC is enumerated once per store backend, and the negative
                    // ids are the reserved ones (invalid / base game / the placeholder the
                    // account backend publishes on login) - none are content.
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
        /// Identity for the wire. Only <see cref="IDlc.internalName"/> is machine-neutral:
        /// the store-facing name is whatever the storefront returns, so it varies with the
        /// store client's language and with which backend supplied the entry - two players
        /// owning exactly the same DLC would compare as a mismatch. The id backs it up when
        /// a backend reports the entry without a name.
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
