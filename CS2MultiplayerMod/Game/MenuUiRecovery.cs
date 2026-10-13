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
    /// Restores the main-menu Multiplayer button when the mod loads after the menu is drawn (usually the
    /// launch that installs an update). An idle menu never re-renders its button column, so the UI module
    /// location is re-announced, which re-runs every mod's registration.
    /// </summary>
    internal sealed class MenuUiRecovery
    {
        /// <summary>Module id in <c>UI/mod.json</c>, which is what the .mjs is registered under.</summary>
        private const string UiModuleId = "CS2MultiplayerMod";

        /// <summary>Grace after the module reports in before the button counts as missing.</summary>
        private const float SettleSeconds = 5f;

        /// <summary>Grace when the module never reported in; slow machines reach it late.</summary>
        private const float MissingModuleSeconds = 45f;

        private const float RetrySeconds = 10f;

        /// <summary>
        /// The re-add must land a frame after the removal, or the two cancel into an unchanged value.
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

            // An outstanding removal would take the mod's interface down for the session.
            if (_reAddPending)
            {
                _reAddFrames++;
                if (now < _reAddAt && _reAddFrames < MaxReAddFrames) return;
                if (Announce(add: true) || _reAddFrames >= MaxReAddFrames) _reAddPending = false;
                return;
            }

            if (float.IsNaN(_firstUpdateAt)) _firstUpdateAt = now;
            if (buttonSeen || _attempts >= MaxAttempts) return;

            // From the module reporting in, or from our first frame if it never did.
            float since = moduleReady ? moduleReadyAt : _firstUpdateAt;
            float grace = moduleReady ? SettleSeconds : MissingModuleSeconds;
            if (float.IsNaN(since) || now - since < grace) return;
            if (now - _lastAttemptAt < RetrySeconds) return;
            if (!MenuIsIdle(world)) return;

            // No asset means the .mjs is genuinely absent; nothing to re-announce.
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

        /// <summary>Idle main menu, no session: the re-announce restarts every mod's UI registration.</summary>
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
