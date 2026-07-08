using System;
using Game.SceneFlow;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Localization;

namespace CS2MultiplayerMod.Game
{
    public sealed partial class MultiplayerService
    {
        private int HandshakedPeerCount()
        {
            int peers = 0;
            foreach (var p in _session.Peers) if (p.Handshaked) peers++;
            return peers;
        }

        private static string PhaseText(ClientWorldPhase phase)
        {
            switch (phase)
            {
                case ClientWorldPhase.Connecting: return L10n.T(L10n.Key.StateConnecting);
                case ClientWorldPhase.WaitingForMap: return L10n.T(L10n.Key.PhaseWaitingForMap);
                case ClientWorldPhase.LoadingMap: return L10n.T(L10n.Key.PhaseLoadingMap);
                default: return phase.ToString();
            }
        }

        /// <summary>Called once per simulation tick by the ECS system.</summary>
        public void Update()
        {
            _session.Update(_clock.ElapsedMilliseconds);
            PumpWorldPhase();
        }

        /// <summary>
        /// Drive the LoadingMap -> InSession transition by watching the game's own
        /// loading flag (there is no reliable public load-completed callback): once the
        /// load we kicked off has been observed running and then stops, the world is in.
        /// </summary>
        private void PumpWorldPhase()
        {
            if (_session.Role != SessionRole.Client || _phase != ClientWorldPhase.LoadingMap) return;

            GameManager manager = GameManager.instance;
            if (manager != null && manager.isGameLoading)
            {
                _sawLoading = true;
                return;
            }

            if (_sawLoading)
            {
                SetPhase(ClientWorldPhase.InSession);
                _log.Info("[MP] Host world loaded - gameplay sync active.");
                return;
            }

            if (NowMs - _phaseChangedMs > MapLoadTimeoutMs)
            {
                // The load never started (failed staging, asset index miss, …). Recover
                // to a defined state instead of idling half-connected forever.
                SetPhase(ClientWorldPhase.WaitingForMap);
                _log.Warn("[MP] Host world never started loading. Still connected - use /sync to " +
                          "request it again, or load '" + JoinMapLoader.TransientName + "' manually.");
            }
        }

        private void SetPhase(ClientWorldPhase phase)
        {
            if (_phase == phase) return;
            _phase = phase;
            _phaseChangedMs = NowMs;
            if (phase != ClientWorldPhase.LoadingMap) _sawLoading = false;
            _log.Info("[MP] World phase: " + phase);

            // A joined client plays in the host's (transient) world: autosaving it would
            // pile copies of the host's city into the local Saves folder and can collide
            // with a resync load mid-write (idea from CS2M's save handling).
            if (phase == ClientWorldPhase.InSession) SuppressAutosave();
            else if (phase == ClientWorldPhase.None) RestoreAutosave();
        }

        private void SuppressAutosave()
        {
            if (_autosaveSuppressed) return;
            try
            {
                var general = GameManager.instance.settings.general;
                _autosaveWasEnabled = general.autoSave;
                if (_autosaveWasEnabled) general.autoSave = false;
                _autosaveSuppressed = true;
                if (_autosaveWasEnabled)
                    _log.Info("[MP] Autosave paused while playing in the host's session; it is restored on disconnect.");
            }
            catch (Exception ex)
            {
                _log.Warn("[MP] Could not pause autosave: " + ex.Message);
            }
        }

        private void RestoreAutosave()
        {
            if (!_autosaveSuppressed) return;
            _autosaveSuppressed = false;
            if (!_autosaveWasEnabled) return;
            try
            {
                GameManager.instance.settings.general.autoSave = true;
                _log.Info("[MP] Autosave restored.");
            }
            catch (Exception ex)
            {
                _log.Warn("[MP] Could not restore autosave - re-enable it in the game options: " + ex.Message);
            }
        }

