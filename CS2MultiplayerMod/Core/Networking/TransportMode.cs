namespace CS2MultiplayerMod.Core.Networking
{
    /// <summary>How the peers of a session reach each other.</summary>
    public enum TransportMode
    {
        /// <summary>TCP to the host's address and port; LAN or a forwarded port only.</summary>
        Direct = 0,

        /// <summary>Steam's relay network by join code; no public port.</summary>
        SteamRelay = 1,
    }
}
