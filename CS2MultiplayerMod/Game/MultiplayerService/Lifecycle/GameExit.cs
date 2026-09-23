using System;
using System.Threading.Tasks;
using Colossal.Serialization.Entities;
using Game;
using Game.SceneFlow;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Session;

namespace CS2MultiplayerMod.Game
{
    /// <summary>
    /// Leaving the shared city (quit, main menu, another world) ends the session here. Two signals: the
    /// pre-load callback (see <see cref="MultiplayerSystem"/>), and a per-frame lifecycle check, the
    /// only warning a process exit gives.
    /// </summary>
    public sealed partial class MultiplayerService
    {
        /// <summary>A world load we started ourselves stays "expected" for this long.</summary>
        private const long ExpectedWorldLoadWindowMs = 180000;
        private const int ClientMainMenuMaxAttempts = 5;
        private const long ClientMainMenuRetryDelayMs = 750;

        private bool _inCityWorld;
        private bool _leavingSession;
        private long _expectedWorldLoadMs = long.MinValue;
        private bool _clientHostWorldActive;
        private bool _clientMainMenuPending;
        private Task _clientMainMenuTask;
        private int _clientMainMenuAttempts;
        private long _clientMainMenuNextAttemptMs;
        private bool _clientMainMenuFailed;
        private string _clientExitNotice;
        private bool _transientCleanupPending;

        internal bool ClientExitNoticeActive => !string.IsNullOrEmpty(_clientExitNotice);
        internal bool ClientExitReturning => _clientMainMenuPending;
        internal bool ClientExitFailed => _clientMainMenuFailed;
        internal string ClientExitReason => _clientExitNotice ?? "";

        /// <summary>
        /// Claims the next world load as ours, so a joining client's city is not read as leaving. A window,
        /// since <see cref="JoinMapLoader"/> may fall back to a second load.
        /// </summary>
        internal void ExpectOwnWorldLoad() => _expectedWorldLoadMs = NowMs;

        private bool ConsumeExpectedWorldLoad()
        {
            bool expected = _expectedWorldLoadMs != long.MinValue &&
                            NowMs - _expectedWorldLoadMs < ExpectedWorldLoadWindowMs;
            _expectedWorldLoadMs = long.MinValue;
            return expected;
        }

        /// <summary>Any load other than our own incoming world takes this machine out of the session.</summary>
        internal void HandleWorldTransition(Purpose purpose, GameMode mode)
        {
            // Consume the claim first: a host world queued before the host vanished still belongs to the
            // dead session.
            if (mode.IsGame() && ConsumeExpectedWorldLoad())
            {
                _clientHostWorldActive = true;
                if (_session.Role == SessionRole.None)
                    QueueClientMainMenu("The host disconnected while its world was loading.");
                return;
            }

            // Clear the marker before Stop() publishes Offline, or a second main-menu load would compete.
            ForgetClientHostWorld();

            if (_session.Role == SessionRole.None)
            {
                _expectedWorldLoadMs = long.MinValue;
                return;
            }

            if (mode.IsGame())
            {
                LeaveSharedSession(
                    "Loading another world (" + purpose + ")",
                    "The host loaded a different city, so this session has ended.");
                return;
            }

            LeaveSharedSession(
                "Left the city for " + mode + " (" + purpose + ")",
                "The host returned to the main menu, so this session has ended.");
        }

        /// <summary>Per frame: shutdown, and a world transition that missed <see cref="HandleWorldTransition"/>.</summary>
        private void PumpGameExit()
        {
            GameManager manager;
            try { manager = GameManager.instance; }
            catch (Exception) { return; }
            if (manager == null) return;

            // Mirror the mode every frame; only a transition seen now counts as leaving, never a session
            // started from the menu.
            bool inCity = manager.gameMode.IsGame();
            bool leftCity = _inCityWorld && !inCity;
            _inCityWorld = inCity;

            if (_session.Role != SessionRole.None)
            {
                GameManager.State state = manager.state;
                if (state == GameManager.State.Quitting || state == GameManager.State.Terminated)
                {
                    LeaveSharedSession("The game is closing",
                        "The host closed the game, so this session has ended.");
                    return;
                }

                if (leftCity)
                {
                    // Backstop for the pre-load callback.
                    LeaveSharedSession("No longer in a city world (" + manager.gameMode + ")",
                        "The host left the city, so this session has ended.");
                    return;
                }
            }

            PumpClientMainMenu(manager);
            PumpTransientCleanup(manager);
        }

        /// <summary>The open city is the host's disposable copy; a disconnect must leave it.</summary>
        internal void MarkClientHostWorldActive() => _clientHostWorldActive = true;

        /// <summary>Queued: a disconnect inside the pump may race a host save that is still loading.</summary>
        private void QueueClientMainMenu(string reason)
        {
            if (!_clientHostWorldActive) return;

            _transientCleanupPending = true;
            if (string.IsNullOrEmpty(_clientExitNotice))
                _clientExitNotice = string.IsNullOrWhiteSpace(reason)
                    ? "The connection to the host closed."
                    : reason.Trim();
            if (_clientMainMenuPending) return;

            _clientMainMenuPending = true;
            _clientMainMenuAttempts = 0;
            _clientMainMenuNextAttemptMs = NowMs;
            _clientMainMenuFailed = false;
            _log.Event(LogTopic.Session, "Client session ended (" + reason +
                "); returning to the main menu.");
        }

        /// <summary>Stays until acknowledged, so the host's world never passes as a local save.</summary>
        internal void DismissClientExitNotice()
        {
            // The automatic close must finish first; the blocking screen offers a retry.
            if (_clientHostWorldActive || _clientMainMenuPending) return;
            ClearClientExitNotice();
            // This notice already showed the kick/ban reason; do not reveal the error overlay beneath it.
            _lastFault = null;
        }