        public void HostFromSettings(Setting settings)
        {
            if (!ModEnabled) { _log.Warn("Cannot host: the mod is disabled in settings."); return; }
            if (_session.Role != SessionRole.None) { _log.Warn("Cannot host: a session is already active."); return; }
            _lastFault = null;
            var config = BuildConfig(settings, hosting: true);
            _log.Info("[MP] Host requested: port=" + config.Port +
                      " lanOnly=" + config.LanOnly +
                      " password=" + (config.Password.Length > 0 ? "SET" : "NONE") +
                      " maxPlayers=" + config.MaxPlayers +
                      " name='" + config.PlayerName + "'" +
                      " mod=" + config.ModVersion + " game=" + config.GameVersion +
                      " dlcs=" + config.DlcList.Length);
            _session.StartHost(config);
        }

        public void JoinFromSettings(Setting settings)
        {
            if (!ModEnabled) { _log.Warn("Cannot join: the mod is disabled in settings."); return; }
            if (_session.Role != SessionRole.None) { _log.Warn("Cannot join: a session is already active."); return; }
            _lastFault = null;
            var config = BuildConfig(settings, hosting: false);
            _log.Info("[MP] Join requested: target=" + config.HostAddress + ":" + config.Port +
                      " password=" + (config.Password.Length > 0 ? "SET" : "NONE") +
                      " name='" + config.PlayerName + "'" +
                      " mod=" + config.ModVersion + " game=" + config.GameVersion +
                      " dlcs=" + config.DlcList.Length);
            SetPhase(ClientWorldPhase.Connecting);
            _session.Join(config);
        }

        public void Disconnect()
        {
            _session.Stop();
            SetPhase(ClientWorldPhase.None);
            JoinMapLoader.DeleteTransient(_log); // a joining client keeps no copy of the host world
        }

        public void Shutdown()
        {
            _session.Stop();
            SetPhase(ClientWorldPhase.None);
            RestoreAutosave(); // even if the phase was already None
            JoinMapLoader.DeleteTransient(_log);
        }

        private MultiplayerConfig BuildConfig(Setting settings, bool hosting)
        {
            string portText = hosting ? settings.HostPort : settings.JoinPort;
            int port;
            if (!int.TryParse((portText ?? "").Trim(), out port) || port <= 0 || port > 65535)
            {
                // Never fall back silently: hosting on a different port than the user
                // thinks they configured is exactly the kind of failure nobody can debug.
                _log.Warn("[MP] Invalid " + (hosting ? "host" : "join") + " port '" + portText +
                          "' - using default " + DefaultPort + " instead. Enter a number from 1 to 65535.");
                port = DefaultPort;
            }

            int maxPlayers;
            if (!int.TryParse((settings.MaxPlayers ?? "").Trim(), out maxPlayers) || maxPlayers < 2 || maxPlayers > 32)
            {
                if (hosting)
                    _log.Warn("[MP] Invalid max players '" + settings.MaxPlayers +
                              "' - using default " + DefaultMaxPlayers + " instead (allowed: 2-32).");
                maxPlayers = DefaultMaxPlayers;
            }

            string modVersion = typeof(Mod).Assembly.GetName().Version.ToString();
            string gameVersion;
            try { gameVersion = UnityEngine.Application.version; }
            catch (Exception) { gameVersion = ""; }

            // Encryption is permanently off in-game: the game's Mono runtime cannot
            // create the TLS certificate (CertificateRequest is missing and the attempt
            // crashed the host silently). Authentication is unaffected - the password
            // challenge-response never sends the password itself.
            return new MultiplayerConfig(
                settings.PlayerName, (settings.ServerAddress ?? "").Trim(), port,
                hosting ? settings.HostPassword : settings.JoinPassword,
                settings.LanOnly, useEncryption: false, maxPlayers: maxPlayers,
                modVersion: modVersion, gameVersion: gameVersion,
                dlcList: DlcCheck.OwnedSyncRelevantDlcs(_log));
        }

    }
}
