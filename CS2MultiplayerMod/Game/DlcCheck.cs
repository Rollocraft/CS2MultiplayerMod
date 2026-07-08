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
        /// <summary>DLCs with no effect on the simulation (CS2M's verified list).</summary>
        private static readonly string[] ClientSideDlcs =
            { "AtmosphericPianoRadio", "DeluxeRelaxRadio", "FeelgoodFunkRadio", "JadeRoadRadio" };

        /// <summary>
        /// The owned, sync-relevant DLC names: canonical (prefix stripped), sorted
        /// ordinally so host and client produce byte-identical lists for equal content.
        /// Returns an empty array when enumeration fails - the handshake treats that
        /// as "unknown" and skips the check rather than blocking the player.
        /// </summary>
        public static string[] OwnedSyncRelevantDlcs(IModLogger log)
        {
            try
            {
                var names = new List<string>();
                foreach (IDlc dlc in PlatformManager.instance.EnumerateDLCs())
                {
                    if (!PlatformManager.instance.IsDlcOwned(dlc)) continue;
                    if (IsClientSide(dlc.internalName)) continue;

                    string name = CanonicalName(dlc);
                    if (name.Length > 0 && !names.Contains(name)) names.Add(name);

                    if (names.Count >= ProtocolConstants.MaxDlcEntries) break;
                }

                names.Sort(StringComparer.Ordinal);
                return names.ToArray();
            }
            catch (Exception ex)
            {
                log.Warn("[MP] Could not enumerate DLCs (" + ex.Message + "); " +
                         "the join handshake will skip the DLC compatibility check.");
                return Array.Empty<string>();
            }
        }

        private static bool IsClientSide(string internalName)
        {
            for (int i = 0; i < ClientSideDlcs.Length; i++)
                if (string.Equals(ClientSideDlcs[i], internalName, StringComparison.Ordinal)) return true;
            return false;
        }

        private static string CanonicalName(IDlc dlc)
        {
            string name = dlc.backendName;
            if (string.IsNullOrEmpty(name)) name = dlc.internalName;
            if (string.IsNullOrEmpty(name)) return string.Empty;

            name = name.Replace("Cities: Skylines II - ", "").Trim();
            if (name.Length > ProtocolConstants.MaxDlcNameLength)
                name = name.Substring(0, ProtocolConstants.MaxDlcNameLength);
            return name;
        }
    }
}