        internal void RetryClientWorldExit()
        {
            if (!_clientHostWorldActive) return;

            _transientCleanupPending = true;
            _clientMainMenuPending = true;
            _clientMainMenuTask = null;
            _clientMainMenuAttempts = 0;
            _clientMainMenuNextAttemptMs = NowMs;
            _clientMainMenuFailed = false;
            _log.Detail(LogTopic.Session,
                "Retrying the return from the disconnected host world to the main menu.");
        }

        private void ClearClientExitNotice()
        {
            _clientExitNotice = null;
            _clientMainMenuFailed = false;
        }

        private void PumpClientMainMenu(GameManager manager)
        {
            if (!_clientMainMenuPending) return;

            GameManager.State state = manager.state;
            if (state == GameManager.State.Quitting || state == GameManager.State.Terminated)
            {
                // Shutdown is disposing the world; another load would compete.
                _clientMainMenuPending = false;
                _clientMainMenuTask = null;
                return;
            }

            if (!_clientHostWorldActive || !manager.gameMode.IsGame())
            {
                ForgetClientHostWorld();
                return;
            }

            // Let an in-flight load finish; the pump keeps running in UIUpdate.
            if (manager.isGameLoading) return;

            // A save and a main-menu load cannot share a world: finish the save.
            if (ClientWorldSaveInProgress) return;

            if (_clientMainMenuTask != null)
            {
                if (!_clientMainMenuTask.IsCompleted) return;

                string failure;
                if (_clientMainMenuTask.IsFaulted)
                {
                    Exception exception = _clientMainMenuTask.Exception != null
                        ? _clientMainMenuTask.Exception.GetBaseException()
                        : null;
                    failure = exception != null
                        ? exception.Message
                        : "the game reported an unknown load failure";
                }
                else if (_clientMainMenuTask.IsCanceled)
                    failure = "the game canceled the main-menu load";
                else
                    failure = "the main-menu request completed without leaving the world";

                _clientMainMenuTask = null;
                if (!manager.gameMode.IsGame())
                {
                    ForgetClientHostWorld();
                    return;
                }

                ScheduleClientMainMenuRetry(failure);
                return;
            }

            if (NowMs < _clientMainMenuNextAttemptMs) return;

            try
            {
                _clientMainMenuAttempts++;
                Task task = manager.MainMenu();

                // MainMenu changes gameMode before its first await; its preload callback usually clears this.
                if (_clientMainMenuPending && manager.gameMode.IsGame())
                {
                    if (task != null)
                        _clientMainMenuTask = task;
                    else
                        ScheduleClientMainMenuRetry("the game returned no main-menu load task");
                }
                else
                    ForgetClientHostWorld();
            }
            catch (Exception ex)
            {
                ScheduleClientMainMenuRetry(ex.Message);
            }
        }

        private void ScheduleClientMainMenuRetry(string failure)
        {
            _clientMainMenuTask = null;
            if (_clientMainMenuAttempts >= ClientMainMenuMaxAttempts)
            {
                // Keep ownership so PumpTransientCleanup does not delete the open world's save.
                _clientMainMenuPending = false;
                _clientMainMenuFailed = true;
                _log.Error(LogTopic.Session,
                    "Could not close the disconnected client's host world after " +
                    ClientMainMenuMaxAttempts + " attempts: " + failure);
                return;
            }

            _clientMainMenuNextAttemptMs = NowMs + ClientMainMenuRetryDelayMs;
            _log.Warn(LogTopic.Session,
                "Returning the disconnected client to the main menu failed (attempt " +
                _clientMainMenuAttempts + "/" + ClientMainMenuMaxAttempts + "): " + failure +
                ". Retrying.");
        }

        private void ForgetClientHostWorld()
        {
            _clientHostWorldActive = false;
            _clientMainMenuPending = false;
            _clientMainMenuTask = null;
            _clientMainMenuAttempts = 0;
            _clientMainMenuNextAttemptMs = 0;
            _clientMainMenuFailed = false;
        }

        /// <summary>The host notifies clients first, or they only see a dropped socket.</summary>
        private void LeaveSharedSession(string logReason, string hostNotice)
        {
            if (_leavingSession || _session.Role == SessionRole.None) return;
            _leavingSession = true;
            try
            {
                bool host = _session.Role == SessionRole.Host;
                _log.Event(LogTopic.Session, logReason + " - " +
                    (host ? "closing the session for every player." : "disconnecting from the host."));

                // Do not restore speed into a world being torn down.
                ResetWorldSyncState(restoreSpeed: false);
                _session.StopWithNotice(hostNotice);
                SetPhase(ClientWorldPhase.None);

                // The staged host world is dropped off the load path - see PumpTransientCleanup.
                _transientCleanupPending = true;
            }
            catch (Exception ex)
            {
                _log.Error(LogTopic.Session, "Failed to close the session while leaving the game.",
                    ex);
            }
            finally
            {
                _leavingSession = false;
                _expectedWorldLoadMs = long.MinValue;
            }
        }

        /// <summary>
        /// Deletes the client's host-world copy once idle; the transition callback runs inside the load
        /// pipeline, which is rebuilding the save index.
        /// </summary>
        private void PumpTransientCleanup(GameManager manager)
        {
            if (!_transientCleanupPending) return;
            if (_clientHostWorldActive || _clientMainMenuPending) return;
            if (manager.isGameLoading) return;
            _transientCleanupPending = false;
            JoinMapLoader.DeleteTransient(_log);
        }
    }
}
