using System;
using System.Collections.Generic;
using Game.SceneFlow;

namespace CS2MultiplayerMod.Localization
{
    /// <summary>
    /// Runtime translation for strings computed in code; option labels are resolved by the game from the
    /// locale sources. Follows the game language live. Lookup: game dictionary -> English -> the key.
    /// </summary>
    public static class L10n
    {
        /// <summary>Runtime locale keys; option labels use game-generated ids instead.</summary>
        public static class Key
        {
            // -- In-game multiplayer hub (right-menu button + panel) --
            public const string UiResyncAllow = "CS2MP.UI.ResyncAllow";
            public const string UiResyncApproval = "CS2MP.UI.ResyncApproval";
            public const string UiResyncHostOnly = "CS2MP.UI.ResyncHostOnly";

            // -- Untested game-version warning banner --
            // {0} = running build, {1} = comma-separated tested builds.
            public const string UiVersionWarning = "CS2MP.UI.VersionWarning";

            // Options version row for local builds. {0} = mod version, {1} = build stamp, {2} = protocol.
            public const string VersionLineDev = "CS2MP.UI.VersionLineDev";

            // -- Other-mods block (host and join are both refused while any is live) --
            // {0} = comma-separated names of the other live mods.
            public const string UiModsBlocked = "CS2MP.UI.ModsBlocked";
            // A block from the loaded-assembly fallback clears only on restart.
            public const string UiModsBlockedRestart = "CS2MP.UI.ModsBlockedRestart";
            public const string UiModsIgnored = "CS2MP.UI.ModsIgnored";

            // -- Session status (join dialog indicator, hub panel, loading overlay) --
            public const string StatusOffline = "CS2MP.Status.Offline";
            public const string StateConnecting = "CS2MP.Status.Connecting";
            public const string StateConnected = "CS2MP.Status.Connected";
            public const string WorldMapProgress = "CS2MP.Status.WorldMapProgress";
            public const string PhaseWaitingForMap = "CS2MP.Status.WaitingForMap";
            public const string PhaseLoadingMap = "CS2MP.Status.LoadingMap";
            public const string PhaseSynchronizing = "CS2MP.Status.Synchronizing";
            public const string PhaseFinishingSetup = "CS2MP.Status.FinishingSetup";
            public const string TitlePlayerJoining = "CS2MP.Status.PlayerJoining";
            public const string TitlePlayersJoining = "CS2MP.Status.PlayersJoining";
            public const string TitleRefreshingWorld = "CS2MP.Status.RefreshingWorld";
            public const string TitleModDisabled = "CS2MP.Status.ModDisabled";
            public const string TitleConnectionFailed = "CS2MP.Status.ConnectionFailed";
            public const string TitleHosting = "CS2MP.Status.Hosting";
            public const string TitleAwaitingApproval = "CS2MP.Status.AwaitingApproval";
            public const string DetailAwaitingApproval = "CS2MP.Status.DetailAwaitingApproval";
            public const string DetailEnableMod = "CS2MP.Status.DetailEnableMod";
            public const string DetailPlayersOne = "CS2MP.Status.DetailPlayersOne";
            public const string DetailPlayersMany = "CS2MP.Status.DetailPlayersMany";
            public const string DetailPasswordProtected = "CS2MP.Status.DetailPasswordProtected";
            public const string DetailOpenAccess = "CS2MP.Status.DetailOpenAccess";
            public const string DetailPublic = "CS2MP.Status.DetailPublic";
            public const string DetailContactingHost = "CS2MP.Status.DetailContactingHost";
            public const string DetailHostPreparing = "CS2MP.Status.DetailHostPreparing";
            public const string DetailWorldReceived = "CS2MP.Status.DetailWorldReceived";
            public const string DetailWorldLoaded = "CS2MP.Status.DetailWorldLoaded";
            public const string DetailPausingWorld = "CS2MP.Status.DetailPausingWorld";
            public const string DetailSavingWorld = "CS2MP.Status.DetailSavingWorld";
            public const string DetailSendingWorld = "CS2MP.Status.DetailSendingWorld";
            public const string DetailWaitingForPlayer = "CS2MP.Status.DetailWaitingForPlayer";
            public const string DetailWaitingForPlayers = "CS2MP.Status.DetailWaitingForPlayers";

