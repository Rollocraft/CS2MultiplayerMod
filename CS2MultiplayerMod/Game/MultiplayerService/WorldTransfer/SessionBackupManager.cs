using System;
using System.IO;
using System.Linq;

namespace CS2MultiplayerMod.Game
{
    /// <summary>
    /// Creates and manages rolling automatic session save backups in a dedicated MultiplayerBackups directory.
    /// </summary>
    public static class SessionBackupManager
    {
        private const int MaxBackupsPerCity = 5;

        public static string BackupDirectory
        {
            get
            {
                string localLow = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "Low";
                string dir = Path.Combine(localLow, "Colossal Order", "Cities Skylines II", "MultiplayerBackups");
                if (!Directory.Exists(dir))
                {
                    try { Directory.CreateDirectory(dir); }
                    catch (Exception ex) { Mod.log.Warn("[MP] Failed to create backup directory: " + ex.Message); }
                }
                return dir;
            }
        }

        public static void CreateBackup(string cityName, byte[] saveBytes)
        {
            if (saveBytes == null || saveBytes.Length == 0) return;

            try
            {
                string dir = BackupDirectory;
                if (!Directory.Exists(dir)) return;

                string sanitizedName = SanitizeFileName(string.IsNullOrWhiteSpace(cityName) ? "MultiplayerCity" : cityName);
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string backupPath = Path.Combine(dir, sanitizedName + "_Backup_" + timestamp + ".mpbak");
                string tmpPath = backupPath + ".tmp";

                // Clean up any legacy .cok files in this directory to prevent CS2 package scanner warnings
                CleanLegacyCokBackups(dir);

                // Write to staging file first to prevent partial/corrupted saves on crash
                File.WriteAllBytes(tmpPath, saveBytes);
                if (File.Exists(backupPath)) File.Delete(backupPath);
                File.Move(tmpPath, backupPath);

                Mod.log.Info("[MP] Auto-backup created successfully: " + backupPath + " (" + saveBytes.Length + " bytes)");

                PruneOldBackups(dir, sanitizedName);
            }
            catch (Exception ex)
            {
                Mod.log.Warn("[MP] Failed to write session auto-backup: " + ex.Message);
            }
        }

        private static void CleanLegacyCokBackups(string dir)
        {
            try
            {
                foreach (string file in Directory.GetFiles(dir, "*.cok"))
                {
                    try { File.Delete(file); } catch { }
                }
            }
            catch { }
        }

        private static void PruneOldBackups(string dir, string cityPrefix)
        {
            try
            {
                var files = Directory.GetFiles(dir, cityPrefix + "_Backup_*.mpbak")
                    .Select(f => new FileInfo(f))
                    .OrderByDescending(f => f.CreationTimeUtc)
                    .ToList();

                if (files.Count > MaxBackupsPerCity)
                {
                    for (int i = MaxBackupsPerCity; i < files.Count; i++)
                    {
                        try { files[i].Delete(); }
                        catch { /* ignore */ }
                    }
                }
            }
            catch { /* ignore pruning errors */ }
        }

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "MultiplayerCity";

            foreach (char c in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '_');
            }
            name = name.Trim('_', ' ');
            return string.IsNullOrEmpty(name) ? "MultiplayerCity" : name;
        }
    }
}
