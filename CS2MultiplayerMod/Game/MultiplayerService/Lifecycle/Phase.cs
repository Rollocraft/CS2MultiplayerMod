using System;
using Game.SceneFlow;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Networking;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Infrastructure;
using Unity.Entities;

namespace CS2MultiplayerMod.Game
{
    public sealed partial class MultiplayerService
    {
        private bool _disconnectConfirmationRequested;

        /// <summary>True while an explicit UI disconnect is waiting for confirmation.</summary>
        public bool DisconnectConfirmationRequested =>
            _disconnectConfirmationRequested && _session.Role != SessionRole.None;

        /// <summary>Chooses host-specific copy for the confirmation dialog.</summary>
        public bool DisconnectConfirmationIsHost =>
            DisconnectConfirmationRequested && _session.Role == SessionRole.Host;

        private int HandshakedPeerCount()
        {
            int peers = 0;
            foreach (var p in _session.Peers) if (p.Handshaked) peers++;
            return peers;
        }

        /// <summary>Called once per simulation tick by the ECS system.</summary>
        public void Update(World world)
        {
            _currentWorld = world;
            _session.Update(_clock.ElapsedMilliseconds);
            PumpClientWorldSave();
            PumpDeferredReceivedMap();
            RefreshPendingJoinsJson();
            RefreshPendingResyncsJson();
            if (_session.Status == SessionStatus.Connected &&
                SyncInbox.TryTakeResyncRequest(out ResyncReport queuedReport))
                RequestAutomaticWorldRecovery(queuedReport);
            PumpMaturedResyncReports();
            PumpMapReRequest();
            PumpWorldPhase();
            MaintainWorldSyncBarrier();
            PumpClientWorldSyncQuiescence();
            PumpGameExit();
        }

        /// <summary>
        /// LoadingMap -> InSession by the game's loading flag (no reliable load-completed callback): once
        /// our load was seen running and then stops, the world is in.
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
                SetPhase(ClientWorldPhase.WaitingForResume);
                _log.Detail(LogTopic.Session,
                    "Host world loaded - waiting for the epoch resume barrier.");
                _session.SendWorldSyncStage(_activeWorldSyncEpoch, WorldSyncStage.Loaded);
                return;
            }

