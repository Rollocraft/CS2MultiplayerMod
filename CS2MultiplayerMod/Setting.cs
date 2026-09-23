using Colossal.IO.AssetDatabase;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Networking;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Localization;
using Game;
using Game.Modding;
using Game.SceneFlow;
using Game.Settings;
using Game.UI.Localization;
using Game.UI.Widgets;

namespace CS2MultiplayerMod
{
    [FileLocation(nameof(CS2MultiplayerMod))]
    [SettingsUITabOrder(GeneralTab, JoinTab, HostTab, AdvancedTab)]
    [SettingsUIGroupOrder(GeneralGroup, SessionGroup, JoinSetupGroup, JoinActionGroup,
        HostSetupGroup, HostActionGroup, CompatibilityGroup)]
    [SettingsUIShowGroupName(GeneralGroup, SessionGroup, JoinSetupGroup, JoinActionGroup,
        HostSetupGroup, HostActionGroup, CompatibilityGroup)]
    public class Setting : ModSetting
    {
        // The Join tab shares its values with the start-screen dialog and is the fallback join path when
        // the UI module fails to load.
        public const string GeneralTab = "General";
        public const string JoinTab = "Join";
        public const string HostTab = "Host";
        public const string AdvancedTab = "Advanced";

        public const string GeneralGroup = "General";
        public const string SessionGroup = "Session";
        public const string JoinSetupGroup = "JoinSetup";
        public const string JoinActionGroup = "JoinAction";
        public const string HostSetupGroup = "HostSetup";
        public const string HostActionGroup = "HostAction";
        public const string CompatibilityGroup = "Compatibility";

        /// <summary>Values of <see cref="HostConnection"/>. Stored as strings so the UI binding is one plain value.</summary>
        public const string ConnectionRelay = "relay";
        public const string ConnectionDirect = "direct";
        public const string ResyncAllow = "allow";
        public const string ResyncApproval = "approval";
        public const string ResyncHostOnly = "hostOnly";

        private string _hostPort = "25001";
        private string _hostPassword = "";
        private string _hostConnection = ConnectionRelay;
        private string _joinConnection = ConnectionRelay;

        public Setting(IMod mod) : base(mod)
        {
        }

        /// <summary>No playable world loaded. Gates hosting only; joining works anywhere.</summary>
        public bool IsNotInGame() => GameManager.instance == null || !GameManager.instance.gameMode.IsGame();

        public bool IsNotInSession() => Mod.Service == null || Mod.Service.Session.Role == SessionRole.None;

        public bool IsInSession() => !IsNotInSession();

        public bool IsHosting() => Mod.Service != null && Mod.Service.Session.Role == SessionRole.Host;

        public bool IsNotHosting() => !IsHosting();

        /// <summary>Also true while another mod is live: the options screen's Host button reaches the service directly.</summary>
        public bool CannotStartHost()
        {
            return IsNotInGame() || !IsNotInSession() ||
                   (CS2MultiplayerMod.Game.ModsCheck.AnyOtherMods && !IgnoreModCompatibilityChecks);
        }

        /// <summary>No relay backend (Microsoft Store / Game Pass): the picker is hidden and direct is used.</summary>
        public bool RelayUnsupported() => !RelayProvider.IsSupported;

        /// <summary>Relay hosting opens no port, so the port and LAN controls do not apply.</summary>
        public bool IsRelayHosting() => HostConnection != ConnectionDirect;

        public bool IsDirectHosting() => !IsRelayHosting();

        public bool HideAutoApproveSteamFriends() => IsDirectHosting() || !RequireJoinApproval;

        /// <summary>How this machine will be reached, resolved once at host/join time.</summary>
        public TransportMode HostTransport() => IsRelayHosting() ? TransportMode.SteamRelay : TransportMode.Direct;

        // ---- General tab ------------------------------------------------------

        /// <summary>Mod version, protocol, and on a local build its stamp.</summary>
        [SettingsUISection(GeneralTab, GeneralGroup)]
        public string ModVersion => Mod.VersionLine;

