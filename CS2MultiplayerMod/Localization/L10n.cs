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
            // -- Main-menu Join Game dialog (read by the UI module via useLocalization) --
            public const string UiJoinGame = "CS2MP.UI.JoinGame";
            public const string UiDialogTitle = "CS2MP.UI.DialogTitle";
            public const string UiPlayerName = "CS2MP.UI.PlayerName";
            public const string UiHostAddress = "CS2MP.UI.HostAddress";
            public const string UiPort = "CS2MP.UI.Port";
            public const string UiPassword = "CS2MP.UI.Password";
            public const string UiWorldTransfer = "CS2MP.UI.WorldTransfer";
            public const string UiJoin = "CS2MP.UI.Join";
            public const string UiDisconnect = "CS2MP.UI.Disconnect";
            public const string UiClose = "CS2MP.UI.Close";

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

            // -- One-time disclaimer gate (shown before first host/join) --
            public const string UiDisclaimerTitle = "CS2MP.UI.DisclaimerTitle";
            public const string UiDisclaimerBody = "CS2MP.UI.DisclaimerBody";
            public const string UiDisclaimerAccept = "CS2MP.UI.DisclaimerAccept";
            public const string UiDisclaimerDecline = "CS2MP.UI.DisclaimerDecline";

            // -- Untested game-version warning banner --
            public const string UiVersionWarningTitle = "CS2MP.UI.VersionWarningTitle";
            // {0} = running build, {1} = comma-separated tested builds.
            public const string UiVersionWarning = "CS2MP.UI.VersionWarning";

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
            public const string WorldNone = "CS2MP.Status.WorldNone";
            public const string WorldHosting = "CS2MP.Status.WorldHosting";
            public const string WorldMapProgress = "CS2MP.Status.WorldMapProgress";
            public const string WorldLoaded = "CS2MP.Status.WorldLoaded";
            public const string PhaseWaitingForMap = "CS2MP.Status.WaitingForMap";
            public const string PhaseLoadingMap = "CS2MP.Status.LoadingMap";
            public const string TitleModDisabled = "CS2MP.Status.ModDisabled";
            public const string TitleConnectionFailed = "CS2MP.Status.ConnectionFailed";
            public const string TitleHosting = "CS2MP.Status.Hosting";
            public const string DetailEnableMod = "CS2MP.Status.DetailEnableMod";
            public const string DetailPlayersOne = "CS2MP.Status.DetailPlayersOne";
            public const string DetailPlayersMany = "CS2MP.Status.DetailPlayersMany";
            public const string DetailPasswordProtected = "CS2MP.Status.DetailPasswordProtected";
            public const string DetailOpenAccess = "CS2MP.Status.DetailOpenAccess";
            public const string DetailPublic = "CS2MP.Status.DetailPublic";

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
