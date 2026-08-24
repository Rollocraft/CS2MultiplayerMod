using System;
using System.Collections.Generic;
using Game.SceneFlow;

namespace CS2MultiplayerMod.Localization
{
    /// <summary>
    /// Runtime translation lookup for strings the mod computes in code (status lines,
    /// host-state messages, the join dialog's headline/detail). Static option labels
    /// are resolved by the game itself from the registered locale sources
    /// (<see cref="PropertiesLocaleSource"/>, one per language); this helper covers
    /// values that are produced per frame and therefore must be translated at read time.
    /// The mod always follows the game language and switches live.
    /// Lookup order: active game dictionary -> built-in English table -> the key itself.
    /// </summary>
    public static class L10n
    {
        /// <summary>
        /// Locale keys for everything the mod resolves at runtime. Settings labels and
        /// descriptions use the game-generated option IDs instead and have no constants.
        /// </summary>
        public static class Key
        {
            // -- Main-menu multiplayer screen (read by the UI module via useLocalization) --
            public const string UiJoinGame = "CS2MP.UI.JoinGame";
            public const string UiHostGame = "CS2MP.UI.HostGame";
            public const string UiHostWorldTitle = "CS2MP.UI.HostWorldTitle";
            public const string UiLoadWorld = "CS2MP.UI.LoadWorld";
            public const string UiCreateWorld = "CS2MP.UI.CreateWorld";
            public const string UiDialogTitle = "CS2MP.UI.DialogTitle";
            public const string UiPlayerName = "CS2MP.UI.PlayerName";
            public const string UiHostAddress = "CS2MP.UI.HostAddress";
            public const string UiPort = "CS2MP.UI.Port";
            public const string UiPassword = "CS2MP.UI.Password";
            public const string UiWorldTransfer = "CS2MP.UI.WorldTransfer";
            public const string UiJoin = "CS2MP.UI.Join";
            public const string UiDisconnect = "CS2MP.UI.Disconnect";
            public const string UiCloseSession = "CS2MP.UI.CloseSession";
            public const string UiClose = "CS2MP.UI.Close";
            public const string UiOpenHelp = "CS2MP.UI.OpenHelp";

            // -- Native confirmation shown before an explicit host/client disconnect --
            public const string UiCloseSessionTitle = "CS2MP.UI.CloseSessionTitle";
            public const string UiCloseSessionBody = "CS2MP.UI.CloseSessionBody";
            public const string UiLeaveSessionTitle = "CS2MP.UI.LeaveSessionTitle";
            public const string UiLeaveSessionBody = "CS2MP.UI.LeaveSessionBody";

            // -- In-game multiplayer hub (right-menu button + panel) --
            public const string UiMultiplayer = "CS2MP.UI.Multiplayer";
            public const string UiSessionSettings = "CS2MP.UI.SessionSettings";
            public const string UiBack = "CS2MP.UI.Back";
            public const string UiChatPlaceholder = "CS2MP.UI.ChatPlaceholder";
            public const string UiSend = "CS2MP.UI.Send";
            public const string UiNoMessages = "CS2MP.UI.NoMessages";
            public const string UiHostSession = "CS2MP.UI.HostSession";
            public const string UiLanOnly = "CS2MP.UI.LanOnly";
            public const string UiMaxPlayers = "CS2MP.UI.MaxPlayers";
            public const string UiResyncMinutes = "CS2MP.UI.ResyncMinutes";
            public const string UiSyncWorld = "CS2MP.UI.SyncWorld";
            public const string UiLockedInSession = "CS2MP.UI.LockedInSession";
            public const string UiPlayers = "CS2MP.UI.Players";
            public const string UiHost = "CS2MP.UI.Host";
            public const string UiYou = "CS2MP.UI.You";
            public const string UiKick = "CS2MP.UI.Kick";
            public const string UiConfirmKick = "CS2MP.UI.ConfirmKick";
            public const string UiBan = "CS2MP.UI.Ban";
            public const string UiConfirmBan = "CS2MP.UI.ConfirmBan";
            public const string UiBanHint = "CS2MP.UI.BanHint";
            public const string UiCancelKick = "CS2MP.UI.CancelKick";
            public const string UiSendingWorld = "CS2MP.UI.SendingWorld";
            public const string UiTryThis = "CS2MP.UI.TryThis";
            public const string UiRequireApproval = "CS2MP.UI.RequireApproval";
            public const string UiJoinRequestTitle = "CS2MP.UI.JoinRequestTitle";
            // {0} = joining player's name.
            public const string UiJoinRequestBody = "CS2MP.UI.JoinRequestBody";
            public const string UiAccept = "CS2MP.UI.Accept";
            public const string UiDecline = "CS2MP.UI.Decline";

