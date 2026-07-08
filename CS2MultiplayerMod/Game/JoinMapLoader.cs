using System;
using System.Collections.Generic;
using System.IO;
using Colossal.IO.AssetDatabase;
using Colossal.Serialization.Entities;
using Game;
using Game.Assets;
using Game.SceneFlow;
using CS2MultiplayerMod.Core.Diagnostics;

namespace CS2MultiplayerMod.Game
{
    /// <summary>
    /// Handles the joining player's copy of the host world. The intent is that a client
    /// plays *in the host's session* rather than accumulating savegames: the received
    /// <c>.cok</c> is written under a clearly-temporary name, loaded straight into the
    /// game, and deleted again when the player leaves (and overwritten on the next join),
    /// so no permanent copy is kept.
    ///
    /// Safety invariants (security findings 4/34): the staging path is a compile-time
    /// constant - nothing from the network ever influences a file name or path, so a
    /// streamed blob can only ever overwrite <c>_MP_JoinSession.cok</c> and never a real
    /// save; the blob itself was already verified against the announced transfer size
    /// by the session layer before it gets here; and it is only accepted at all from an
    /// authenticated host on the registered "map" channel.
    /// </summary>
    internal static class JoinMapLoader
    {
        public const string TransientName = "_MP_JoinSession";
        private const string SaveExtension = ".cok";

        /// <summary>
        /// Write the received world to the fixed transient path and kick off loading it.
        /// Returns true when a load was actually started; false means "staged but not
        /// loading" (or not even staged) - the caller surfaces a recoverable state.
        /// </summary>
        public static bool StageAndLoad(byte[] saveBytes, IModLogger log)
        {
            if (saveBytes == null || saveBytes.Length == 0)
            {
                log.Warn("[MP] Received an empty host world; ignoring.");
                return false;
            }

            string dir = SavesDirectory();
            if (dir == null) { log.Warn("[MP] Saves folder not found; cannot load host map."); return false; }

            try
            {
                // Drop any previous transient world (file + index entry) first, so a
                // mid-session /sync re-stream never leaves a stale registration shadowing
                // the fresh one when we look it up below.
                DeleteTransient(log);

                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, TransientName + SaveExtension);
                File.WriteAllBytes(path, saveBytes);
                log.Info("[MP] Host world staged at '" + path + "' (" + (saveBytes.Length / 1024) + " KB).");
                log.Info("[MP] Host world received (" + (saveBytes.Length / 1024) + " KB); loading into game...");
                return TryLoad(log);
            }
            catch (Exception ex)
            {
                log.Error("[MP] Failed to stage host map: " + ex.Message);
                return false;
            }
        }

        private static bool TryLoad(IModLogger log)
        {
            try
            {
                // The game only ever sees savegames it has indexed in AssetDatabase.user.
                // Its file data source indexes a freshly written .cok through a watcher
                // that polls on window *focus* (FileSystemDataSource, PollingMode.OnFocus)
                // — and a joining player never alt-tabs, so the world we just wrote stays
                // invisible and the join silently stalls at "100%". So we register the
                // file ourselves, exactly as the engine's watcher would on focus, but
                // synchronously, right now.
                RegisterStagedSave(log);

                SaveGameMetadata metadata = FindStagedSave();
                if (metadata != null)
                {
                    GameManager.instance.Load(GameMode.Game, Purpose.LoadGame, metadata);
                    log.Info("[MP] Loading host world - joining the session.");
                    return true;
                }

                log.Warn("[MP] Host world staged but could not be registered with the save index. " +
                         "Run /sync to retry, or load '" + TransientName + "' from Load Game.");
                return false;
            }
            catch (Exception ex)
            {
                log.Error("[MP] Auto-load failed: " + ex.Message + " - the world is staged as '" + TransientName + "' to load manually.");
                return false;
            }
        }