            // -- Friendly, actionable connection failures --
            public const string ErrorPassword = "CS2MP.Error.Password";
            public const string ErrorPasswordHelp = "CS2MP.Error.Password.Help";
            public const string ErrorModVersion = "CS2MP.Error.ModVersion";
            public const string ErrorModVersionHelp = "CS2MP.Error.ModVersion.Help";
            public const string ErrorGameVersion = "CS2MP.Error.GameVersion";
            public const string ErrorGameVersionHelp = "CS2MP.Error.GameVersion.Help";
            public const string ErrorDlc = "CS2MP.Error.Dlc";
            public const string ErrorDlcHelp = "CS2MP.Error.Dlc.Help";
            public const string ErrorMods = "CS2MP.Error.Mods";
            public const string ErrorModsHelp = "CS2MP.Error.Mods.Help";
            public const string ErrorModSet = "CS2MP.Error.ModSet";
            public const string ErrorModSetHelp = "CS2MP.Error.ModSet.Help";
            public const string ErrorPublicPassword = "CS2MP.Error.PublicPassword";
            public const string ErrorPublicPasswordHelp = "CS2MP.Error.PublicPassword.Help";
            public const string ErrorFull = "CS2MP.Error.Full";
            public const string ErrorFullHelp = "CS2MP.Error.Full.Help";
            public const string ErrorAddress = "CS2MP.Error.Address";
            public const string ErrorAddressHelp = "CS2MP.Error.Address.Help";
            public const string ErrorRefused = "CS2MP.Error.Refused";
            public const string ErrorRefusedHelp = "CS2MP.Error.Refused.Help";
            public const string ErrorTimeout = "CS2MP.Error.Timeout";
            public const string ErrorTimeoutHelp = "CS2MP.Error.Timeout.Help";
            public const string ErrorNetwork = "CS2MP.Error.Network";
            public const string ErrorNetworkHelp = "CS2MP.Error.Network.Help";
            public const string ErrorPortInUse = "CS2MP.Error.PortInUse";
            public const string ErrorPortInUseHelp = "CS2MP.Error.PortInUse.Help";
            public const string ErrorRemoved = "CS2MP.Error.Removed";
            public const string ErrorRemovedHelp = "CS2MP.Error.Removed.Help";
            public const string ErrorDeclined = "CS2MP.Error.Declined";
            public const string ErrorDeclinedHelp = "CS2MP.Error.Declined.Help";
            public const string ErrorGeneric = "CS2MP.Error.Generic";
            public const string ErrorGenericHelp = "CS2MP.Error.Generic.Help";
            public const string DiagnosticsNone = "CS2MP.Diagnostics.None";
            // {0} = file name in the game's Logs folder.
            public const string DiagnosticsSaved = "CS2MP.Diagnostics.Saved";
            public const string DiagnosticsFailed = "CS2MP.Diagnostics.Failed";

            // -- Connection mode --
            public const string ConnectionRelay = "CS2MP.Connection.Relay";
            public const string ConnectionDirect = "CS2MP.Connection.Direct";
            public const string JoinCode = "CS2MP.Connection.JoinCode";
            public const string JoinCodeUnavailable = "CS2MP.Connection.JoinCodeUnavailable";
            public const string JoinCodeHint = "CS2MP.Connection.JoinCodeHint";
            public const string JoinCodeSelectHint = "CS2MP.Connection.JoinCodeSelectHint";
            public const string JoinCodeEntry = "CS2MP.Connection.JoinCodeEntry";
            public const string JoinCodeEntryHint = "CS2MP.Connection.JoinCodeEntryHint";
            public const string RelayHint = "CS2MP.Connection.RelayHint";
            public const string DirectHint = "CS2MP.Connection.DirectHint";
            public const string RelayUnavailableHint = "CS2MP.Connection.RelayUnavailableHint";

            // -- Host tab state line --
            public const string HostLoadCityFirst = "CS2MP.Host.LoadCityFirst";
            public const string HostReady = "CS2MP.Host.Ready";
            public const string HostSessionActive = "CS2MP.Host.SessionActive";
        }

        // English fallback parsed once from the embedded en.properties, for keys the active dictionary lacks.
        private static Dictionary<string, string> _englishFallback;

        private static Dictionary<string, string> EnglishFallback
        {
            get
            {
                if (_englishFallback == null)
                {
                    var dict = new Dictionary<string, string>();
                    try
                    {
                        foreach (var pair in PropertiesLocaleSource.LoadRaw("en"))
                            if (pair.Key.Length == 0 || pair.Key[0] != '@')
                                dict[pair.Key] = pair.Value; // runtime CS2MP.* keys only
                    }
                    catch (Exception)
                    {
                        // A missing resource must not throw out of a polled status getter; T() returns the key.
                    }
                    _englishFallback = dict;
                }
                return _englishFallback;
            }
        }

        /// <summary>Translate a runtime key using the game's active language.</summary>
        public static string T(string key)
        {
            GameManager manager = GameManager.instance;
            if (manager != null && manager.localizationManager != null)
            {
                var dictionary = manager.localizationManager.activeDictionary;
                if (dictionary != null && dictionary.TryGetValue(key, out string value) && !string.IsNullOrEmpty(value))
                    return value;
            }

            return EnglishFallback.TryGetValue(key, out string english) ? english : key;
        }

        /// <summary>
        /// Translates and formats; a malformed translated placeholder falls back to the English format.
        /// </summary>
        public static string F(string key, params object[] args)
        {
            string format = T(key);
            try
            {
                return string.Format(format, args);
            }
            catch (FormatException)
            {
                return EnglishFallback.TryGetValue(key, out string english) ? string.Format(english, args) : key;
            }
        }
    }
}
