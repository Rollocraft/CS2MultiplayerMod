using System;
using CS2MultiplayerMod.Core.Diagnostics;
using Steamworks;

namespace CS2MultiplayerMod.Core.Networking.Steam
{
    /// <summary>
    /// The relay backend. The game already runs the Steam API and pumps its callbacks
    /// every frame, so this only has to report whether that is true right now and hand
    /// out transports when it is.
    ///
    /// Instantiated by name from the mod assembly, which never links this one - see
    /// SteamRelayBootstrap for why the Steam code has to sit on its own.
    /// </summary>
    public sealed class SteamRelayProvider : IRelayProvider
    {
        /// <summary>
        /// Virtual port inside the relay, not a network port: nothing is opened on the
        /// machine and nothing needs forwarding. Both sides must simply agree, so it is
        /// a constant rather than a setting.
        /// </summary>
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
                    // A non-Steam copy of the game has no native Steam library at all, which
                    // surfaces here as a load failure rather than a false return.
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
        /// The Steam persona name. Read without gating on <see cref="UnavailableReason"/>:
        /// the name is known whenever the API answers at all, and it is only ever used as
        /// a first-run default for the player-name field.
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

        public ITransport CreateHost(IModLogger log)
        {
            return SteamRelayTransport.StartHost(log, VirtualPort);
        }

        public ITransport CreateClient(IModLogger log, string joinCode)
        {
            return SteamRelayTransport.Connect(log, joinCode, VirtualPort);
        }

        internal static ulong LocalSteamId()
        {
            try { return SteamUser.GetSteamID().m_SteamID; }
            catch (Exception) { return 0; }
        }
    }
}
