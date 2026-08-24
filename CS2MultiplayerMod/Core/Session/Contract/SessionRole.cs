namespace CS2MultiplayerMod.Core.Session
{
    public enum SessionRole
    {
        /// <summary>Not in a multiplayer session.</summary>
        None,

        /// <summary>Authoritative host: owns player-id assignment and relays commands.</summary>
        Host,

        /// <summary>Joins a host and exchanges commands with it.</summary>
        Client,
    }
}
