using Colossal.Serialization.Entities;
using Colossal.UI.Binding;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Networking;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Localization;
using Game;
using Game.SceneFlow;
using Game.UI;
using Game.UI.Menu;
using System.IO;

namespace CS2MultiplayerMod.Game
{
    /// <summary>
    /// The main-menu multiplayer screen's bindings (group "cs2mp"), backed by <see cref="Setting"/>.
    /// Host-world actions go through the game's New/Load Game screens and start the server once the
    /// world is ready.
    /// </summary>
    public partial class MultiplayerUISystem : UISystemBase
    {
        private const string Group = "cs2mp";

        /// <summary>Wait for "uiReady" before warning; slow machines load mod UI modules minutes late.</summary>
        private const float UiReadyGraceSeconds = 120f;

        // Per process: the UI module registers once per run, not per world.
        private static bool s_UiModuleReady;
        private static float s_UiModuleReadyAt = float.NaN;
        private static bool s_MenuButtonSeen;

        private static readonly MenuUiRecovery Recovery = new MenuUiRecovery();

        private float _createdAt;
        private bool _uiModuleWarned;
        private bool _hostAfterWorldLoad;
        private bool _hostWorldLoadStarted;
        private ValueBinding<bool> _multiplayerMenuActiveBinding;

        private static bool IsUiBundleInstalled()
        {
            try
            {
                string assemblyDirectory = Path.GetDirectoryName(typeof(MultiplayerUISystem).Assembly.Location);
                return !string.IsNullOrEmpty(assemblyDirectory) &&
                       File.Exists(Path.Combine(assemblyDirectory, "CS2MultiplayerMod.mjs"));
            }
            catch
            {
                return false;
            }
        }

