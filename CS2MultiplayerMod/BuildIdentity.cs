using System;
using System.Reflection;

namespace CS2MultiplayerMod
{
    /// <summary>Release version and source commit of this build.</summary>
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
                var attributes = typeof(Mod).Assembly.GetCustomAttributes(
                    typeof(AssemblyMetadataAttribute));
                foreach (object item in attributes)
                {
                    AssemblyMetadataAttribute attribute = item as AssemblyMetadataAttribute;
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
