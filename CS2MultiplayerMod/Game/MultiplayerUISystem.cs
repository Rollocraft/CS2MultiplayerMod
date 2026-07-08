using Colossal.UI.Binding;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Localization;
using Game.UI;

namespace CS2MultiplayerMod.Game
{
    /// <summary>
    /// C# side of the main-menu "Join Game" dialog (UI module in <c>UI/</c>).
    /// Exposes the start-screen fields under binding group "cs2mp", backed directly
    /// by the mod's <see cref="Setting"/>. Player name is shared with Options; join
    /// fields stay as Setting-backed dialog state. The join/disconnect triggers reuse
    /// the same service entry points as the options-screen buttons.
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
                Mod.log.Info("UI module loaded and registered - the main-menu Join Game button is available.");
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
            AddUpdateBinding(new GetterValueBinding<int>(Group, "mapTransferPercent",
                () => Mod.Service != null ? Mod.Service.MapTransferPercent : -1));
            AddUpdateBinding(new GetterValueBinding<int>(Group, "worldSendPercent",
                () => Mod.Service != null ? Mod.Service.WorldSendPercent : -1));
            AddUpdateBinding(new GetterValueBinding<bool>(Group, "inSession",
                () => Mod.Service != null && Mod.Service.Session.Role != SessionRole.None));

            // Untested game-version warning: localized sentence when the running build
            // is not in GameVersionCheck.TestedVersions, otherwise "" (banner hidden).
            AddUpdateBinding(new GetterValueBinding<string>(Group, "versionWarning",
                () => GameVersionCheck.WarningText()));

            // One-time disclaimer gate: the UI shows it before the first host/join and
            // only flips this once the player accepts. Persisted in Setting so it never
            // reappears for that user.
            AddUpdateBinding(new GetterValueBinding<bool>(Group, "disclaimerAccepted",
                () => Mod.Setting != null && Mod.Setting.DisclaimerAccepted));
            AddBinding(new TriggerBinding(Group, "acceptDisclaimer", () =>
            {
                if (Mod.Setting == null || Mod.Setting.DisclaimerAccepted) return;
                Mod.Setting.DisclaimerAccepted = true;
                Mod.Setting.ApplyAndSave();
            }));

            // -- In-game hub panel (right-menu button above the Chirper) ----------

            // Serialized once per append on the C# side; the binding only pushes
            // when the cached string instance changes.
            AddUpdateBinding(new GetterValueBinding<string>(Group, "chatLog",
                () => Mod.Service != null ? Mod.Service.ChatLogJson : "[]"));
            AddUpdateBinding(new GetterValueBinding<bool>(Group, "isHost",
                () => Mod.Service != null && Mod.Service.Session.Role == SessionRole.Host));
            AddUpdateBinding(new GetterValueBinding<int>(Group, "playerCount",
                () => Mod.Service != null ? Mod.Service.PlayerCount : 0));
            // Hosting shares the loaded city, so it needs one — and no running session.
            AddUpdateBinding(new GetterValueBinding<bool>(Group, "canHost",
                () => Mod.Setting != null && !Mod.Setting.CannotStartHost() && MultiplayerService.ModEnabled));

            AddUpdateBinding(new GetterValueBinding<string>(Group, "hostPort",
                () => Mod.Setting != null ? Mod.Setting.HostPort : "25001"));
            AddUpdateBinding(new GetterValueBinding<string>(Group, "hostPassword",
                () => Mod.Setting != null ? Mod.Setting.HostPassword : ""));
            AddUpdateBinding(new GetterValueBinding<string>(Group, "maxPlayers",
                () => Mod.Setting != null ? Mod.Setting.MaxPlayers : "8"));
            AddUpdateBinding(new GetterValueBinding<bool>(Group, "lanOnly",
                () => Mod.Setting != null && Mod.Setting.LanOnly));
            AddUpdateBinding(new GetterValueBinding<string>(Group, "resyncMinutes",
                () => Mod.Setting != null ? Mod.Setting.ResyncMinutes : "15"));

            // Host setup edits. HostPort/HostPassword setters already refuse changes
            // mid-session inside Setting, so no extra guarding here.
            AddBinding(new TriggerBinding<string>(Group, "setHostPort",
                value => { if (Mod.Setting != null) Mod.Setting.HostPort = value; }));
            AddBinding(new TriggerBinding<string>(Group, "setHostPassword",
                value => { if (Mod.Setting != null) Mod.Setting.HostPassword = value; }));
            AddBinding(new TriggerBinding<string>(Group, "setMaxPlayers",
                value => { if (Mod.Setting != null) Mod.Setting.MaxPlayers = value; }));
            AddBinding(new TriggerBinding<bool>(Group, "setLanOnly",
                value => { if (Mod.Setting != null) Mod.Setting.LanOnly = value; }));
            AddBinding(new TriggerBinding<string>(Group, "setResyncMinutes",
                value => { if (Mod.Setting != null) Mod.Setting.ResyncMinutes = value; }));

            AddBinding(new TriggerBinding<string>(Group, "sendChat",
                value => { if (Mod.Service != null) Mod.Service.SendChatFromUi(value); }));
            AddBinding(new TriggerBinding(Group, "hostStart", () =>
            {
                if (Mod.Service == null || Mod.Setting == null) return;
                Mod.Setting.ApplyAndSave();
                Mod.Service.HostFromSettings(Mod.Setting);
            }));
            AddBinding(new TriggerBinding(Group, "syncNow", () =>
            {
                if (Mod.Service != null) Mod.Service.RequestWorldSync();
            }));

            // Field edits: written straight into Setting (persisted on Join).
            AddBinding(new TriggerBinding<string>(Group, "setPlayerName",
                value => { if (Mod.Setting != null) Mod.Setting.PlayerName = value; }));
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

            Mod.log.Info(nameof(MultiplayerUISystem) + " created (binding group '" + Group + "').");
        }

        protected override void OnUpdate()
        {
            base.OnUpdate();

            if (_uiModuleReady || _uiModuleWarned) return;
            if (UnityEngine.Time.realtimeSinceStartup - _createdAt < UiReadyGraceSeconds) return;

            _uiModuleWarned = true;
            Mod.log.Warn(
                "The Join Game UI module never reported in - the main-menu button is most likely missing. " +
                "Either CS2MultiplayerMod.mjs is not in the mod folder, or another mod's broken UI module " +
                "(known offender: Gooee) crashed the game's UI-module load chain before it reached this mod. " +
                "Check the game's UI log for JS errors from other mods and remove the broken mod. " +
                "Joining still works without the button via Options > CS2 Multiplayer Mod > Join Game.");
        }
    }
}
