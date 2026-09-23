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
    /// The joining client's copy of the host world: written under a fixed transient name, loaded, and
    /// deleted on leave, so no permanent copy is kept. The path is a compile-time constant (nothing from
    /// the network names a file), the size was verified by the session, and only an authenticated host's
    /// "map" channel reaches here.
    /// </summary>
    internal static class JoinMapLoader
    {
        public const string TransientName = "_MP_JoinSession";
        private const string SaveExtension = ".cok";

        /// <summary>True when a load started; false means staged but not loading, which the caller recovers.</summary>
        public static bool StageAndLoad(byte[] saveBytes, IModLogger log)
        {
            if (saveBytes == null || saveBytes.Length == 0)
            {
                log.Warn(LogTopic.WorldTransfer, "Received an empty host world; ignoring.");
                return false;
            }

            string dir = SavesDirectory();
            if (dir == null) { log.Warn(LogTopic.WorldTransfer, "Saves folder not found; cannot load host map."); return false; }

            try
            {
                // Drop the previous transient world first, so a stale registration cannot shadow this one.
                DeleteTransient(log);

                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, TransientName + SaveExtension);
                File.WriteAllBytes(path, saveBytes);
                log.Detail(LogTopic.WorldTransfer, "Host world staged at '" +
                    Diagnostics.LogPaths.Redact(path) + "' (" + (saveBytes.Length / 1024) +
                    " KB).");
                log.Event(LogTopic.WorldTransfer, "Host world received (" +
                    (saveBytes.Length / 1024) + " KB); loading into game...");

                // Claim the load first, including the manual fallback below, so the session watcher does not read
                // it as leaving.
                if (Mod.Service != null) Mod.Service.ExpectOwnWorldLoad();
                return TryLoad(log);
            }
            catch (Exception ex)
            {
                log.Error(LogTopic.WorldTransfer, "Failed to stage host map: " + ex.Message);
                return false;
            }
        }

        private static bool TryLoad(IModLogger log)
        {
            try
            {
                // The game indexes a new .cok only on window focus, which a joining player never changes, so the
                // join would stall at 100%. Register it now, as the watcher would.
                RegisterStagedSave(log);

                SaveGameMetadata metadata = FindStagedSave();
                if (metadata != null)
                {
                    GameManager.instance.Load(GameMode.Game, Purpose.LoadGame, metadata);
                    log.Event(LogTopic.WorldTransfer, "Loading host world - joining the session.");
                    return true;
                }

                log.Warn(LogTopic.WorldTransfer,
                    "Host world staged but could not be registered with the save index. " +
                    "Run /sync to retry, or load '" + TransientName + "' from Load Game.");
                return false;
            }
            catch (Exception ex)
            {
                log.Error(LogTopic.WorldTransfer, "Auto-load failed: " + ex.Message +
                    " - the world is staged as '" + TransientName + "' to load manually.");
                return false;
            }
        }

        /// <summary>
        /// Adds the transient .cok to the user data source as a package asset, as the file watcher does on a
        /// new save, so it is loadable immediately. Only ever the fixed <c>_MP_JoinSession.cok</c>.
        /// </summary>
        private static void RegisterStagedSave(IModLogger log)
        {
            string dir = SavesDirectory();
            if (dir == null) return;

            try
            {
                // As the watcher does: the real forward-slashed path, unescaped.
                string fullPath = Path.Combine(dir, TransientName + SaveExtension).Replace('\\', '/');
                string fileDir = Path.GetDirectoryName(fullPath)?.Replace('\\', '/');
                string fileName = Path.GetFileName(fullPath);
                AssetDataPath entryPath = AssetDataPath.Create(fileDir, fileName, hasExtension: true, EscapeStrategy.None);
                AssetDatabase.user.dataSource.AddEntry(entryPath, typeof(PackageAsset));
            }
            catch (Exception ex)
            {
                // Non-fatal: the watcher may still find it on focus; otherwise the caller recovers.
                log.Warn(LogTopic.WorldTransfer,
                    "Could not register the host world with the save index: " + ex.Message);
            }
        }

        /// <summary>By URI containing the transient name: the package keeps the host's internal asset names.</summary>
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
            // Deleting the indexed asset removes the .cok and .cid sidecar with the entry.
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
                    catch (Exception ex) { log.Warn(LogTopic.WorldTransfer, "Could not remove transient save entry: " + ex.Message); }
                }
            }
            catch (Exception ex)
            {
                log.Warn(LogTopic.WorldTransfer, "Transient save index cleanup failed: " +
                    ex.Message);
            }

            // Staged but never indexed: remove the raw files directly.
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
                    log.Detail(LogTopic.WorldTransfer,
                        "Removed transient host world (no local copy kept).");
            }
            catch (Exception ex)
            {
                log.Warn(LogTopic.WorldTransfer, "Could not delete transient map: " + ex.Message);
            }
        }

        public static string SavesDirectory()
        {
            // The game's own user-data path; CSII_USERDATAPATH is set only on developer machines.
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
