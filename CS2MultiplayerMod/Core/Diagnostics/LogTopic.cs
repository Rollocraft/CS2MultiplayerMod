namespace CS2MultiplayerMod.Core.Diagnostics
{
    /// <summary>
    /// What a log line is about, named from the player's side ("my transit lines are missing" is
    /// <see cref="Routes"/>). The grep target, and what developer builds narrow detail to. In Core so
    /// session code shares the game layer's topics.
    /// </summary>
    public enum LogTopic
    {
        /// <summary>Load, settings, registration, compatibility and DLC checks. First, so unattributed lines land here.</summary>
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

        /// <summary>Other mods' state: what was discovered, agreed and sent.</summary>
        ModSync,
    }
}
