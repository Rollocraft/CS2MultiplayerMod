using System;
using CS2MultiplayerMod.Core.Diagnostics;
using Steamworks;

namespace CS2MultiplayerMod.Core.Networking.Steam
{
    /// <summary>
    /// The relay backend. The game already runs the Steam API; this reports availability and hands out
    /// transports. Instantiated by name, never linked (see SteamRelayBootstrap).
    /// </summary>
    public sealed class SteamRelayProvider : IRelayProvider
    {
        /// <summary>A virtual port inside the relay, not a network port; both sides must agree.</summary>
        public const int VirtualPort = 25001;

        public string UnavailableReason
        {
            get
            {
                try
                {
                    if (!SteamAPI.IsSteamRunning())
                        return "Steam is not running.";
                    if (LocalSteamId() == 0)
                        return "Steam is not signed in.";
                    return null;
                }
                catch (Exception ex)
                {
                    // A non-Steam copy has no native Steam library: a load failure.
                    return "Steam is not available (" + ex.Message + ").";
                }
            }
        }

        public string LocalJoinCode
        {
            get
            {
                ulong id = LocalSteamId();
                return id == 0 ? "" : id.ToString();
            }
        }

        /// <summary>
        /// The Steam persona name, not gated on <see cref="UnavailableReason"/>; only a first-run default.
        /// </summary>
        public string LocalPlayerName
        {
            get
            {
                try
                {
                    string name = SteamFriends.GetPersonaName();
                    return name ?? "";
                }
                catch (Exception)
                {
                    return "";
                }
            }
        }

        private SteamServerDirectory _directory;

        public ITransport CreateHost(IModLogger log, bool relayOnly) =>
            SteamRelayTransport.StartHost(log, VirtualPort, relayOnly);

        public ITransport CreateClient(IModLogger log, string joinCode, bool relayOnly) =>
            SteamRelayTransport.Connect(log, joinCode, VirtualPort, relayOnly);

        public IServerDirectory OpenDirectory(IModLogger log) =>
            _directory ?? (_directory = new SteamServerDirectory(log));

        internal static ulong LocalSteamId()
        {
            try { return SteamUser.GetSteamID().m_SteamID; }
            catch (Exception) { return 0; }
        }
    }
}
