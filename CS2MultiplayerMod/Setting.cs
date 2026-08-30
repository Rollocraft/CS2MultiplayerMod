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
    [SettingsUITabOrder(GeneralTab, JoinTab, HostTab, LoggingTab)]
    [SettingsUIGroupOrder(GeneralGroup, StatusGroup, SessionGroup, JoinSetupGroup, JoinActionGroup,
        HostSetupGroup, HostActionGroup,
        LogAllGroup, LogConnectionGroup, LogWorldGroup, LogEconomyGroup, LogClientGroup)]
    [SettingsUIShowGroupName(GeneralGroup, StatusGroup, SessionGroup, JoinSetupGroup, JoinActionGroup,
        HostSetupGroup, HostActionGroup,
        LogAllGroup, LogConnectionGroup, LogWorldGroup, LogEconomyGroup, LogClientGroup)]
    public class Setting : ModSetting
    {
        // The options UI exposes general/session state plus join and host setup.
        // The Join tab shares its backing values with the start-screen dialog and
        // doubles as the fallback join path when the dialog's UI module cannot
        // load (e.g. another mod's broken .mjs aborts the UI-module load chain).
        public const string GeneralTab = "General";
        public const string JoinTab = "Join";
        public const string HostTab = "Host";

        // Its own tab, not a group on General: there is one switch per feature (see LogTopic),
        // which is the point of them - but eighteen checkboxes wedged under the player-name field
        // would be the first thing anyone sees when they open the mod's options.
        public const string LoggingTab = "Logging";

        public const string GeneralGroup = "General";
        public const string StatusGroup = "Status";
        public const string SessionGroup = "Session";
        public const string JoinSetupGroup = "JoinSetup";
        public const string JoinActionGroup = "JoinAction";
        public const string HostSetupGroup = "HostSetup";
        public const string HostActionGroup = "HostAction";

        // The logging switches, grouped the way a player narrows a problem down: first "can I get
        // in", then "is the city the same", then "are the numbers right", then "is it my client".
        public const string LogAllGroup = "LogAll";
        public const string LogConnectionGroup = "LogConnection";
        public const string LogWorldGroup = "LogWorld";
        public const string LogEconomyGroup = "LogEconomy";
        public const string LogClientGroup = "LogClient";

        /// <summary>Values of <see cref="HostConnection"/>. Stored as strings so the UI binding is one plain value.</summary>
        public const string ConnectionRelay = "relay";
        public const string ConnectionDirect = "direct";

        private string _hostPort = "25001";
        private string _hostPassword = "";
        private string _hostConnection = ConnectionRelay;
        private string _joinConnection = ConnectionRelay;

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

        /// <summary>
        /// Also false while another mod is live: the options screen's Host button reaches
        /// the service directly, so the rule has to hold here too and not only on the
        /// multiplayer screens.
        /// </summary>
        public bool CannotStartHost()
        {
            return IsNotInGame() || !IsNotInSession() ||
                   (CS2MultiplayerMod.Game.ModsCheck.AnyOtherMods && !IgnoreModCompatibilityChecks);
        }

        /// <summary>
        /// Whether the relay is a choice on this machine. Copies of the game without Steam
        /// (Microsoft Store / Game Pass) have no relay backend, so the picker is hidden and
        /// everything behaves as if direct had been chosen.
        /// </summary>
        public bool RelayUnsupported()
        {
            return !RelayProvider.IsSupported;
        }

        /// <summary>Relay hosting opens no port, so the port and LAN controls do not apply.</summary>
        public bool IsRelayHosting()
        {
            return HostConnection != ConnectionDirect;
        }

        public bool IsDirectHosting()
        {
            return !IsRelayHosting();
        }

        /// <summary>How this machine will be reached, resolved once at host/join time.</summary>
        public TransportMode HostTransport()
        {
            return IsRelayHosting() ? TransportMode.SteamRelay : TransportMode.Direct;
        }

        // ---- General tab ------------------------------------------------------

        [SettingsUISection(GeneralTab, GeneralGroup)]
        public bool EnableMod { get; set; } = true;

        /// <summary>The name every copy of the game falls back to when it knows no better.</summary>
        public const string DefaultPlayerName = "Player";

        [SettingsUITextInput]
        [SettingsUISection(GeneralTab, GeneralGroup)]
        public string PlayerName { get; set; } = DefaultPlayerName;

        /// <summary>
        /// Set once <see cref="ApplyPlatformNamePreset"/> has had its one chance to fill
        /// <see cref="PlayerName"/> in. Persisted and hidden: without it a player who
        /// deliberately calls themselves "Player" would be renamed on every start.
        /// </summary>
        [SettingsUIHidden]
        public bool PlayerNamePresetApplied { get; set; } = false;

        /// <summary>
        /// First run only: prefer the platform account's own display name over the plain
        /// "Player" default, so a signed-in host is recognisable to the people joining.
        /// Copies of the game with no platform backend (Microsoft Store / Game Pass) have
        /// no name to read and keep the default.
        /// </summary>
        public void ApplyPlatformNamePreset()
        {
            if (PlayerNamePresetApplied) return;

            // Anything the player chose themselves wins, and is recorded as the final
            // answer so a later start never revisits this.
            string current = (PlayerName ?? "").Trim();
            if (current.Length > 0 && current != DefaultPlayerName)
            {
                PlayerNamePresetApplied = true;
                ApplyAndSave();
                return;
            }

            // Empty means the platform cannot say (not signed in yet, or no backend at
            // all). Leave the flag unset so a later start can still pick the name up.
            string platformName = RelayProvider.LocalPlayerName;
            if (string.IsNullOrEmpty(platformName.Trim())) return;

            string preset = Core.Protocol.WireGuard.SanitizePlayerName(platformName);
            PlayerName = preset;
            PlayerNamePresetApplied = true;
            ApplyAndSave();
            SyncLog.Detail(LogTopic.Startup, "Player name preset from the platform account: '" +
                preset + "'.");
        }

        // ---- Logging tab --------------------------------------------------------
        // One switch per feature rather than one "extra logging" switch, because the log that
        // answers a question is the one about the thing that broke: a player chasing missing
        // roads should get roads, not twenty thousand lines of everything else.
        //
        // None of them has to be on for a bug report to be worth reading. Connects,
        // disconnects, world transfers, resyncs, dropped commands and every fault are written
        // whatever is set here (see SyncLog); these only add the per-action detail underneath
        // them. VerboseLogging below is the "I do not know which one" shortcut, not a
        // different kind of logging.

        /// <summary>
        /// The master switch: turns every topic below on at once, without disturbing which
        /// individual ones the player had ticked.
        ///
        /// Safe to leave on - it makes the log longer, not the game slower, because the detail
        /// lines sit behind a field read and the flight log only flushes them in batches. Turn it
        /// on when you have been asked for a full log and do not want to guess which switch
        /// covers the problem.
        /// </summary>
        [SettingsUISection(LoggingTab, LogAllGroup)]
        public bool VerboseLogging { get; set; } = false;

        /// <summary>Connecting, disconnecting, the handshake, and players joining or leaving.</summary>
        [SettingsUISection(LoggingTab, LogConnectionGroup)]
        public bool LogSession { get; set; } = false;

        /// <summary>The wire underneath a session: sockets, the Steam relay, port forwarding, rates.</summary>
        [SettingsUISection(LoggingTab, LogConnectionGroup)]
        public bool LogTransport { get; set; } = false;

        /// <summary>Sending, receiving, staging and loading the world a joining player downloads.</summary>
        [SettingsUISection(LoggingTab, LogConnectionGroup)]
        public bool LogWorldTransfer { get; set; } = false;

        /// <summary>What diverged, what the arbiter decided about it, and what the repair did.</summary>
        [SettingsUISection(LoggingTab, LogWorldGroup)]
        public bool LogResync { get; set; } = false;

        /// <summary>The command pipeline: inbox, observers, authority holds, realization.</summary>
        [SettingsUISection(LoggingTab, LogWorldGroup)]
        public bool LogPipeline { get; set; } = false;

        /// <summary>Roads, tracks, pipes and wires.</summary>
        [SettingsUISection(LoggingTab, LogWorldGroup)]
        public bool LogNets { get; set; } = false;

        /// <summary>Placed objects: buildings, props and trees.</summary>
        [SettingsUISection(LoggingTab, LogWorldGroup)]
        public bool LogBuildings { get; set; } = false;

        /// <summary>Zoning, areas and districts, terrain, tile purchases.</summary>
        [SettingsUISection(LoggingTab, LogWorldGroup)]
        public bool LogLand { get; set; } = false;

        /// <summary>City-wide state: names, policies, money, milestones, the development tree.</summary>
        [SettingsUISection(LoggingTab, LogWorldGroup)]
        public bool LogCity { get; set; } = false;

        /// <summary>Transit lines, stops, vehicles and fares.</summary>
        [SettingsUISection(LoggingTab, LogWorldGroup)]
        public bool LogRoutes { get; set; } = false;

        [SettingsUISection(LoggingTab, LogEconomyGroup)]
        public bool LogResidential { get; set; } = false;

        [SettingsUISection(LoggingTab, LogEconomyGroup)]
        public bool LogCommercial { get; set; } = false;

        [SettingsUISection(LoggingTab, LogEconomyGroup)]
        public bool LogIndustrial { get; set; } = false;

        [SettingsUISection(LoggingTab, LogEconomyGroup)]
        public bool LogOffice { get; set; } = false;

        /// <summary>The other players: their cursors, markers, map pings and chat.</summary>
        [SettingsUISection(LoggingTab, LogClientGroup)]
        public bool LogPlayers { get; set; } = false;

        /// <summary>The mod's own screens: the main-menu button, the join dialog, the options page.</summary>
        [SettingsUISection(LoggingTab, LogClientGroup)]
        public bool LogUi { get; set; } = false;

        /// <summary>Mod load, system registration, and the compatibility and DLC checks.</summary>
        [SettingsUISection(LoggingTab, LogClientGroup)]
        public bool LogStartup { get; set; } = false;

        /// <summary>
        /// Frame times and the mod's own main-thread cost, reported every 30 s together with a
        /// per-zone split. Cheap enough to leave on: the measurement itself is two timestamp reads
        /// per pass, and it is the only thing that can tell the mod's cost apart from the city's.
        /// </summary>
        [SettingsUISection(LoggingTab, LogClientGroup)]
        public bool LogPerformance { get; set; } = false;

        /// <summary>
        /// Whether the player asked for detail about this topic. <see cref="VerboseLogging"/> is
        /// applied by the caller (<see cref="Game.Diagnostics.SyncLog.IsEnabled"/>), so this stays
        /// a plain per-topic answer.
        ///
        /// Unknown topics answer false: a topic added without a switch should be silent by
        /// default rather than quietly chatty in every player's log.
        /// </summary>
        public bool IsTopicEnabled(LogTopic topic)
        {
            switch (topic)
            {
                case LogTopic.Startup: return LogStartup;
                case LogTopic.Session: return LogSession;
                case LogTopic.Transport: return LogTransport;
                case LogTopic.WorldTransfer: return LogWorldTransfer;
                case LogTopic.Resync: return LogResync;
                case LogTopic.Pipeline: return LogPipeline;
                case LogTopic.Nets: return LogNets;
                case LogTopic.Buildings: return LogBuildings;
                case LogTopic.Land: return LogLand;
                case LogTopic.City: return LogCity;
                case LogTopic.Routes: return LogRoutes;
                case LogTopic.Residential: return LogResidential;
                case LogTopic.Commercial: return LogCommercial;
                case LogTopic.Industrial: return LogIndustrial;
                case LogTopic.Office: return LogOffice;
                case LogTopic.Players: return LogPlayers;
                case LogTopic.Ui: return LogUi;
                case LogTopic.Performance: return LogPerformance;
                default: return false;
            }
        }

        /// <summary>
        /// The partner markers are the only thing this mod draws every rendered frame, so they are
        /// the one part of it whose cost scales with screen resolution rather than with city size.
        /// </summary>
        [SettingsUISection(GeneralTab, GeneralGroup)]
        public bool ShowPartnerMarkers { get; set; } = true;

        /// <summary>
        /// Expert escape hatch for mod-specific compatibility checks. This permits other
        /// active mods locally and lets a host admit a different CS2 Multiplayer Mod build.
        /// Wire-protocol, game-version and DLC checks remain mandatory because bypassing
        /// those can make the peers unable to interpret one another's data at all.
        /// </summary>
        [SettingsUISection(GeneralTab, GeneralGroup)]
        [SettingsUIDisableByCondition(typeof(Setting), nameof(IsInSession))]
        public bool IgnoreModCompatibilityChecks { get; set; } = false;

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
            set { if (Mod.Service != null) Mod.Service.RequestDisconnect(); }
        }

        // ---- Host tab -----------------------------------------------------------
        // Setup stays editable in the main menu so the Host Game flow can use the
        // chosen values as soon as its selected city finishes loading. Only the
        // direct Host Session action still requires an already loaded city.

        /// <summary>
        /// Relay hosting needs no reachable port: Steam carries the traffic and players
        /// join with the code below. Direct hosting is the original path and still needs
        /// a forwarded port (or a LAN).
        /// </summary>
        [SettingsUIDropdown(typeof(Setting), nameof(GetHostConnectionValues))]
        [SettingsUISection(HostTab, HostSetupGroup)]
        [SettingsUIDisableByCondition(typeof(Setting), nameof(IsInSession))]
        [SettingsUIHideByCondition(typeof(Setting), nameof(RelayUnsupported))]
        public string HostConnection
        {
            // Reads as direct where there is no relay backend, so the options screen, the
            // UI bindings and HostTransport() all agree without each having to check.
            get { return RelayProvider.IsSupported ? _hostConnection : ConnectionDirect; }
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
            get { return _hostPort; }
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
            get { return _hostPassword; }
            set
            {
                if (IsInSession()) return;
                _hostPassword = value ?? "";
            }
        }

        [SettingsUIHideByCondition(typeof(Setting), nameof(IsRelayHosting))]
        [SettingsUISection(HostTab, HostSetupGroup)]
        public bool LanOnly { get; set; } = false;

        [SettingsUISection(HostTab, HostSetupGroup)]
        public bool RequireJoinApproval { get; set; } = true;

        [SettingsUITextInput]
        [SettingsUISection(HostTab, HostSetupGroup)]
        public string MaxPlayers { get; set; } = "8";

        [SettingsUITextInput]
        [SettingsUISection(HostTab, HostSetupGroup)]
        public string ResyncMinutes { get; set; } = "15";

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
        [SettingsUIHideByCondition(typeof(Setting), nameof(JoinIsRelay))]
        [SettingsUISection(JoinTab, JoinSetupGroup)]
        [SettingsUIDisableByCondition(typeof(Setting), nameof(IsInSession))]
        public string ServerAddress { get; set; } = "127.0.0.1";

        /// <summary>
        /// A join code addresses the host through the relay, so no address or port is
        /// involved. Chosen explicitly rather than guessed from what was typed: the
        /// joining player should see the same choice the host made.
        /// </summary>
        public bool JoinIsRelay()
        {
            return JoinConnection != ConnectionDirect;
        }

        public bool JoinIsDirect()
        {
            return !JoinIsRelay();
        }

        public TransportMode JoinTransport()
        {
            return JoinIsRelay() ? TransportMode.SteamRelay : TransportMode.Direct;
        }

        [SettingsUIDropdown(typeof(Setting), nameof(GetHostConnectionValues))]
        [SettingsUISection(JoinTab, JoinSetupGroup)]
        [SettingsUIDisableByCondition(typeof(Setting), nameof(IsInSession))]
        [SettingsUIHideByCondition(typeof(Setting), nameof(RelayUnsupported))]
        public string JoinConnection
        {
            get { return RelayProvider.IsSupported ? _joinConnection : ConnectionDirect; }
            set
            {
                if (IsInSession()) return;
                _joinConnection = value == ConnectionDirect ? ConnectionDirect : ConnectionRelay;
            }
        }

        /// <summary>
        /// The host's join code. Kept apart from <see cref="ServerAddress"/> so switching
        /// between relay and direct does not overwrite whichever one is not in use.
        /// </summary>
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

        public override void SetDefaults()
        {
            EnableMod = true;
            VerboseLogging = false;
            LogSession = false;
            LogTransport = false;
            LogWorldTransfer = false;
            LogResync = false;
            LogPipeline = false;
            LogNets = false;
            LogBuildings = false;
            LogLand = false;
            LogCity = false;
            LogRoutes = false;
            LogResidential = false;
            LogCommercial = false;
            LogIndustrial = false;
            LogOffice = false;
            LogPlayers = false;
            LogUi = false;
            LogStartup = false;
            LogPerformance = false;
            ShowPartnerMarkers = true;
            IgnoreModCompatibilityChecks = false;
            PlayerName = DefaultPlayerName;
            // Resetting the name asks for the default name again, which on a signed-in
            // copy of the game is the account name, not the literal "Player".
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
            MaxPlayers = "8";
            ResyncMinutes = "15";
        }
    }
}
