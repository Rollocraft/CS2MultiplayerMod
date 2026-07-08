using Colossal.IO.AssetDatabase;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Localization;
using Game;
using Game.Modding;
using Game.SceneFlow;
using Game.Settings;

namespace CS2MultiplayerMod
{
    [FileLocation(nameof(CS2MultiplayerMod))]
    [SettingsUITabOrder(GeneralTab, JoinTab, HostTab)]
    [SettingsUIGroupOrder(GeneralGroup, StatusGroup, SessionGroup, JoinSetupGroup, JoinActionGroup, HostSetupGroup, HostActionGroup)]
    [SettingsUIShowGroupName(GeneralGroup, StatusGroup, SessionGroup, JoinSetupGroup, JoinActionGroup, HostSetupGroup, HostActionGroup)]
    public class Setting : ModSetting
    {
        // The options UI exposes general/session state plus join and host setup.
        // The Join tab shares its backing values with the start-screen dialog and
        // doubles as the fallback join path when the dialog's UI module cannot
        // load (e.g. another mod's broken .mjs aborts the UI-module load chain).
        public const string GeneralTab = "General";
        public const string JoinTab = "Join";
        public const string HostTab = "Host";

        public const string GeneralGroup = "General";
        public const string StatusGroup = "Status";
        public const string SessionGroup = "Session";
        public const string JoinSetupGroup = "JoinSetup";
        public const string JoinActionGroup = "JoinAction";
        public const string HostSetupGroup = "HostSetup";
        public const string HostActionGroup = "HostAction";

        private string _hostPort = "25001";
        private string _hostPassword = "";

        public Setting(IMod mod) : base(mod)
        {
        }

        /// <summary>
        /// True when no playable world is loaded. Gates host-side actions only (hosting
        /// streams the current city); joining works from anywhere, so it stays unaffected.
        /// </summary>
        public bool IsNotInGame()
        {
            return GameManager.instance == null || !GameManager.instance.gameMode.IsGame();
        }

        public bool IsNotInSession()
        {
            return Mod.Service == null || Mod.Service.Session.Role == SessionRole.None;
        }

        public bool IsInSession()
        {
            return !IsNotInSession();
        }

        public bool IsHosting()
        {
            return Mod.Service != null && Mod.Service.Session.Role == SessionRole.Host;
        }

        public bool IsNotHosting()
        {
            return !IsHosting();
        }

        public bool CannotStartHost()
        {
            return IsNotInGame() || !IsNotInSession();
        }

        // ---- General tab ------------------------------------------------------

        [SettingsUISection(GeneralTab, GeneralGroup)]
        public bool EnableMod { get; set; } = true;

        [SettingsUITextInput]
        [SettingsUISection(GeneralTab, GeneralGroup)]
        public string PlayerName { get; set; } = "Player";

        /// <summary>
        /// Off: only the important lines (connect/disconnect, world transfer, faults).
        /// On: also the per-action sync notices and periodic diagnostics. See <see cref="Mod.Verbose"/>.
        /// </summary>
        [SettingsUISection(GeneralTab, GeneralGroup)]
        public bool VerboseLogging { get; set; } = false;

        /// <summary>
        /// Set once the player accepts the in-game disclaimer gate (shown before the
        /// first host/join). Persisted so it only appears once; intentionally hidden
        /// from the options screen and left out of <see cref="SetDefaults"/> so that
        /// resetting other settings does not re-prompt an existing user.
        /// </summary>
        [SettingsUIHidden]
        public bool DisclaimerAccepted { get; set; } = false;

        [SettingsUISection(GeneralTab, StatusGroup)]
        public string StatusRole => Mod.Service != null ? Mod.Service.StatusRoleText : L10n.T(L10n.Key.StatusOffline);

        [SettingsUISection(GeneralTab, StatusGroup)]
        public string StatusState => Mod.Service != null ? Mod.Service.StatusStateText : L10n.T(L10n.Key.StatusOffline);

        [SettingsUISection(GeneralTab, StatusGroup)]
        public string StatusPlayers => Mod.Service != null ? Mod.Service.StatusPlayersText : L10n.T(L10n.Key.PlayersNone);

        [SettingsUISection(GeneralTab, StatusGroup)]
        public string StatusAccess => Mod.Service != null ? Mod.Service.StatusAccessText : L10n.T(L10n.Key.NoSession);

        [SettingsUISection(GeneralTab, StatusGroup)]
        public string StatusExposure => Mod.Service != null ? Mod.Service.StatusExposureText : L10n.T(L10n.Key.NoSession);

        [SettingsUISection(GeneralTab, StatusGroup)]
        public string StatusWorld => Mod.Service != null ? Mod.Service.StatusWorldText : L10n.T(L10n.Key.WorldNone);

        [SettingsUIButton]
        [SettingsUIHideByCondition(typeof(Setting), nameof(IsNotInSession))]
        [SettingsUISection(GeneralTab, SessionGroup)]
        public bool DisconnectButton
        {
            set { if (Mod.Service != null) Mod.Service.Disconnect(); }
        }

        // ---- Host tab -----------------------------------------------------------