            // -- One-time disclaimer gate (shown before first host/join) --
            public const string UiDisclaimerTitle = "CS2MP.UI.DisclaimerTitle";
            public const string UiDisclaimerBody = "CS2MP.UI.DisclaimerBody";
            public const string UiDisclaimerAccept = "CS2MP.UI.DisclaimerAccept";
            public const string UiDisclaimerDecline = "CS2MP.UI.DisclaimerDecline";

            // -- Untested game-version warning banner --
            public const string UiVersionWarningTitle = "CS2MP.UI.VersionWarningTitle";
            // {0} = running build, {1} = comma-separated tested builds.
            public const string UiVersionWarning = "CS2MP.UI.VersionWarning";

            // -- Other-mods block (host and join are both refused while any is live) --
            public const string UiModsBlockedTitle = "CS2MP.UI.ModsBlockedTitle";
            // {0} = comma-separated names of the other live mods.
            public const string UiModsBlocked = "CS2MP.UI.ModsBlocked";
            // Same, for a block that came from the loaded-assembly fallback: that one only
            // clears on restart, so it must not tell the player to just toggle the mod off.
            public const string UiModsBlockedRestart = "CS2MP.UI.ModsBlockedRestart";
            public const string UiModsIgnoredTitle = "CS2MP.UI.ModsIgnoredTitle";
            public const string UiModsIgnored = "CS2MP.UI.ModsIgnored";

            // -- Full-screen join loading overlay --
            public const string UiCancel = "CS2MP.UI.Cancel";
            public const string UiJoiningTitle = "CS2MP.UI.JoiningTitle";
            public const string UiLoadingHint = "CS2MP.UI.LoadingHint";

            // -- Session status (options screen Status group + join dialog indicator) --
            public const string StatusDisabled = "CS2MP.Status.Disabled";
            public const string StatusOffline = "CS2MP.Status.Offline";
            public const string RoleHost = "CS2MP.Status.RoleHost";
            public const string RoleClient = "CS2MP.Status.RoleClient";
            public const string StateConnecting = "CS2MP.Status.Connecting";
            public const string StateConnected = "CS2MP.Status.Connected";
            public const string StateFaulted = "CS2MP.Status.Faulted";
            public const string OfflineFault = "CS2MP.Status.OfflineFault";
            public const string PlayersNone = "CS2MP.Status.PlayersNone";
            public const string PlayersClients = "CS2MP.Status.PlayersClients";
            public const string ConnectedToHost = "CS2MP.Status.ConnectedToHost";
            public const string NoSession = "CS2MP.Status.NoSession";
            public const string AccessPassword = "CS2MP.Status.AccessPassword";
            public const string AccessOpen = "CS2MP.Status.AccessOpen";
            public const string ExposureInternet = "CS2MP.Status.ExposureInternet";
            public const string ExposureLan = "CS2MP.Status.ExposureLan";
            public const string ExposureRelay = "CS2MP.Status.ExposureRelay";
            public const string ExposureRelayClient = "CS2MP.Status.ExposureRelayClient";
            public const string ExposureForwarding = "CS2MP.Status.ExposureForwarding";
            public const string ExposureForwarded = "CS2MP.Status.ExposureForwarded";
            public const string ExposureForwardedAt = "CS2MP.Status.ExposureForwardedAt";
            public const string ExposureForwardManually = "CS2MP.Status.ExposureForwardManually";
            public const string WorldNone = "CS2MP.Status.WorldNone";
            public const string WorldHosting = "CS2MP.Status.WorldHosting";
            public const string WorldMapProgress = "CS2MP.Status.WorldMapProgress";
            public const string WorldLoaded = "CS2MP.Status.WorldLoaded";
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

            // -- Connection mode --
            public const string ConnectionRelay = "CS2MP.Connection.Relay";
            public const string ConnectionDirect = "CS2MP.Connection.Direct";
            public const string ConnectionMode = "CS2MP.Connection.Mode";
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

        // English fallback for runtime keys, parsed once from the embedded en.properties
        // (the same file the en-US locale source loads). Used when the active dictionary
        // has no entry — e.g. an unsupported game language — so the English text still
        // lives in exactly one place: the .properties file.
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
                        // A missing/corrupt resource must not throw out of a status getter
                        // polled by the UI; T() then returns the key itself as last resort.
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
                string value;
                if (dictionary != null && dictionary.TryGetValue(key, out value) && !string.IsNullOrEmpty(value))
                    return value;
            }

            string english;
            return EnglishFallback.TryGetValue(key, out english) ? english : key;
        }

        /// <summary>
        /// Translate and <see cref="string.Format(string,object[])"/> a runtime key.
        /// A malformed placeholder in a translation falls back to the English format - a bad
        /// locale entry must never throw out of a status getter polled by the UI.
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
                string english;
                return EnglishFallback.TryGetValue(key, out english) ? string.Format(english, args) : key;
            }
        }
    }
}