            if (NowMs - _phaseChangedMs > MapLoadTimeoutMs)
            {
                // The load never started; recover instead of idling half-connected.
                SetPhase(ClientWorldPhase.WaitingForMap);
                if (_worldSyncBarrierActive && _activeWorldSyncEpoch > 0)
                    _session.SendWorldSyncStage(_activeWorldSyncEpoch, WorldSyncStage.Failed);
                _log.Warn(LogTopic.Session,
                    "Host world never started loading. Still connected - use /sync to " +
                    "request it again, or load '" + JoinMapLoader.TransientName + "' manually.");
            }
        }

        private void SetPhase(ClientWorldPhase phase)
        {
            if (_phase == phase) return;
            _phase = phase;
            _phaseChangedMs = NowMs;
            if (phase != ClientWorldPhase.LoadingMap) _sawLoading = false;
            _log.Detail(LogTopic.Session, "World phase: " + phase);

            // Autosaving the host's transient world piles copies into Saves and can collide with a resync
            // load (idea from CS2M).
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
                    _log.Detail(LogTopic.Session,
                        "Autosave paused while playing in the host's session; it is restored on disconnect.");
            }
            catch (Exception ex)
            {
                _log.Warn(LogTopic.Session, "Could not pause autosave: " + ex.Message);
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
                _log.Detail(LogTopic.Session, "Autosave restored.");
            }
            catch (Exception ex)
            {
                _log.Warn(LogTopic.Session,
                    "Could not restore autosave - re-enable it in the game options: " + ex.Message);
            }
        }

        /// <summary>
        /// Refuses while another mod is live and records the fault. Enforced here because the options
        /// screen and hub reach these entry points directly. True when the caller must stop.
        /// </summary>
        private bool RefuseForOtherMods(string action)
        {
            string detail = ModsCheck.FaultDetail();
            if (detail.Length == 0) return false;

            if (Mod.Setting != null && Mod.Setting.IgnoreModCompatibilityChecks)
            {
                _log.Warn(LogTopic.Session,
                    "Ignoring the other-mod compatibility check while trying to " + action +
                    " at the player's own risk: " + detail + ".");
                return false;
            }

            _lastFault = detail;
            _log.Warn(LogTopic.Session, "Cannot " + action + ": " + detail +
                ". Multiplayer runs only with CS2 Multiplayer Mod alone - disable the " +
                "others in the active playset and restart the game.");
            return true;
        }

        /// <summary>The full version, plus the compared release part when they differ.</summary>
        private static string ModVersionText(MultiplayerConfig config)
        {
            return " mod=" + Mod.Version +
                   (string.Equals(Mod.Version, config.ModVersion, StringComparison.Ordinal)
                       ? "" : " compat=" + config.ModVersion);
        }

        /// <summary>This machine's other mods, for the log; nothing about them crosses the wire.</summary>
        private static string LocalModsText(Setting settings)
        {
            bool bypassed = settings != null && settings.IgnoreModCompatibilityChecks;
            return " otherMods=" + ModsCheck.Summary() +
                   " modChecks=" + (bypassed ? "bypassed" : "enforced");
        }

        public void HostFromSettings(Setting settings)
        {
            if (!ModEnabled) { _log.Warn(LogTopic.Session, "Cannot host: the mod is disabled in settings."); return; }
            if (_session.Role != SessionRole.None) { _log.Warn(LogTopic.Session, "Cannot host: a session is already active."); return; }
            if (RefuseForOtherMods("host")) return;
            _disconnectConfirmationRequested = false;
            ClearClientExitNotice();
            ResetCommandDiagnostics();
            _lastFault = null;
            var config = BuildConfig(settings, hosting: true);
            _log.Event(LogTopic.Session, "Host requested: transport=" + config.Transport +
                (config.Transport == TransportMode.SteamRelay ? " joinCode=" + RelayProvider.LocalJoinCode : " port=" + config.Port) +
                " lanOnly=" + config.LanOnly + " password=" +
                (config.Password.Length > 0 ? "SET" : "NONE") + " maxPlayers=" + config.MaxPlayers +
                " approval=" + config.RequireJoinApproval +
                " autoApproveFriends=" + config.AutoApprovePlatformFriends +
                " clientResyncs=" + config.ClientResyncPolicy +
                " name='" + config.PlayerName + "'" + ModVersionText(config) + " game=" +
                config.GameVersion + " dlcs=[" + string.Join(", ", config.DlcList) + "]" +
                LocalModsText(settings));
            _session.StartHost(config);
        }

        public void JoinFromSettings(Setting settings)
        {
            if (!ModEnabled) { _log.Warn(LogTopic.Session, "Cannot join: the mod is disabled in settings."); return; }
            if (_session.Role != SessionRole.None) { _log.Warn(LogTopic.Session, "Cannot join: a session is already active."); return; }
            if (RefuseForOtherMods("join")) return;
            _disconnectConfirmationRequested = false;
            ClearClientExitNotice();
            ResetCommandDiagnostics();
            _lastFault = null;
            var config = BuildConfig(settings, hosting: false);
            _log.Event(LogTopic.Session, "Join requested: transport=" + config.Transport +
                " target=" +
                (config.Transport == TransportMode.SteamRelay ? config.JoinCode : config.HostAddress + ":" + config.Port) +
                " password=" + (config.Password.Length > 0 ? "SET" : "NONE") + " name='" +
                config.PlayerName + "'" + ModVersionText(config) + " game=" +
                config.GameVersion + " dlcs=[" + string.Join(", ", config.DlcList) + "]" +
                LocalModsText(settings));
            SetPhase(ClientWorldPhase.Connecting);
            _session.Join(config);
        }

        /// <summary>Asks the UI to confirm; automatic paths call <see cref="Disconnect"/> directly.</summary>
        public void RequestDisconnect()
        {
            if (_session.Role == SessionRole.None) return;
            if (_disconnectConfirmationRequested) return;
            _disconnectConfirmationRequested = true;
            _log.Detail(LogTopic.Session, "Waiting for confirmation before " +
                (_session.Role == SessionRole.Host ? "closing the hosted session." : "disconnecting from the session."));
        }

        public void CancelDisconnectRequest() => _disconnectConfirmationRequested = false;

        public void ConfirmDisconnect()
        {
            if (!DisconnectConfirmationRequested) return;
            _disconnectConfirmationRequested = false;
            Disconnect();
        }

        public void Disconnect()
        {
            _disconnectConfirmationRequested = false;
            if (_session.Role == SessionRole.Client && _clientHostWorldActive)
                QueueClientMainMenu("You disconnected from the multiplayer session.");

            ResetWorldSyncState(restoreSpeed: true);
            // Without the notice clients only see a socket drop.
            _session.StopWithNotice("The host ended this multiplayer session.");
            SetPhase(ClientWorldPhase.None);

            // Never delete a save that is loading or open; the lifecycle pump removes it later.
            if (_clientHostWorldActive || _clientMainMenuPending)
                _transientCleanupPending = true;
            else
                JoinMapLoader.DeleteTransient(_log);
        }

        /// <summary>The UI re-reads status on every mount, so a remembered fault would reappear.</summary>
        public void DismissFault() => _lastFault = null;

        public void Shutdown()
        {
            _disconnectConfirmationRequested = false;
            _settledReport = null;
            _mapReRequestPending = false;
            Diagnostics.ResyncArbiter.Reset();
            Sync.Infrastructure.RealizeGate.Reset();
            ResetWorldSyncState(restoreSpeed: false); // the world is going away with the process
            _session.StopWithNotice("The host closed the game, so this session has ended.");
            SetPhase(ClientWorldPhase.None);
            RestoreAutosave(); // even if the phase was already None
            ForgetClientHostWorld(); // process teardown owns the world; do not start MainMenu
            ClearClientExitNotice();
            JoinMapLoader.DeleteTransient(_log);
        }

        private MultiplayerConfig BuildConfig(Setting settings, bool hosting)
        {
            // Both sides pick their connection explicitly.
            TransportMode transport = hosting ? settings.HostTransport() : settings.JoinTransport();
            bool relay = transport == TransportMode.SteamRelay;
            string target = (settings.ServerAddress ?? "").Trim();
            string joinCode = (settings.JoinCodeInput ?? "").Trim();

            string portText = hosting ? settings.HostPort : settings.JoinPort;
            if (!int.TryParse((portText ?? "").Trim(), out int port) || port <= 0 || port > 65535)
            {
                // Never silently host on another port. Relay sessions have no port.
                if (!relay)
                    _log.Warn(LogTopic.Session, "Invalid " + (hosting ? "host" : "join") + " port '" +
                        portText + "' - using default " + DefaultPort +
                        " instead. Enter a number from 1 to 65535.");
                port = DefaultPort;
            }

            if (!int.TryParse((settings.MaxPlayers ?? "").Trim(), out int maxPlayers) || maxPlayers < 2 ||
                maxPlayers > 32)
            {
                if (hosting)
                    _log.Warn(LogTopic.Session, "Invalid max players '" + settings.MaxPlayers +
                        "' - using default " + DefaultMaxPlayers + " instead (allowed: 2-32).");
                maxPlayers = DefaultMaxPlayers;
            }

            // The release part (see Mod.CompatibilityVersion); host and join lines print both.
            string modVersion = Mod.CompatibilityVersion;
            string gameVersion;
            try { gameVersion = UnityEngine.Application.version; }
            catch (Exception) { gameVersion = ""; }

            string[] dlcs = DlcCheck.OwnedSyncRelevantDlcs(_log);
            Diagnostics.FlightRecorder.RecordLoadedContent(dlcs);

            // Encryption stays off: the game's Mono runtime cannot create the TLS certificate. The password
            // challenge-response never sends the password.
            return new MultiplayerConfig(
                settings.PlayerName, target, port,
                hosting ? settings.HostPassword : settings.JoinPassword,
                // A relay session is not network-reachable; LAN-only has nothing to restrict.
                lanOnly: !relay && settings.LanOnly,
                useEncryption: false, maxPlayers: maxPlayers,
                modVersion: modVersion, gameVersion: gameVersion,
                dlcList: dlcs,
                requireJoinApproval: hosting && settings.RequireJoinApproval,
                transport: transport,
                joinCode: relay && !hosting ? joinCode : "",
                ignoreModCompatibilityChecks: settings.IgnoreModCompatibilityChecks,
                simulationSync: settings.SimulationSync,
                autoApprovePlatformFriends: hosting && relay && settings.AutoApproveSteamFriends,
                clientResyncPolicy: hosting
                    ? settings.SelectedClientResyncPolicy()
                    : Core.Session.ClientResyncPolicy.Allow);
        }
    }
}