        protected override void OnCreate()
        {
            base.OnCreate();

            _createdAt = UnityEngine.Time.realtimeSinceStartup;

            // Second chance: the platform may sign in after the mod loads.
            if (Mod.Setting != null) Mod.Setting.ApplyPlatformNamePreset();

            // Fired by the module's register(): proves the .mjs survived the sequential UI-module chain, which
            // another mod's broken module can abort.
            AddBinding(new TriggerBinding(Group, "uiReady", () =>
            {
                if (s_UiModuleReady) return;
                s_UiModuleReady = true;
                s_UiModuleReadyAt = UnityEngine.Time.realtimeSinceStartup;
                SyncLog.Detail(LogTopic.Ui, "UI module loaded and registered.");
            }));

            // Sent when the button mounts; registering the append does not mean it is on screen (see
            // MenuUiRecovery).
            AddBinding(new TriggerBinding(Group, "menuButtonMounted", () =>
            {
                if (s_MenuButtonSeen) return;
                s_MenuButtonSeen = true;
                SyncLog.Detail(LogTopic.Ui, "Main-menu Multiplayer button is on screen.");
            }));

            // Field values: polled from Setting every UI frame, pushed on change.
            AddUpdateBinding(new GetterValueBinding<string>(Group, "playerName",
                () => Mod.Setting != null ? Mod.Setting.PlayerName : "Player"));
            AddUpdateBinding(new GetterValueBinding<string>(Group, "joinAddress",
                () => Mod.Setting != null ? Mod.Setting.ServerAddress : "127.0.0.1"));
            AddUpdateBinding(new GetterValueBinding<string>(Group, "joinPort",
                () => Mod.Setting != null ? Mod.Setting.JoinPort : "25001"));
            AddUpdateBinding(new GetterValueBinding<string>(Group, "joinPassword",
                () => Mod.Setting != null ? Mod.Setting.JoinPassword : ""));

            AddUpdateBinding(new GetterValueBinding<string>(Group, "statusKind",
                () => Mod.Service != null ? Mod.Service.UiStatusKind : "offline"));
            AddUpdateBinding(new GetterValueBinding<string>(Group, "statusTitle",
                () => Mod.Service != null ? Mod.Service.UiStatusTitle : L10n.T(L10n.Key.StatusOffline)));
            AddUpdateBinding(new GetterValueBinding<string>(Group, "statusDetail",
                () => Mod.Service != null ? Mod.Service.UiStatusDetail : ""));
            AddUpdateBinding(new GetterValueBinding<string>(Group, "statusHelp",
                () => Mod.Service != null ? Mod.Service.UiStatusHelp : ""));
            AddUpdateBinding(new GetterValueBinding<string>(Group, "statusHelpPage",
                () => Mod.Service != null ? Mod.Service.UiStatusHelpPage : ""));
            AddUpdateBinding(new GetterValueBinding<string>(Group, "progressMode",
                () => Mod.Service != null ? Mod.Service.UiProgressMode : "none"));
            AddUpdateBinding(new GetterValueBinding<int>(Group, "mapTransferPercent",
                () => Mod.Service != null ? Mod.Service.MapTransferPercent : -1));
            AddUpdateBinding(new GetterValueBinding<int>(Group, "worldSendPercent",
                () => Mod.Service != null ? Mod.Service.WorldSendPercent : -1));
            AddUpdateBinding(new GetterValueBinding<bool>(Group, "inSession",
                () => Mod.Service != null && Mod.Service.Session.Role != SessionRole.None));
            // Menu and Game hooks can coexist during a world swap; one surface owns the blocking overlay.
            AddUpdateBinding(new GetterValueBinding<bool>(Group, "inGameWorld",
                () => GameManager.instance != null && GameManager.instance.gameMode.IsGame()));
            AddUpdateBinding(new GetterValueBinding<bool>(Group, "canSaveClientWorld",
                () => Mod.Service != null && Mod.Service.CanSaveClientWorld));
            AddUpdateBinding(new GetterValueBinding<string>(Group, "clientWorldSaveStatus",
                () => Mod.Service != null ? Mod.Service.ClientWorldSaveStatus : "idle"));
            AddUpdateBinding(new GetterValueBinding<string>(Group, "clientWorldSaveName",
                () => Mod.Service != null ? Mod.Service.ClientWorldSaveName : ""));
            AddUpdateBinding(new GetterValueBinding<bool>(Group, "clientExitNoticeActive",
                () => Mod.Service != null && Mod.Service.ClientExitNoticeActive));
            AddUpdateBinding(new GetterValueBinding<bool>(Group, "clientExitReturning",
                () => Mod.Service != null && Mod.Service.ClientExitReturning));
            AddUpdateBinding(new GetterValueBinding<bool>(Group, "clientExitFailed",
                () => Mod.Service != null && Mod.Service.ClientExitFailed));
            AddUpdateBinding(new GetterValueBinding<string>(Group, "clientExitReason",
                () => Mod.Service != null ? Mod.Service.ClientExitReason : ""));
            AddUpdateBinding(new GetterValueBinding<bool>(Group, "disconnectConfirmationRequested",
                () => Mod.Service != null && Mod.Service.DisconnectConfirmationRequested));
            AddUpdateBinding(new GetterValueBinding<bool>(Group, "disconnectConfirmationIsHost",
                () => Mod.Service != null && Mod.Service.DisconnectConfirmationIsHost));

            // Untested game version: localized sentence, or "" (hidden).
            AddUpdateBinding(new GetterValueBinding<string>(Group, "versionWarning",
                () => GameVersionCheck.WarningText()));

            // Other live mods: localized sentence, or "" (hidden); the ignored flag decides block vs warning.
            AddUpdateBinding(new GetterValueBinding<string>(Group, "modsBlocked",
                () => ModsCheck.BlockText(Mod.Setting != null &&
                                           Mod.Setting.IgnoreModCompatibilityChecks)));
            AddUpdateBinding(new GetterValueBinding<bool>(Group, "modsCheckIgnored",
                () => Mod.Setting != null && Mod.Setting.IgnoreModCompatibilityChecks));

            // One-time disclaimer, persisted once accepted.
            AddUpdateBinding(new GetterValueBinding<bool>(Group, "disclaimerAccepted",
                () => Mod.Setting != null && Mod.Setting.DisclaimerAccepted));
            AddBinding(_multiplayerMenuActiveBinding =
                new ValueBinding<bool>(Group, "multiplayerMenuActive", false));
            AddBinding(new TriggerBinding(Group, "acceptDisclaimer", () =>
            {
                if (Mod.Setting == null || Mod.Setting.DisclaimerAccepted) return;
                Mod.Setting.DisclaimerAccepted = true;
                Mod.Setting.ApplyAndSave();
            }));
            AddBinding(new TriggerBinding(Group, "openMultiplayerScreen", OpenMultiplayerMenuScreen));
            AddBinding(new TriggerBinding(Group, "multiplayerScreenExited",
                () => _multiplayerMenuActiveBinding.Update(false)));

            // -- In-game hub panel (right-menu button above the Chirper) ----------

            // Serialized once per append; the binding pushes only on a new instance.
            AddUpdateBinding(new GetterValueBinding<string>(Group, "chatLog",
                () => Mod.Service != null ? Mod.Service.ChatLogJson : "[]"));
            AddUpdateBinding(new GetterValueBinding<bool>(Group, "isHost",
                () => Mod.Service != null && Mod.Service.Session.Role == SessionRole.Host));
            AddUpdateBinding(new GetterValueBinding<int>(Group, "playerCount",
                () => Mod.Service != null ? Mod.Service.PlayerCount : 0));
            AddUpdateBinding(new GetterValueBinding<string>(Group, "playerList",
                () => Mod.Service != null ? Mod.Service.PlayerListJson : "[]"));
            // Joins waiting for the host's approval (empty on a client / when approval is off).
            AddUpdateBinding(new GetterValueBinding<string>(Group, "pendingJoins",
                () => Mod.Service != null ? Mod.Service.PendingJoinsJson : "[]"));
            AddUpdateBinding(new GetterValueBinding<string>(Group, "pendingResyncs",
                () => Mod.Service != null ? Mod.Service.PendingResyncsJson : "[]"));
            // Hosting needs a loaded city and no session; CannotStartHost also owns the other-mod gate.
            AddUpdateBinding(new GetterValueBinding<bool>(Group, "canHost",
                () => Mod.Setting != null && !Mod.Setting.CannotStartHost() && MultiplayerService.ModEnabled));

            // "relay" (default) or "direct"; a relay host hands out a join code instead of an address.
            AddUpdateBinding(new GetterValueBinding<string>(Group, "hostConnection",
                () => Mod.Setting != null ? Mod.Setting.HostConnection : Setting.ConnectionRelay));
            AddUpdateBinding(new GetterValueBinding<string>(Group, "joinCode",
                () => RelayProvider.LocalJoinCode));
            AddUpdateBinding(new GetterValueBinding<bool>(Group, "relayAvailable",
                () => RelayProvider.IsAvailable));
            // False without Steam (Microsoft Store / Game Pass): the connection picker is hidden.
            AddUpdateBinding(new GetterValueBinding<bool>(Group, "relaySupported",
                () => RelayProvider.IsSupported));
            // The running session's transport, not the configured one.
            AddUpdateBinding(new GetterValueBinding<bool>(Group, "sessionUsesRelay",
                () => Mod.Service != null && Mod.Service.Session.UsesRelay));
            // Empty while the relay is usable, otherwise why it is not.
            AddUpdateBinding(new GetterValueBinding<string>(Group, "relayUnavailableReason",
                () => RelayProvider.IsAvailable ? "" : RelayProvider.UnavailableReason ?? ""));
            // The joining player's own choice, and the code they will dial with.
            AddUpdateBinding(new GetterValueBinding<string>(Group, "joinConnection",
                () => Mod.Setting != null ? Mod.Setting.JoinConnection : Setting.ConnectionRelay));
            AddUpdateBinding(new GetterValueBinding<string>(Group, "joinCodeInput",
                () => Mod.Setting != null ? Mod.Setting.JoinCodeInput : ""));

            AddUpdateBinding(new GetterValueBinding<string>(Group, "hostPort",
                () => Mod.Setting != null ? Mod.Setting.HostPort : "25001"));
            AddUpdateBinding(new GetterValueBinding<string>(Group, "hostPassword",
                () => Mod.Setting != null ? Mod.Setting.HostPassword : ""));
            AddUpdateBinding(new GetterValueBinding<string>(Group, "maxPlayers",
                () => Mod.Setting != null ? Mod.Setting.MaxPlayers : "8"));
            AddUpdateBinding(new GetterValueBinding<bool>(Group, "lanOnly",
                () => Mod.Setting != null && Mod.Setting.LanOnly));
            AddUpdateBinding(new GetterValueBinding<bool>(Group, "requireApproval",
                () => Mod.Setting == null || Mod.Setting.RequireJoinApproval));
            AddUpdateBinding(new GetterValueBinding<bool>(Group, "autoApproveSteamFriends",
                () => Mod.Setting != null && Mod.Setting.AutoApproveSteamFriends));
            AddUpdateBinding(new GetterValueBinding<string>(Group, "resyncPolicy",
                () => Mod.Setting != null ? Mod.Setting.ResyncPolicy : Setting.ResyncAllow));
            // The session's answer once running; neither a client's setting nor a mid-session edit changes it.
            AddUpdateBinding(new GetterValueBinding<bool>(Group, "simulationSync",
                () => Mod.Service != null && Mod.Service.Session.Role != SessionRole.None
                    ? Mod.Service.SimulationSyncEnabled
                    : Mod.Setting == null || Mod.Setting.SimulationSync));

            // Setting already refuses port/password changes mid-session.
            AddBinding(new TriggerBinding<string>(Group, "setHostConnection",
                value =>
                {
                    if (Mod.Setting == null) return;
                    Mod.Setting.HostConnection = value;
                    // Persist now: the host flow reads it back only after the chosen world loads.
                    Mod.Setting.ApplyAndSave();
                }));
            AddBinding(new TriggerBinding<string>(Group, "setHostPort",
                value => { if (Mod.Setting != null) Mod.Setting.HostPort = value; }));
            AddBinding(new TriggerBinding<string>(Group, "setHostPassword",
                value => { if (Mod.Setting != null) Mod.Setting.HostPassword = value; }));
            AddBinding(new TriggerBinding<string>(Group, "setMaxPlayers",
                value => { if (Mod.Setting != null) Mod.Setting.MaxPlayers = value; }));
            AddBinding(new TriggerBinding<bool>(Group, "setLanOnly",
                value => { if (Mod.Setting != null) Mod.Setting.LanOnly = value; }));
            AddBinding(new TriggerBinding<bool>(Group, "setRequireApproval",
                value =>
                {
                    if (Mod.Setting != null && !Mod.Setting.IsInSession())
                        Mod.Setting.RequireJoinApproval = value;
                }));
            AddBinding(new TriggerBinding<bool>(Group, "setAutoApproveSteamFriends",
                value =>
                {
                    if (Mod.Setting != null && !Mod.Setting.IsInSession())
                        Mod.Setting.AutoApproveSteamFriends = value;
                }));
            AddBinding(new TriggerBinding<string>(Group, "setResyncPolicy",
                value =>
                {
                    if (Mod.Setting == null || Mod.Setting.IsInSession()) return;
                    Mod.Setting.ResyncPolicy = value == Setting.ResyncApproval ||
                        value == Setting.ResyncHostOnly ? value : Setting.ResyncAllow;
                }));
            AddBinding(new TriggerBinding<bool>(Group, "setSimulationSync",
                value =>
                {
                    if (Mod.Setting != null && !Mod.Setting.IsInSession())
                        Mod.Setting.SimulationSync = value;
                }));

            AddBinding(new TriggerBinding<string>(Group, "sendChat",
                value => { if (Mod.Service != null) Mod.Service.SendChatFromUi(value); }));
            AddBinding(new TriggerBinding<int>(Group, "kickPlayer",
                playerId => { if (Mod.Service != null) Mod.Service.KickPlayerFromUi(playerId); }));
            AddBinding(new TriggerBinding<int>(Group, "banPlayer",
                playerId => { if (Mod.Service != null) Mod.Service.BanPlayerFromUi(playerId); }));
            AddBinding(new TriggerBinding<int>(Group, "approveJoin",
                playerId => { if (Mod.Service != null) Mod.Service.ApproveJoinFromUi(playerId); }));
            AddBinding(new TriggerBinding<int>(Group, "declineJoin",
                playerId => { if (Mod.Service != null) Mod.Service.DeclineJoinFromUi(playerId); }));
            AddBinding(new TriggerBinding<int>(Group, "approveResync",
                playerId => { if (Mod.Service != null) Mod.Service.ApproveResyncFromUi(playerId); }));
            AddBinding(new TriggerBinding<int>(Group, "declineResync",
                playerId => { if (Mod.Service != null) Mod.Service.DeclineResyncFromUi(playerId); }));
            AddBinding(new TriggerBinding(Group, "hostStart", StartHostFromSettings));
            AddBinding(new TriggerBinding(Group, "hostLoadWorld", () =>
                OpenHostWorldScreen(MenuUISystem.MenuScreen.LoadGame)));
            AddBinding(new TriggerBinding(Group, "hostCreateWorld", () =>
                OpenHostWorldScreen(MenuUISystem.MenuScreen.NewGame)));
            AddBinding(new TriggerBinding(Group, "syncNow", () =>
            {
                if (Mod.Service != null) Mod.Service.RequestWorldSync();
            }));
            AddBinding(new TriggerBinding<string>(Group, "saveClientWorld",
                value => { if (Mod.Service != null) Mod.Service.SaveClientWorldFromUi(value); }));
            AddBinding(new TriggerBinding(Group, "resetClientWorldSaveStatus", () =>
            {
                if (Mod.Service != null) Mod.Service.ResetClientWorldSaveStatusFromUi();
            }));

            // Field edits: written straight into Setting (persisted on Join).
            AddBinding(new TriggerBinding<string>(Group, "setPlayerName",
                value => { if (Mod.Setting != null) Mod.Setting.PlayerName = value; }));
            AddBinding(new TriggerBinding<string>(Group, "setJoinConnection",
                value => { if (Mod.Setting != null) Mod.Setting.JoinConnection = value; }));
            AddBinding(new TriggerBinding<string>(Group, "setJoinCodeInput",
                value => { if (Mod.Setting != null) Mod.Setting.JoinCodeInput = value; }));
            AddBinding(new TriggerBinding<string>(Group, "setJoinAddress",
                value => { if (Mod.Setting != null) Mod.Setting.ServerAddress = value; }));
            AddBinding(new TriggerBinding<string>(Group, "setJoinPort",
                value => { if (Mod.Setting != null) Mod.Setting.JoinPort = value; }));
            AddBinding(new TriggerBinding<string>(Group, "setJoinPassword",
                value => { if (Mod.Setting != null) Mod.Setting.JoinPassword = value; }));

            AddBinding(new TriggerBinding(Group, "join", () =>
            {
                if (Mod.Service == null || Mod.Setting == null) return;
                Mod.Setting.ApplyAndSave();
                Mod.Service.JoinFromSettings(Mod.Setting);
            }));
            AddBinding(new TriggerBinding(Group, "disconnect", () =>
            {
                if (Mod.Service != null) Mod.Service.Disconnect();
            }));
            AddBinding(new TriggerBinding(Group, "requestDisconnect", () =>
            {
                if (Mod.Service != null) Mod.Service.RequestDisconnect();
            }));
            AddBinding(new TriggerBinding(Group, "confirmDisconnect", () =>
            {
                if (Mod.Service != null) Mod.Service.ConfirmDisconnect();
            }));
            AddBinding(new TriggerBinding(Group, "cancelDisconnect", () =>
            {
                if (Mod.Service != null) Mod.Service.CancelDisconnectRequest();
            }));
            AddBinding(new TriggerBinding<string>(Group, "openHelp", HelpLinks.Open));
            // Sent alongside "disconnect" when the player closes an error screen.
            AddBinding(new TriggerBinding(Group, "dismissStatusFault", () =>
            {
                if (Mod.Service != null) Mod.Service.DismissFault();
            }));
            AddBinding(new TriggerBinding(Group, "dismissClientExitNotice", () =>
            {
                if (Mod.Service != null) Mod.Service.DismissClientExitNotice();
            }));
            AddBinding(new TriggerBinding(Group, "retryClientWorldExit", () =>
            {
                if (Mod.Service != null) Mod.Service.RetryClientWorldExit();
            }));

            SyncLog.Detail(LogTopic.Ui, nameof(MultiplayerUISystem) + " created (binding group '" +
                Group + "').");
        }

