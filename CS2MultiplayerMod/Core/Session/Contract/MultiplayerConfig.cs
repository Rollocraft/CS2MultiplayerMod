using CS2MultiplayerMod.Core.Networking;

namespace CS2MultiplayerMod.Core.Session
{
    public enum ClientResyncPolicy
    {
        Allow = 0,
        RequireApproval = 1,
        HostOnly = 2,
    }

    /// <summary>Immutable parameters used to start a host or join a session.</summary>
    public sealed class MultiplayerConfig
    {
        public readonly string PlayerName;

        /// <summary>Direct mode: the host's address. Relay mode: unused.</summary>
        public readonly string HostAddress;

        /// <summary>Direct mode: the TCP port. Relay mode: unused.</summary>
        public readonly int Port;

        /// <summary>Relay mode opens no port and addresses the host by <see cref="JoinCode"/>.</summary>
        public readonly TransportMode Transport;

        /// <summary>The host's join code when joining over relay; unused otherwise.</summary>
        public readonly string JoinCode;

        /// <summary>When hosting: required password (empty = open). When joining: password to present.</summary>
        public readonly string Password;

        /// <summary>Host only (default true): refuse non-private addresses. Internet play needs a password.</summary>
        public readonly bool LanOnly;

        /// <summary>TLS for all connections. Must match between host and clients.</summary>
        public readonly bool UseEncryption;

        /// <summary>Host only. Hard cap on simultaneous players, including the host.</summary>
        public readonly int MaxPlayers;

        /// <summary>
        /// Host only: joins wait for manual approval. False here so tests admit immediately; the in-game
        /// setting defaults on.
        /// </summary>
        public readonly bool RequireJoinApproval;

        /// <summary>
        /// Host only: platform friends skip manual approval. Direct connections have no account identity.
        /// </summary>
        public readonly bool AutoApprovePlatformFriends;

        /// <summary>Host only. Controls what happens when a client asks for a world resync.</summary>
        public readonly ClientResyncPolicy ClientResyncPolicy;

        /// <summary>Mod build identifier, normally compared strictly during the handshake.</summary>
        public readonly string ModVersion;

        /// <summary>
        /// Host only, opt-in: admit a different mod build. The protocol must still match; behaviour can
        /// still differ.
        /// </summary>
        public readonly bool IgnoreModCompatibilityChecks;

        /// <summary>Host only: replicate simulation decisions; announced to every client on acceptance.</summary>
        public readonly bool SimulationSync;

        /// <summary>Game build identifier, compared strictly during the handshake.</summary>
        public readonly string GameVersion;

        /// <summary>Sorted owned DLC names, compared as a set; empty means no sync-relevant DLC.</summary>
        public readonly string[] DlcList;

        public MultiplayerConfig(string playerName, string hostAddress, int port, string password = "",
                                 bool lanOnly = true, bool useEncryption = true, int maxPlayers = 8,
                                 string modVersion = "", string gameVersion = "", string[] dlcList = null,
                                 bool requireJoinApproval = false,
                                 TransportMode transport = TransportMode.Direct, string joinCode = "",
                                 bool ignoreModCompatibilityChecks = false,
                                 bool simulationSync = true,
                                 bool autoApprovePlatformFriends = false,
                                 ClientResyncPolicy clientResyncPolicy = ClientResyncPolicy.Allow)
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
            IgnoreModCompatibilityChecks = ignoreModCompatibilityChecks;
            GameVersion = gameVersion ?? string.Empty;
            DlcList = dlcList ?? System.Array.Empty<string>();
            RequireJoinApproval = requireJoinApproval;
            SimulationSync = simulationSync;
            AutoApprovePlatformFriends = autoApprovePlatformFriends;
            ClientResyncPolicy = clientResyncPolicy;
        }
    }
}
