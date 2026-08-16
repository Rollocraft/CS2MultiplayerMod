using CS2MultiplayerMod.Core.Networking;

namespace CS2MultiplayerMod.Core.Session
{
    /// <summary>Immutable parameters used to start a host or join a session.</summary>
    public sealed class MultiplayerConfig
    {
        public readonly string PlayerName;

        /// <summary>Direct mode: the host's address. Relay mode: unused.</summary>
        public readonly string HostAddress;

        /// <summary>Direct mode: the TCP port. Relay mode: unused.</summary>
        public readonly int Port;

        /// <summary>
        /// How to reach the peers. Relay mode opens no port and needs no forwarding;
        /// it addresses the host by <see cref="JoinCode"/> instead.
        /// </summary>
        public readonly TransportMode Transport;

        /// <summary>
        /// Relay mode when joining: the host's join code. Ignored in direct mode and
        /// unused when hosting (a host's own code comes from the relay provider).
        /// </summary>
        public readonly string JoinCode;

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

        /// <summary>
        /// Host only. When true, a join that passes every automatic check still waits for
        /// the host to approve it by hand before the player is admitted. Defaults to false
        /// so programmatic hosts (and the test harness) admit valid joins immediately; the
        /// in-game host setting turns it on by default.
        /// </summary>
        public readonly bool RequireJoinApproval;

        /// <summary>Mod build identifier, compared strictly during the handshake.</summary>
        public readonly string ModVersion;

        /// <summary>Game build identifier, compared strictly during the handshake.</summary>
        public readonly string GameVersion;

        /// <summary>
        /// Canonical (sorted) DLC names this machine owns. Compared as a complete set
        /// during the handshake because differing DLCs mean differing prefab catalogues.
        /// An empty array is a real set: this machine owns no sync-relevant DLC.
        /// </summary>
        public readonly string[] DlcList;

        public MultiplayerConfig(string playerName, string hostAddress, int port, string password = "",
                                 bool lanOnly = true, bool useEncryption = true, int maxPlayers = 8,
                                 string modVersion = "", string gameVersion = "", string[] dlcList = null,
                                 bool requireJoinApproval = false,
                                 TransportMode transport = TransportMode.Direct, string joinCode = "")
        {
            Transport = transport;
            JoinCode = joinCode ?? string.Empty;
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
            RequireJoinApproval = requireJoinApproval;
        }
    }
}