        /// <summary>
        /// Uses the native Credits screen slot, so the flow shares focus, Back and transitions with built-in
        /// screens.
        /// </summary>
        private void OpenMultiplayerMenuScreen()
        {
            MenuUISystem menu = World.GetExistingSystemManaged<MenuUISystem>();
            if (menu == null)
            {
                SyncLog.Error(LogTopic.Ui, "Could not open the multiplayer menu screen.");
                return;
            }

            _multiplayerMenuActiveBinding.Update(true);
            menu.activeScreen = MenuUISystem.MenuScreen.Credits;
        }

        /// <summary>
        /// The next world picked in the native menu becomes a multiplayer host; backing out to the menu
        /// clears the intent.
        /// </summary>
        private void OpenHostWorldScreen(MenuUISystem.MenuScreen screen)
        {
            if (Mod.Service == null || Mod.Setting == null) return;
            if (!MultiplayerService.ModEnabled)
            {
                SyncLog.Warn(LogTopic.Ui,
                    "Cannot choose a host world: the mod is disabled in settings.");
                return;
            }
            if (Mod.Service.Session.Role != SessionRole.None)
            {
                SyncLog.Warn(LogTopic.Ui,
                    "Cannot choose a host world: a multiplayer session is already active.");
                return;
            }

            MenuUISystem menu = World.GetExistingSystemManaged<MenuUISystem>();
            if (menu == null)
            {
                SyncLog.Error(LogTopic.Ui, "Could not open the game's world-selection screen.");
                return;
            }

            _hostAfterWorldLoad = true;
            _hostWorldLoadStarted = false;
            menu.activeScreen = screen;
            SyncLog.Detail(LogTopic.Ui, "Host world selection opened through the game's " + screen +
                " screen.");
        }