        [SettingsUISection(GeneralTab, GeneralGroup)]
        public bool EnableMod { get; set; } = true;

        /// <summary>The name every copy of the game falls back to when it knows no better.</summary>
        public const string DefaultPlayerName = "Player";

        [SettingsUITextInput]
        [SettingsUISection(GeneralTab, GeneralGroup)]
        public string PlayerName { get; set; } = DefaultPlayerName;

        /// <summary>
        /// Set once <see cref="ApplyPlatformNamePreset"/> has run, so a player who chose "Player" is not
        /// renamed each start.
        /// </summary>
        [SettingsUIHidden]
        public bool PlayerNamePresetApplied { get; set; } = false;

        /// <summary>
        /// First run: use the platform account name instead of "Player". Without a platform backend the
        /// default stays.
        /// </summary>
        public void ApplyPlatformNamePreset()
        {
            if (PlayerNamePresetApplied) return;

            // The player's own choice wins and is final.
            string current = (PlayerName ?? "").Trim();
            if (current.Length > 0 && current != DefaultPlayerName)
            {
                PlayerNamePresetApplied = true;
                ApplyAndSave();
                return;
            }

            // Empty: the platform cannot say yet; leave the flag for a later start.
            string platformName = RelayProvider.LocalPlayerName;
            if (string.IsNullOrEmpty(platformName.Trim())) return;

            string preset = Core.Protocol.WireGuard.SanitizePlayerName(platformName);
            PlayerName = preset;
            PlayerNamePresetApplied = true;
            ApplyAndSave();
            SyncLog.Detail(LogTopic.Startup, "Player name preset from the platform account: '" +
                preset + "'.");
        }

        /// <summary>
        /// The only logging choice. Off, the log still has connects, transfers, resyncs and their causes,
        /// dropped commands, versions, live mods and every fault; on, it adds per-action detail. Cheap to
        /// leave on. Per-subsystem narrowing is a developer switch in Game/Diagnostics/LogTopics.cs.
        /// </summary>
        [SettingsUISection(GeneralTab, GeneralGroup)]
        public bool VerboseLogging { get; set; } = false;

        /// <summary>The markers are drawn every frame, so their cost scales with resolution, not city size.</summary>
        [SettingsUISection(GeneralTab, GeneralGroup)]
        public bool ShowPartnerMarkers { get; set; } = true;

        /// <summary>
        /// Disclaimer accepted; hidden and excluded from <see cref="SetDefaults"/> so a reset does not re-prompt.
        /// </summary>
        [SettingsUIHidden]
        public bool DisclaimerAccepted { get; set; } = false;

        [SettingsUIButton]
        [SettingsUIHideByCondition(typeof(Setting), nameof(IsNotInSession))]
        [SettingsUISection(GeneralTab, SessionGroup)]
        public bool DisconnectButton
        {
            set { if (Mod.Service != null) Mod.Service.RequestDisconnect(); }
        }

        // ---- Host tab -----------------------------------------------------------
        // Editable in the main menu so Host Game can use it once its city loads; Host Session needs a city.

        /// <summary>Relay needs no reachable port (players use the join code); direct needs a forwarded port or LAN.</summary>
        [SettingsUIDropdown(typeof(Setting), nameof(GetHostConnectionValues))]
        [SettingsUISection(HostTab, HostSetupGroup)]
        [SettingsUIDisableByCondition(typeof(Setting), nameof(IsInSession))]
        [SettingsUIHideByCondition(typeof(Setting), nameof(RelayUnsupported))]
        public string HostConnection
        {
            // Direct where there is no relay backend, so every reader agrees.
            get             // Direct where there is no relay backend, so every reader agrees.
            => RelayProvider.IsSupported ? _hostConnection : ConnectionDirect;
            set
            {
                if (IsInSession()) return;
                _hostConnection = value == ConnectionDirect ? ConnectionDirect : ConnectionRelay;
            }
        }

