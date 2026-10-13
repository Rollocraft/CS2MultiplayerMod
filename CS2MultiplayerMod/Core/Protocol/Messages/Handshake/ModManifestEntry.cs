namespace CS2MultiplayerMod.Core.Protocol.Messages
{
    /// <summary>
    /// One live mod in a handshake, as "platformId|version|name". The id is empty where only the loaded
    /// assembly was readable; such an entry matches by name.
    /// </summary>
    public struct ModManifestEntry
    {
        public string Id;
        public string Version;
        public string Name;

        public static string Format(string id, string version, string name) =>
            Clean(id) + "|" + Clean(version) + "|" + (name ?? "").Trim();

        public static ModManifestEntry Parse(string line)
        {
            string[] parts = (line ?? "").Split(new[] { '|' }, 3);
            if (parts.Length < 3)
                return new ModManifestEntry { Id = "", Version = "", Name = (line ?? "").Trim() };
            return new ModManifestEntry { Id = parts[0], Version = parts[1], Name = parts[2] };
        }

        private static string Clean(string value) => (value ?? "").Replace('|', '/').Trim();

        public override string ToString() => Version.Length == 0 ? Name : Name + " " + Version;
    }
}
