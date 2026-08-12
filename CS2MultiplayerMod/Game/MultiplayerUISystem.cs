using Colossal.Serialization.Entities;
using Colossal.UI.Binding;
using CS2MultiplayerMod.Core.Networking;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Localization;
using Game;
using Game.SceneFlow;
using Game.UI;
using Game.UI.Menu;

namespace CS2MultiplayerMod.Game
{
    /// <summary>
    /// C# side of the main-menu multiplayer screen (UI module in <c>UI/</c>).
    /// Exposes the start-screen fields under binding group "cs2mp", backed directly
    /// by the mod's <see cref="Setting"/>. Player name is shared with Options; join
    /// fields stay as Setting-backed dialog state. Host-world actions hand off to the
    /// game's own New Game / Load Game screens and start the server once that world is
    /// fully ready.
    ///
    /// Declared <c>partial</c> because Unity's Entities source generators extend
    /// system types.
    /// </summary>
    public partial class MultiplayerUISystem : UISystemBase
    {
        private const string Group = "cs2mp";

        /// <summary>
        /// How long after system creation we wait for the UI module's "uiReady"
        /// trigger before warning. Generous because on slow machines the game UI
        /// loads mod modules well over a minute after the C# mods are up.
        /// </summary>
        private const float UiReadyGraceSeconds = 120f;

        private float _createdAt;
        private bool _uiModuleReady;
        private bool _uiModuleWarned;
        private bool _hostAfterWorldLoad;
        private bool _hostWorldLoadStarted;
        private ValueBinding<bool> _multiplayerMenuActiveBinding;

