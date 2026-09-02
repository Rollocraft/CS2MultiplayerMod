using System;
using Colossal.IO.AssetDatabase;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Diagnostics;
using Game.SceneFlow;
using Game.UI.Menu;
using Unity.Entities;

namespace CS2MultiplayerMod.Game
{
    /// <summary>
    /// Puts the main-menu Multiplayer button back when the mod finishes loading after the
    /// main menu is already on screen. That is what a player gets on the launch that
    /// installs a mod update: the mod list resolves tens of seconds after the menu is up.
    ///
    /// The button is an extension of the game's menu button column, so it only becomes
    /// visible the next time that column renders - and an idle menu never renders again on
    /// its own. Re-announcing the mod's UI module location is the game's own path for a
    /// module that arrives while the interface is running: it re-runs every mod's
    /// registration and rebuilds the interface around the result.
    /// </summary>
    internal sealed class MenuUiRecovery
    {
        /// <summary>Module id in <c>UI/mod.json</c>, which is what the .mjs is registered under.</summary>
        private const string UiModuleId = "CS2MultiplayerMod";

        /// <summary>Grace after the module reports in before the button counts as missing.</summary>
        private const float SettleSeconds = 5f;

        /// <summary>
        /// Grace when the module never reported in at all. Longer, because a slow machine
        /// can take a while to reach the mod in the interface's module load chain.
        /// </summary>
        private const float MissingModuleSeconds = 45f;

        private const float RetrySeconds = 10f;

        /// <summary>
        /// The re-add has to reach the interface in a later frame than the removal: both
        /// edits hit the same location set, so a pair inside one frame arrives as a single
        /// unchanged value and the interface never re-reads the module.
        /// </summary>
        private const float ReAddSeconds = 0.5f;

        private const int MaxAttempts = 2;

        /// <summary>Frames to keep retrying the re-add before giving up on it.</summary>
        private const int MaxReAddFrames = 300;

        private string _couiPath;
        private bool _pathResolved;
        private int _attempts;
        private float _lastAttemptAt = float.NegativeInfinity;
        private bool _reAddPending;
        private float _reAddAt;
        private int _reAddFrames;
        private float _firstUpdateAt = float.NaN;

        public void Update(World world, bool moduleReady, float moduleReadyAt, bool buttonSeen)
        {
            float now = UnityEngine.Time.realtimeSinceStartup;

            // Nothing else may run while a removal is outstanding: leaving it that way
            // takes the mod's own interface down for the rest of the session.
            if (_reAddPending)
            {
                _reAddFrames++;
                if (now < _reAddAt && _reAddFrames < MaxReAddFrames) return;
                if (Announce(add: true) || _reAddFrames >= MaxReAddFrames) _reAddPending = false;
                return;
            }

            if (float.IsNaN(_firstUpdateAt)) _firstUpdateAt = now;
            if (buttonSeen || _attempts >= MaxAttempts) return;

            // Count from the module reporting in, or from this system's first frame when
            // it never did - a module whose registration never ran needs the same retry.
            float since = moduleReady ? moduleReadyAt : _firstUpdateAt;
            float grace = moduleReady ? SettleSeconds : MissingModuleSeconds;
            if (float.IsNaN(since) || now - since < grace) return;
            if (now - _lastAttemptAt < RetrySeconds) return;
            if (!MenuIsIdle(world)) return;

            // Only worth a retry when the game does know about the module: with no asset
            // there is nothing to re-announce and the .mjs is genuinely absent.
            if (CouiPath() == null) return;

            _lastAttemptAt = now;
            if (!Announce(add: false)) return;

            _attempts++;
            _reAddPending = true;
            _reAddAt = now + ReAddSeconds;
            _reAddFrames = 0;
            SyncLog.Warn(LogTopic.Ui,
                (moduleReady
                    ? "The main-menu Multiplayer button never reached the menu - the mod finished loading after the menu was drawn. "
                    : "The multiplayer UI module never registered. ") +
                "Rebuilding the menu interface (attempt " + _attempts + " of " + MaxAttempts + ").");
        }

        /// <summary>
        /// Only an idle main menu with no session running: the re-announce restarts every
        /// mod's UI registration, so it must not land on a screen a player is working in.
        /// </summary>
        private static bool MenuIsIdle(World world)
        {
            GameManager manager = GameManager.instance;
            if (manager == null || manager.gameMode != global::Game.GameMode.MainMenu) return false;
            if (manager.isGameLoading) return false;
            if (Mod.Service != null && Mod.Service.Session.Role != SessionRole.None) return false;
            if (world == null) return false;

            MenuUISystem menu = world.GetExistingSystemManaged<MenuUISystem>();
            return menu != null && menu.activeScreen == MenuUISystem.MenuScreen.Menu;
        }

        private string CouiPath()
        {
            if (_pathResolved) return _couiPath;
            _pathResolved = true;

            try
            {
                foreach (UIModuleAsset asset in AssetDatabase.global.GetAssets(default(SearchFilter<UIModuleAsset>)))
                {
                    if (asset == null || asset.moduleInfo.m_ModuleId != UiModuleId) continue;
                    _couiPath = asset.couiPath;
                    break;
                }
            }
            catch (Exception e)
            {
                SyncLog.Warn(LogTopic.Ui, "Could not look up the mod's UI module: " + e.Message);
            }

            if (_couiPath == null)
                SyncLog.Warn(LogTopic.Ui, "The mod's UI module is not registered with the game.");

            return _couiPath;
        }

        private bool Announce(bool add)
        {
            if (string.IsNullOrEmpty(_couiPath)) return false;

            GameManager manager = GameManager.instance;
            if (manager == null || manager.userInterface == null) return false;

            global::Game.UI.AppBindings bindings = manager.userInterface.appBindings;
            if (bindings == null) return false;

            string[] location = { _couiPath };
            try
            {
                if (add) bindings.AddActiveUIModLocation(location);
                else bindings.RemoveActiveUIModLocation(location);
            }
            catch (Exception e)
            {
                SyncLog.Warn(LogTopic.Ui, "UI module re-announce failed: " + e.Message);
                return false;
            }

            return true;
        }
    }
}