        [SettingsUITextInput]
        [SettingsUISection(HostTab, HostSetupGroup)]
        [SettingsUIHideByCondition(typeof(Setting), nameof(IsNotInGame))]
        [SettingsUIDisableByCondition(typeof(Setting), nameof(IsHosting))]
        public string HostPort
        {
            get { return _hostPort; }
            set
            {
                if (IsHosting()) return;
                _hostPort = value ?? "";
            }
        }

        [SettingsUITextInput]
        [SettingsUISection(HostTab, HostSetupGroup)]
        [SettingsUIHideByCondition(typeof(Setting), nameof(IsNotInGame))]
        [SettingsUIDisableByCondition(typeof(Setting), nameof(IsInSession))]
        public string HostPassword
        {
            get { return _hostPassword; }
            set
            {
                if (IsInSession()) return;
                _hostPassword = value ?? "";
            }
        }

        [SettingsUIHideByCondition(typeof(Setting), nameof(IsNotInGame))]
        [SettingsUISection(HostTab, HostSetupGroup)]
        public bool LanOnly { get; set; } = false;

        [SettingsUITextInput]
        [SettingsUIHideByCondition(typeof(Setting), nameof(IsNotInGame))]
        [SettingsUISection(HostTab, HostSetupGroup)]
        public string MaxPlayers { get; set; } = "8";

        [SettingsUITextInput]
        [SettingsUIHideByCondition(typeof(Setting), nameof(IsNotInGame))]
        [SettingsUISection(HostTab, HostSetupGroup)]
        public string ResyncMinutes { get; set; } = "15";

        [SettingsUIHideByCondition(typeof(Setting), nameof(IsNotInGame))]
        [SettingsUISection(HostTab, HostActionGroup)]
        public string HostStatus => IsNotInGame()
            ? L10n.T(L10n.Key.HostLoadCityFirst)
            : (IsNotInSession() ? L10n.T(L10n.Key.HostReady) : L10n.T(L10n.Key.HostSessionActive));

        [SettingsUIButton]
        [SettingsUIHideByCondition(typeof(Setting), nameof(CannotStartHost))]
        [SettingsUISection(HostTab, HostActionGroup)]
        public bool HostButton
        {
            set { if (Mod.Service != null) Mod.Service.HostFromSettings(this); }
        }

        /// <summary>
        /// Push the host's world to all clients now - the manual drift safety-net, same as the
        /// in-game hub's "Sync World". Duplicated here so it stays reachable if the hub's UI
        /// module fails to load. Host-only.
        /// </summary>
        [SettingsUIButton]
        [SettingsUIHideByCondition(typeof(Setting), nameof(IsNotHosting))]
        [SettingsUISection(HostTab, HostActionGroup)]
        public bool SyncWorldButton
        {
            set { if (Mod.Service != null) Mod.Service.RequestWorldSync(); }
        }

        // ---- Join tab -----------------------------------------------------------
        // Shared backing values: the start-screen dialog writes the same properties
        // through the cs2mp bindings, so dialog and options screen always agree.
        // Joining needs no loaded city (the world comes from the host), so these
        // stay visible in the main menu and are only disabled mid-session.

        [SettingsUITextInput]
        [SettingsUISection(JoinTab, JoinSetupGroup)]
        [SettingsUIDisableByCondition(typeof(Setting), nameof(IsInSession))]
        public string ServerAddress { get; set; } = "127.0.0.1";

        [SettingsUITextInput]
        [SettingsUISection(JoinTab, JoinSetupGroup)]
        [SettingsUIDisableByCondition(typeof(Setting), nameof(IsInSession))]
        public string JoinPort { get; set; } = "25001";

        [SettingsUITextInput]
        [SettingsUISection(JoinTab, JoinSetupGroup)]
        [SettingsUIDisableByCondition(typeof(Setting), nameof(IsInSession))]
        public string JoinPassword { get; set; } = "";

        [SettingsUISection(JoinTab, JoinActionGroup)]
        public string JoinStatus => Mod.Service == null
            ? L10n.T(L10n.Key.StatusOffline)
            : (string.IsNullOrEmpty(Mod.Service.UiStatusDetail)
                ? Mod.Service.UiStatusTitle
                : Mod.Service.UiStatusTitle + " - " + Mod.Service.UiStatusDetail);

        [SettingsUIButton]
        [SettingsUIHideByCondition(typeof(Setting), nameof(IsInSession))]
        [SettingsUISection(JoinTab, JoinActionGroup)]
        public bool JoinButton
        {
            set
            {
                if (Mod.Service == null) return;
                ApplyAndSave();
                Mod.Service.JoinFromSettings(this);
            }
        }

        [SettingsUIButton]
        [SettingsUIHideByCondition(typeof(Setting), nameof(IsNotInSession))]
        [SettingsUISection(JoinTab, JoinActionGroup)]
        public bool JoinDisconnectButton
        {
            set { if (Mod.Service != null) Mod.Service.Disconnect(); }
        }

        public override void SetDefaults()
        {
            EnableMod = true;
            VerboseLogging = false;
            PlayerName = "Player";
            ServerAddress = "127.0.0.1";
            HostPort = "25001";
            JoinPort = "25001";
            HostPassword = "";
            JoinPassword = "";
            LanOnly = false;
            MaxPlayers = "8";
            ResyncMinutes = "15";
        }
    }
}