        /// <summary>
        /// Make <see cref="AssetDatabase.user"/> aware of the transient .cok we just wrote
        /// by adding it to the user data source as a package asset - the exact call the
        /// engine's own file watcher makes when it notices a new save
        /// (<c>FileSystemDataSource.OnFileSystemEvent -> AddEntry</c>). Adding a
        /// <see cref="PackageAsset"/> opens the package and registers the
        /// <see cref="SaveGameMetadata"/> it contains, so the save becomes loadable
        /// immediately instead of only after the next window-focus poll.
        ///
        /// The path is the same compile-time constant the bytes were written to - nothing
        /// from the network influences it, so this can only ever register
        /// <c>_MP_JoinSession.cok</c>.
        /// </summary>
        private static void RegisterStagedSave(IModLogger log)
        {
            string dir = SavesDirectory();
            if (dir == null) return;

            try
            {
                // Mirror the watcher: build the entry from the real, forward-slashed file
                // path with no escaping (the path is fixed and already clean).
                string fullPath = Path.Combine(dir, TransientName + SaveExtension).Replace('\\', '/');
                string fileDir = Path.GetDirectoryName(fullPath)?.Replace('\\', '/');
                string fileName = Path.GetFileName(fullPath);
                AssetDataPath entryPath = AssetDataPath.Create(fileDir, fileName, hasExtension: true, EscapeStrategy.None);
                AssetDatabase.user.dataSource.AddEntry(entryPath, typeof(PackageAsset));
            }
            catch (Exception ex)
            {
                // Non-fatal: if the engine's watcher later notices the file (e.g. on an
                // alt-tab) the lookup can still succeed; otherwise the caller recovers.
                log.Warn("[MP] Could not register the host world with the save index: " + ex.Message);
            }
        }

        /// <summary>
        /// Find the <see cref="SaveGameMetadata"/> for the staged world. The streamed
        /// package keeps the *host's* internal asset names, so the only thing that
        /// identifies our copy is its file path - match on the asset's URI containing the
        /// transient name rather than on its display name.
        /// </summary>
        private static SaveGameMetadata FindStagedSave()
        {
            var filter = SearchFilter<SaveGameMetadata>.ByCondition(
                m => m != null && m.id.uri != null &&
                     m.id.uri.IndexOf(TransientName, StringComparison.OrdinalIgnoreCase) >= 0);
            foreach (SaveGameMetadata md in AssetDatabase.user.GetAssets(filter))
                if (md != null) return md;
            return null;
        }

        /// <summary>Remove the transient world so the joining player keeps no local copy.</summary>
        public static void DeleteTransient(IModLogger log)
        {
            // Remove the index registration(s) first: deleting the asset drops the .cok
            // (and its .cid guid sidecar) from disk together with the entry, so the next
            // join re-registers from a clean slate.
            bool removedViaIndex = false;
            try
            {
                var doomed = new List<SaveGameMetadata>();
                var filter = SearchFilter<SaveGameMetadata>.ByCondition(
                    m => m != null && m.id.uri != null &&
                         m.id.uri.IndexOf(TransientName, StringComparison.OrdinalIgnoreCase) >= 0);
                foreach (SaveGameMetadata md in AssetDatabase.user.GetAssets(filter))
                    if (md != null) doomed.Add(md);

                foreach (SaveGameMetadata md in doomed)
                {
                    try { AssetDatabase.user.DeleteAsset(md); removedViaIndex = true; }
                    catch (Exception ex) { log.Warn("[MP] Could not remove transient save entry: " + ex.Message); }
                }
            }
            catch (Exception ex)
            {
                log.Warn("[MP] Transient save index cleanup failed: " + ex.Message);
            }

            // Belt and braces: if the world was staged but never indexed, remove the raw
            // file (and any guid sidecar) directly so no local copy of the host city lingers.
            try
            {
                string dir = SavesDirectory();
                if (dir == null) return;
                string path = Path.Combine(dir, TransientName + SaveExtension);
                bool removedFile = false;
                if (File.Exists(path)) { File.Delete(path); removedFile = true; }
                string cid = path + ".cid";
                if (File.Exists(cid)) File.Delete(cid);

                if (removedViaIndex || removedFile)
                    log.Info("[MP] Removed transient host world (no local copy kept).");
            }
            catch (Exception ex)
            {
                log.Warn("[MP] Could not delete transient map: " + ex.Message);
            }
        }

        public static string SavesDirectory()
        {
            // The game's own user-data path — correct on every installation. The
            // CSII_USERDATAPATH environment variable is only set on developer
            // machines by the modding toolchain, so it is merely a fallback;
            // relying on it broke map loading for every normal player.
            string userData = null;
            try { userData = Colossal.PSI.Environment.EnvPath.kUserDataPath; }
            catch (Exception) { }

            if (string.IsNullOrEmpty(userData))
                userData = Environment.GetEnvironmentVariable("CSII_USERDATAPATH", EnvironmentVariableTarget.User);

            if (string.IsNullOrEmpty(userData)) return null;
            return Path.Combine(userData, "Saves");
        }
    }
}