        public static DropdownItem<string>[] GetHostConnectionValues()
        {
            return new[]
            {
                new DropdownItem<string>
                {
                    value = ConnectionRelay,
                    displayName = LocalizedString.Value(L10n.T(L10n.Key.ConnectionRelay)),
                },
                new DropdownItem<string>
                {
                    value = ConnectionDirect,
                    displayName = LocalizedString.Value(L10n.T(L10n.Key.ConnectionDirect)),
                },
            };
        }

        /// <summary>The code players type on their Join screen. Relay hosting only.</summary>
        [SettingsUIHideByCondition(typeof(Setting), nameof(IsDirectHosting))]
        [SettingsUISection(HostTab, HostSetupGroup)]
        public string HostJoinCode
        {
            get
            {
                string code = RelayProvider.LocalJoinCode;
                return string.IsNullOrEmpty(code) ? L10n.T(L10n.Key.JoinCodeUnavailable) : code;
            }
        }

        [SettingsUITextInput]
        [SettingsUIHideByCondition(typeof(Setting), nameof(IsRelayHosting))]
        [SettingsUISection(HostTab, HostSetupGroup)]
        [SettingsUIDisableByCondition(typeof(Setting), nameof(IsHosting))]
        public string HostPort
        {
            get => _hostPort;
            set
            {
                if (IsHosting()) return;
                _hostPort = value ?? "";
            }
        }

        [SettingsUITextInput]
        [SettingsUISection(HostTab, HostSetupGroup)]
        [SettingsUIDisableByCondition(typeof(Setting), nameof(IsInSession))]
        public string HostPassword
        {
            get => _hostPassword;
            set
            {
                if (IsInSession()) return;
                _hostPassword = value ?? "";
            }
        }

        [SettingsUIHideByCondition(typeof(Setting), nameof(IsRelayHosting))]
        [SettingsUISection(HostTab, HostSetupGroup)]
        public bool LanOnly { get; set; } = false;

        [SettingsUIDisableByCondition(typeof(Setting), nameof(IsInSession))]
        [SettingsUISection(HostTab, HostSetupGroup)]
        public bool RequireJoinApproval { get; set; } = true;

        [SettingsUIHideByCondition(typeof(Setting), nameof(HideAutoApproveSteamFriends))]
        [SettingsUIDisableByCondition(typeof(Setting), nameof(IsInSession))]
        [SettingsUISection(HostTab, HostSetupGroup)]
        public bool AutoApproveSteamFriends { get; set; } = false;

        [SettingsUIDropdown(typeof(Setting), nameof(GetResyncPolicyValues))]
        [SettingsUIDisableByCondition(typeof(Setting), nameof(IsInSession))]
        [SettingsUISection(HostTab, HostSetupGroup)]
        public string ResyncPolicy { get; set; } = ResyncAllow;

        public static DropdownItem<string>[] GetResyncPolicyValues()
        {
            return new[]
            {
                new DropdownItem<string>
                {
                    value = ResyncAllow,
                    displayName = LocalizedString.Value(L10n.T(L10n.Key.UiResyncAllow)),
                },
                new DropdownItem<string>
                {
                    value = ResyncApproval,
                    displayName = LocalizedString.Value(L10n.T(L10n.Key.UiResyncApproval)),
                },
                new DropdownItem<string>
                {
                    value = ResyncHostOnly,
                    displayName = LocalizedString.Value(L10n.T(L10n.Key.UiResyncHostOnly)),
                },
            };
        }

        public ClientResyncPolicy SelectedClientResyncPolicy()
        {
            if (ResyncPolicy == ResyncApproval) return Core.Session.ClientResyncPolicy.RequireApproval;
            if (ResyncPolicy == ResyncHostOnly) return Core.Session.ClientResyncPolicy.HostOnly;
            return Core.Session.ClientResyncPolicy.Allow;
        }

        [SettingsUITextInput]
        [SettingsUISection(HostTab, HostSetupGroup)]
        public string MaxPlayers { get; set; } = "8";

        /// <summary>
        /// Host switch for the simulation half of the session, announced in the handshake. Player edits are
        /// unaffected. Off, each city grows, houses, employs and shows demand locally, and none of the
        /// population-scaled correction work runs. Shown only in the in-game session settings.
        /// </summary>
        [SettingsUIHidden]
        public bool SimulationSync { get; set; } = true;

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

