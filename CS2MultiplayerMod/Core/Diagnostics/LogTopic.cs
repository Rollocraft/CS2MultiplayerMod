namespace CS2MultiplayerMod.Core.Diagnostics
{
    /// <summary>
    /// What a log line is about.
    ///
    /// Every line the mod writes names one of these, and each one can be switched on by itself.
    /// That is the whole point: a player chasing "roads do not appear on my partner's screen"
    /// turns on <see cref="Nets"/> and gets a log about roads, instead of a general "debug"
    /// switch that buries the one interesting line under twenty thousand others. A log nobody
    /// can read is a log nobody reads.
    ///
    /// The topics are named after the thing that went wrong from the player's side, not after
    /// the class that noticed it - "my transit lines are missing" is <see cref="Routes"/>, and
    /// the reporter may be a channel, a system or the pipeline.
    ///
    /// This lives in the portable core so the networking and session code can name the same
    /// topics as the game layer without referencing a game assembly.
    /// </summary>
    public enum LogTopic
    {
        /// <summary>
        /// The mod itself: load, settings, system registration, compatibility and DLC checks.
        /// Deliberately first, so an unattributed line lands somewhere honest.
        /// </summary>
        Startup = 0,

        /// <summary>Connecting, disconnecting, the handshake, peers joining and leaving, kicks and bans.</summary>
        Session,

        /// <summary>The wire underneath a session: sockets, the Steam relay, port forwarding, framing, rates.</summary>
        Transport,

        /// <summary>Sending, receiving, staging and loading the world a joining player downloads.</summary>
        WorldTransfer,

        /// <summary>Divergence: what was detected, what the arbiter decided, and what the repair did.</summary>
        Resync,

        /// <summary>The command pipeline: inbox, observers, authority holds, definition gates, realization.</summary>
        Pipeline,

        /// <summary>Roads, tracks, pipes and wires - placement, upgrades, replacement, topology.</summary>
        Nets,

        /// <summary>Placed objects: buildings, props and trees - placement, move, upgrade, delete.</summary>
        Buildings,

        /// <summary>The map itself: zoning, areas and districts, terrain, tile purchases.</summary>
        Land,

        /// <summary>City-wide state: names, policies, money, milestones, the development tree, statistics.</summary>
        City,

        /// <summary>Transit: lines, stops, vehicles and fares.</summary>
        Routes,

        /// <summary>Households, residents and their homes.</summary>
        Residential,

        /// <summary>Shops: tenancy, figures and stock.</summary>
        Commercial,

        /// <summary>Factories and extractors: tenancy, figures and stock.</summary>
        Industrial,

        /// <summary>Offices: tenancy, figures and stock.</summary>
        Office,

        /// <summary>The other players: their cursors, markers, map pings and chat.</summary>
        Players,

        /// <summary>The mod's own screens: the main-menu button, the join dialog, the options page.</summary>
        Ui,

        /// <summary>Frame times and the mod's own main-thread cost, including the per-zone split.</summary>
        Performance,
    }
}