        protected override void OnCreate()
        {
            base.OnCreate();

            _createdAt = UnityEngine.Time.realtimeSinceStartup;

            // Fired once from the UI module's register() — proves the .mjs made it
            // through the game's sequential UI-module load chain. A broken module
            // from another mod (e.g. Gooee) can abort that chain, in which case
            // this trigger never arrives and OnUpdate logs a diagnosis.
            AddBinding(new TriggerBinding(Group, "uiReady", () =>
            {
                if (_uiModuleReady) return;
                _uiModuleReady = true;
                Mod.log.Info("UI module loaded and registered - the main-menu Multiplayer button is available.");
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
            AddUpdateBinding(new GetterValueBinding<string>(Group, "progressMode",
                () => Mod.Service != null ? Mod.Service.UiProgressMode : "none"));
            AddUpdateBinding(new GetterValueBinding<int>(Group, "mapTransferPercent",
                () => Mod.Service != null ? Mod.Service.MapTransferPercent : -1));
            AddUpdateBinding(new GetterValueBinding<int>(Group, "worldSendPercent",
                () => Mod.Service != null ? Mod.Service.WorldSendPercent : -1));
            AddUpdateBinding(new GetterValueBinding<bool>(Group, "inSession",
                () => Mod.Service != null && Mod.Service.Session.Role != SessionRole.None));
            // UI append hooks for Menu and Game can briefly coexist while the game swaps
            // worlds. Give the connection overlay one authoritative surface so only one
            // blocking screen owns focus during that hand-off.
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

            // Untested game-version warning: localized sentence when the running build
            // is not in GameVersionCheck.TestedVersions, otherwise "" (banner hidden).
            AddUpdateBinding(new GetterValueBinding<string>(Group, "versionWarning",
                () => GameVersionCheck.WarningText()));

            // One-time disclaimer gate: the UI shows it before the first host/join and
            // only flips this once the player accepts. Persisted in Setting so it never
            // reappears for that user.
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

            // Serialized once per append on the C# side; the binding only pushes
            // when the cached string instance changes.
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
            // Hosting shares the loaded city, so it needs one — and no running session.
            AddUpdateBinding(new GetterValueBinding<bool>(Group, "canHost",
                () => Mod.Setting != null && !Mod.Setting.CannotStartHost() && MultiplayerService.ModEnabled));

            // Connection mode: "relay" (default) or "direct". The join code is what a
            // relay host hands out instead of an address and port.
            AddUpdateBinding(new GetterValueBinding<string>(Group, "hostConnection",
                () => Mod.Setting != null ? Mod.Setting.HostConnection : Setting.ConnectionRelay));
            AddUpdateBinding(new GetterValueBinding<string>(Group, "joinCode",
                () => RelayProvider.LocalJoinCode));
            AddUpdateBinding(new GetterValueBinding<bool>(Group, "relayAvailable",
                () => RelayProvider.IsAvailable));
            // Whether the relay exists as a choice at all. False on copies of the game
            // without Steam (Microsoft Store / Game Pass), where the screens drop the
            // connection picker entirely instead of offering a mode that cannot run.
            AddUpdateBinding(new GetterValueBinding<bool>(Group, "relaySupported",
                () => RelayProvider.IsSupported));
            // What the running session actually uses, as opposed to what is configured
            // for the next one.
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
            AddUpdateBinding(new GetterValueBinding<string>(Group, "resyncMinutes",
                () => Mod.Setting != null ? Mod.Setting.ResyncMinutes : "15"));

            // Host setup edits. HostPort/HostPassword setters already refuse changes
            // mid-session inside Setting, so no extra guarding here.
            AddBinding(new TriggerBinding<string>(Group, "setHostConnection",
                value =>
                {
                    if (Mod.Setting == null) return;
                    Mod.Setting.HostConnection = value;
                    // Persist immediately: the host flow leaves this screen for the game's
                    // world picker and only reads the setting back once that world is up.
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
                value => { if (Mod.Setting != null) Mod.Setting.RequireJoinApproval = value; }));
            AddBinding(new TriggerBinding<string>(Group, "setResyncMinutes",
                value => { if (Mod.Setting != null) Mod.Setting.ResyncMinutes = value; }));

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
            AddBinding(new TriggerBinding(Group, "dismissClientExitNotice", () =>
            {
                if (Mod.Service != null) Mod.Service.DismissClientExitNotice();
            }));
            AddBinding(new TriggerBinding(Group, "retryClientWorldExit", () =>
            {
                if (Mod.Service != null) Mod.Service.RetryClientWorldExit();
            }));

            Mod.log.Info(nameof(MultiplayerUISystem) + " created (binding group '" + Group + "').");
        }

        /// <summary>
        /// Use the native Credits screen slot while the multiplayer flow is active.
        /// Its UI component is extended by the mod, so it participates in the same
        /// focus, Back action and transition coordinator as every built-in menu screen.
        /// </summary>
        private void OpenMultiplayerMenuScreen()
        {
            MenuUISystem menu = World.GetExistingSystemManaged<MenuUISystem>();
            if (menu == null)
            {
                Mod.log.Error("Could not open the multiplayer menu screen.");
                return;
            }

            _multiplayerMenuActiveBinding.Update(true);
            menu.activeScreen = MenuUISystem.MenuScreen.Credits;
        }

        /// <summary>
        /// Remember that the next world selected through the native menu is meant to
        /// become a multiplayer host, then open that menu screen. The intent is cleared
        /// if the player backs out to the main menu.
        /// </summary>
        private void OpenHostWorldScreen(MenuUISystem.MenuScreen screen)
        {
            if (Mod.Service == null || Mod.Setting == null) return;
            if (!MultiplayerService.ModEnabled)
            {
                Mod.log.Warn("Cannot choose a host world: the mod is disabled in settings.");
                return;
            }
            if (Mod.Service.Session.Role != SessionRole.None)
            {
                Mod.log.Warn("Cannot choose a host world: a multiplayer session is already active.");
                return;
            }

            MenuUISystem menu = World.GetExistingSystemManaged<MenuUISystem>();
            if (menu == null)
            {
                Mod.log.Error("Could not open the game's world-selection screen.");
                return;
            }

            _hostAfterWorldLoad = true;
            _hostWorldLoadStarted = false;
            menu.activeScreen = screen;
            Mod.log.Info("Host world selection opened through the game's " + screen + " screen.");
        }

        private void CancelPendingHost()
        {
            if (!_hostAfterWorldLoad) return;

            _hostAfterWorldLoad = false;
            _hostWorldLoadStarted = false;
            Mod.log.Info("Host world selection cancelled.");
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
            Mod.log.Info("Selected host world is loading (" + purpose + ").");
        }

        protected override void OnUpdate()
        {
            base.OnUpdate();

            // The native screens return to Menu when Back is pressed. Watching the
            // screen state here cancels the intent without wrapping or replacing any
            // of the game's UI components.
            if (_hostAfterWorldLoad && !_hostWorldLoadStarted)
            {
                GameManager manager = GameManager.instance;
                if (manager != null && manager.isGameLoading && manager.gameMode.IsGame())
                {
                    // Backstop for the preload callback: UIUpdate normally observes at
                    // least one loading frame as the selected city enters the game.
                    _hostWorldLoadStarted = true;
                    Mod.log.Info("Selected host world entered the game load pipeline.");
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
                        Mod.log.Info("Host world is ready - starting the multiplayer session.");
                        StartHostFromSettings();
                    }
                    else
                    {
                        // A failed/cancelled load can return to a ready main menu after
                        // preload already fired. Do not let that intent affect a later game.
                        CancelPendingHost();
                    }
                }
            }

            if (_uiModuleReady || _uiModuleWarned) return;
            if (UnityEngine.Time.realtimeSinceStartup - _createdAt < UiReadyGraceSeconds) return;

            _uiModuleWarned = true;
            Mod.log.Warn(
                "The multiplayer UI module never reported in - the main-menu button is most likely missing. " +
                "Either CS2MultiplayerMod.mjs is not in the mod folder, or another mod's broken UI module " +
                "(known offender: Gooee) crashed the game's UI-module load chain before it reached this mod. " +
                "Check the game's UI log for JS errors from other mods and remove the broken mod. " +
                "Joining still works without the button via Options > CS2 Multiplayer Mod > Join Game.");
        }
    }
}