        private void CancelPendingHost()
        {
            if (!_hostAfterWorldLoad) return;

            _hostAfterWorldLoad = false;
            _hostWorldLoadStarted = false;
            SyncLog.Detail(LogTopic.Ui, "Host world selection cancelled.");
        }

        private void StartHostFromSettings()
        {
            if (Mod.Service == null || Mod.Setting == null) return;
            Mod.Setting.ApplyAndSave();
            Mod.Service.HostFromSettings(Mod.Setting);
        }

        protected override void OnGamePreload(Purpose purpose, global::Game.GameMode mode)
        {
            base.OnGamePreload(purpose, mode);

            if (!_hostAfterWorldLoad || !mode.IsGame()) return;
            if (purpose != Purpose.NewGame && purpose != Purpose.LoadGame) return;

            _hostWorldLoadStarted = true;
            SyncLog.Detail(LogTopic.Ui, "Selected host world is loading (" + purpose + ").");
        }

        protected override void OnUpdate()
        {
            base.OnUpdate();

            // Native screens return to Menu on Back; watching that cancels the intent without wrapping them.
            if (_hostAfterWorldLoad && !_hostWorldLoadStarted)
            {
                GameManager manager = GameManager.instance;
                if (manager != null && manager.isGameLoading && manager.gameMode.IsGame())
                {
                    // Backstop for the preload callback.
                    _hostWorldLoadStarted = true;
                    SyncLog.Detail(LogTopic.Ui,
                        "Selected host world entered the game load pipeline.");
                }
                else
                {
                    MenuUISystem menu = World.GetExistingSystemManaged<MenuUISystem>();
                    if (menu != null && menu.activeScreen == MenuUISystem.MenuScreen.Menu)
                        CancelPendingHost();
                }
            }

            if (_hostAfterWorldLoad && _hostWorldLoadStarted)
            {
                GameManager manager = GameManager.instance;
                if (manager != null &&
                    manager.state == GameManager.State.WorldReady &&
                    !manager.isGameLoading)
                {
                    if (manager.gameMode.IsGame())
                    {
                        _hostAfterWorldLoad = false;
                        _hostWorldLoadStarted = false;
                        SyncLog.Detail(LogTopic.Ui,
                            "Host world is ready - starting the multiplayer session.");
                        StartHostFromSettings();
                    }
                    else
                    {
                        // A failed load can return to the menu after preload fired; drop the intent.
                        CancelPendingHost();
                    }
                }
            }

            // The mod can load after the menu is drawn, and the menu column never re-reads our append itself.
            Recovery.Update(World, s_UiModuleReady, s_UiModuleReadyAt, s_MenuButtonSeen);

            if (s_UiModuleReady || _uiModuleWarned) return;
            if (UnityEngine.Time.realtimeSinceStartup - _createdAt < UiReadyGraceSeconds) return;

            _uiModuleWarned = true;
            if (!IsUiBundleInstalled())
            {
                SyncLog.Warn(LogTopic.Ui,
                    "The multiplayer UI module never reported in because CS2MultiplayerMod.mjs is missing " +
                    "beside the mod DLL. The installed mod package is incomplete; reinstall or update it. " +
                    "Joining still works via Options > CS2 Multiplayer Mod > Join Game.");
            }
            else
            {
                SyncLog.Warn(LogTopic.Ui,
                    "The multiplayer UI module never reported in even though CS2MultiplayerMod.mjs is installed. " +
                    "Another UI module may have crashed the game's module-load chain before it reached this mod. " +
                    "Check the game's UI log for the first JavaScript error. Joining still works via " +
                    "Options > CS2 Multiplayer Mod > Join Game.");
            }
        }
    }
}
