using System;
using System.Reflection;

namespace CS2MultiplayerMod
{
    /// <summary>
    /// Human-readable identity of one compiled mod artifact. The release version is supplied
    /// by Paradox Mods while the commit is stamped by the project file when a Git checkout is
    /// available. Keeping both in every diagnostic prevents two locally-built artifacts from
    /// being mistaken for the same build merely because they share a release number.
    /// </summary>
    internal static class BuildIdentity
    {
        private const string CommitKey = "CS2MP.Commit";

        internal static string Commit => _commit ?? (_commit = ReadCommit());
        internal static string Label => Mod.Version + "@" + Commit;

        private static string _commit;

        private static string ReadCommit()
        {
            try
            {
                object[] attributes = typeof(Mod).Assembly.GetCustomAttributes(
                    typeof(AssemblyMetadataAttribute));
                for (int i = 0; i < attributes.Length; i++)
                {
                    AssemblyMetadataAttribute attribute = attributes[i] as AssemblyMetadataAttribute;
                    if (attribute != null && string.Equals(attribute.Key, CommitKey,
                        StringComparison.Ordinal) &&
                        !string.IsNullOrEmpty(attribute.Value))
                        return attribute.Value;
                }
            }
            catch { /* A missing metadata attribute is a valid archive build. */ }

            return "unknown";
        }
    }
}