        /// <summary>Host only: push the world to all clients, as the hub's "Sync World"; reachable if the hub fails to load.</summary>
        [SettingsUIButton]
        [SettingsUIHideByCondition(typeof(Setting), nameof(IsNotHosting))]
        [SettingsUISection(HostTab, HostActionGroup)]
        public bool SyncWorldButton
        {
            set { if (Mod.Service != null) Mod.Service.RequestWorldSync(); }
        }

        // ---- Join tab -----------------------------------------------------------
        // Shared with the start-screen dialog through the cs2mp bindings; visible in the main menu, disabled
        // mid-session.

        [SettingsUITextInput]
        [SettingsUIHideByCondition(typeof(Setting), nameof(JoinIsRelay))]
        [SettingsUISection(JoinTab, JoinSetupGroup)]
        [SettingsUIDisableByCondition(typeof(Setting), nameof(IsInSession))]
        public string ServerAddress { get; set; } = "127.0.0.1";

        /// <summary>Chosen explicitly, not guessed from the text, so the joiner sees the host's choice.</summary>
        public bool JoinIsRelay() => JoinConnection != ConnectionDirect;

        public bool JoinIsDirect() => !JoinIsRelay();

        public TransportMode JoinTransport() => JoinIsRelay() ? TransportMode.SteamRelay : TransportMode.Direct;

        [SettingsUIDropdown(typeof(Setting), nameof(GetHostConnectionValues))]
        [SettingsUISection(JoinTab, JoinSetupGroup)]
        [SettingsUIDisableByCondition(typeof(Setting), nameof(IsInSession))]
        [SettingsUIHideByCondition(typeof(Setting), nameof(RelayUnsupported))]
        public string JoinConnection
        {
            get => RelayProvider.IsSupported ? _joinConnection : ConnectionDirect;
            set
            {
                if (IsInSession()) return;
                _joinConnection = value == ConnectionDirect ? ConnectionDirect : ConnectionRelay;
            }
        }

        /// <summary>Kept apart from <see cref="ServerAddress"/> so switching mode loses neither.</summary>
        [SettingsUITextInput]
        [SettingsUIHideByCondition(typeof(Setting), nameof(JoinIsDirect))]
        [SettingsUISection(JoinTab, JoinSetupGroup)]
        [SettingsUIDisableByCondition(typeof(Setting), nameof(IsInSession))]
        public string JoinCodeInput { get; set; } = "";

        [SettingsUITextInput]
        [SettingsUIHideByCondition(typeof(Setting), nameof(JoinIsRelay))]
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
            set { if (Mod.Service != null) Mod.Service.RequestDisconnect(); }
        }

        // ---- Advanced tab -------------------------------------------------------

        /// <summary>
        /// Expert override for mod-specific checks: permits other live mods and admits a different build of
        /// this mod. Protocol, game-version and DLC checks stay mandatory.
        /// </summary>
        [SettingsUISection(AdvancedTab, CompatibilityGroup)]
        [SettingsUIDisableByCondition(typeof(Setting), nameof(IsInSession))]
        public bool IgnoreModCompatibilityChecks { get; set; } = false;

        public override void SetDefaults()
        {
            EnableMod = true;
            VerboseLogging = false;
            ShowPartnerMarkers = true;
            IgnoreModCompatibilityChecks = false;
            PlayerName = DefaultPlayerName;
            // On a signed-in copy the default name is the account name.
            PlayerNamePresetApplied = false;
            ServerAddress = "127.0.0.1";
            HostConnection = ConnectionRelay;
            JoinConnection = ConnectionRelay;
            JoinCodeInput = "";
            HostPort = "25001";
            JoinPort = "25001";
            HostPassword = "";
            JoinPassword = "";
            LanOnly = false;
            RequireJoinApproval = true;
            AutoApproveSteamFriends = false;
            ResyncPolicy = ResyncAllow;
            SimulationSync = true;
            MaxPlayers = "8";
        }
    }
}
