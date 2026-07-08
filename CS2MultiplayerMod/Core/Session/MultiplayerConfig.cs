namespace CS2MultiplayerMod.Core.Session
{
    /// <summary>Immutable parameters used to start a host or join a session.</summary>
    public sealed class MultiplayerConfig
    {
        public readonly string PlayerName;
        public readonly string HostAddress;
        public readonly int Port;

        /// <summary>When hosting: required password (empty = open). When joining: password to present.</summary>
        public readonly string Password;

        /// <summary>
        /// Host only: when true (default), non-private address connections refused.
        /// Session LAN-only. Internet play requires password.
        /// </summary>
        public readonly bool LanOnly;

        /// <summary>TLS for all connections. Must match between host and clients.</summary>
        public readonly bool UseEncryption;

        /// <summary>Host only. Hard cap on simultaneous players, including the host.</summary>
        public readonly int MaxPlayers;

        /// <summary>Mod build identifier, compared strictly during the handshake.</summary>
        public readonly string ModVersion;

        /// <summary>Game build identifier, compared strictly during the handshake.</summary>
        public readonly string GameVersion;

        /// <summary>
        /// Canonical (sorted) DLC names this machine owns. Compared as set during
        /// handshake - differing DLCs mean differing prefab catalogues, desync. Empty
        /// means "unknown/don't check", skipping validation for unknown DLC enumerations.
        /// </summary>
        public readonly string[] DlcList;

        public MultiplayerConfig(string playerName, string hostAddress, int port, string password = "",
                                 bool lanOnly = true, bool useEncryption = true, int maxPlayers = 8,
                                 string modVersion = "", string gameVersion = "", string[] dlcList = null)
        {
            PlayerName = string.IsNullOrEmpty(playerName) ? "Player" : playerName;
            HostAddress = string.IsNullOrEmpty(hostAddress) ? "127.0.0.1" : hostAddress;
            Port = port;
            Password = password ?? string.Empty;
            LanOnly = lanOnly;
            UseEncryption = useEncryption;
            MaxPlayers = maxPlayers < 2 ? 2 : maxPlayers;
            ModVersion = modVersion ?? string.Empty;
            GameVersion = gameVersion ?? string.Empty;
            DlcList = dlcList ?? System.Array.Empty<string>();
        }
    }
}
