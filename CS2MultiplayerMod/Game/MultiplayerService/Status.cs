using System;
using CS2MultiplayerMod.Core.Networking;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Localization;

namespace CS2MultiplayerMod.Game
{
    public sealed partial class MultiplayerService
    {
        public string StatusRoleText
        {
            get
            {
                if (!ModEnabled) return L10n.T(L10n.Key.StatusDisabled);
                switch (_session.Role)
                {
                    case SessionRole.Host: return L10n.T(L10n.Key.RoleHost);
                    case SessionRole.Client: return L10n.T(L10n.Key.RoleClient);
                    default: return L10n.T(L10n.Key.StatusOffline);
                }
            }
        }

        public string StatusStateText
        {
            get
            {
                if (!ModEnabled) return L10n.T(L10n.Key.StatusDisabled);
                if (_session.Role == SessionRole.None)
                    return string.IsNullOrEmpty(_lastFault)
                        ? L10n.T(L10n.Key.StatusOffline)
                        : L10n.F(L10n.Key.OfflineFault, FriendlyFaultSummary(_lastFault));
                switch (_session.Status)
                {
                    case SessionStatus.Connecting: return L10n.T(L10n.Key.StateConnecting);
                    case SessionStatus.Connected: return L10n.T(L10n.Key.StateConnected);
                    case SessionStatus.Faulted: return L10n.T(L10n.Key.StateFaulted);
                    default: return L10n.T(L10n.Key.StatusOffline);
                }
            }
        }

        public string StatusPlayersText
        {
            get
            {
                if (_session.Role == SessionRole.None) return L10n.T(L10n.Key.PlayersNone);
                int peers = HandshakedPeerCount();
                return _session.Role == SessionRole.Host
                    ? L10n.F(L10n.Key.PlayersClients, peers)
                    : (_session.Status == SessionStatus.Connected
                        ? L10n.T(L10n.Key.ConnectedToHost)
                        : L10n.T(L10n.Key.PlayersNone));
            }
        }

        public string StatusAccessText
        {
            get
            {
                if (_session.Role == SessionRole.None) return L10n.T(L10n.Key.NoSession);
                return L10n.T(_session.PasswordProtected ? L10n.Key.AccessPassword : L10n.Key.AccessOpen);
            }
        }

        public string StatusExposureText
        {
            get
            {
                // A relay session has no exposure to describe: nothing on this machine is
                // reachable, so the useful line is the code players need instead.
                if (_session.UsesRelay && _session.Role == SessionRole.Host)
                    return L10n.F(L10n.Key.ExposureRelay, RelayProvider.LocalJoinCode);
                if (_session.UsesRelay && _session.Role == SessionRole.Client)
                    return L10n.T(L10n.Key.ExposureRelayClient);
                if (_session.Role == SessionRole.Host)
                {
                    if (!_session.PublicExposure) return L10n.T(L10n.Key.ExposureLan);

                    // A public host's real question is not "am I public" but "can anyone
                    // actually reach me", which is what the router just answered.
                    switch (_session.PortForwardStatus)
                    {
                        case Core.Networking.PortForwardState.Working:
                            return L10n.T(L10n.Key.ExposureForwarding);
                        case Core.Networking.PortForwardState.Open:
                            return _session.PortForwardAddress != null
                                ? L10n.F(L10n.Key.ExposureForwardedAt,
                                         _session.PortForwardAddress + ":" + _session.Port)
                                : L10n.T(L10n.Key.ExposureForwarded);
                        case Core.Networking.PortForwardState.NoRouter:
                        case Core.Networking.PortForwardState.Refused:
                            return L10n.F(L10n.Key.ExposureForwardManually, _session.Port);
                    }
                    return L10n.T(L10n.Key.ExposureInternet);
                }
                if (_session.Role == SessionRole.Client) return L10n.T(L10n.Key.ConnectedToHost);
                return L10n.T(L10n.Key.NoSession);
            }
        }

        public string StatusWorldText
        {
            get
            {
                if (_session.Role == SessionRole.None) return L10n.T(L10n.Key.WorldNone);
                if (_worldSyncBarrierActive)
                {
                    if (_session.Role == SessionRole.Host) return HostWorldSyncTitle();
                    if (MapTransferPercent >= 0)
                        return L10n.F(L10n.Key.WorldMapProgress, MapTransferPercent);
                    return PhaseText(_phase);
                }
                if (_session.Role == SessionRole.Host) return L10n.T(L10n.Key.WorldHosting);
                if (_session.IncomingBlobChannel == MapChannel && _session.IncomingBlobTotal > 0)
                    return L10n.F(L10n.Key.WorldMapProgress,
                        (int)(100L * _session.IncomingBlobReceived / _session.IncomingBlobTotal));
                return _phase == ClientWorldPhase.InSession
                    ? L10n.T(L10n.Key.WorldLoaded)
                    : PhaseText(_phase);
            }
        }

        public int MapTransferPercent
        {
            get
            {
                if (_session.IncomingBlobChannel != MapChannel ||
                    _session.IncomingBlobTotal <= 0) return -1;
                long percent = 100L * _session.IncomingBlobReceived / _session.IncomingBlobTotal;
                if (percent < 0) return 0;
                if (percent > 100) return 100;
                return (int)percent;
            }
        }

        /// <summary>
        /// Host-side world-send progress (0-100), or -1 when no world is streaming out.
        /// </summary>
        public int WorldSendPercent
        {
            get
            {
                if (_session.OutgoingBlobTotal <= 0 ||
                    (!_session.OutgoingBlobActive && !_worldSyncBarrierActive)) return -1;
                long percent = 100L * _session.OutgoingBlobSent / _session.OutgoingBlobTotal;
                if (percent < 0) return 0;
                if (percent > 100) return 100;
                return (int)percent;
            }
        }

        /// <summary>
        /// Coarse status bucket for UI accents:
        /// disabled, offline, connecting, syncing, connected, or error.
        /// </summary>
        public string UiStatusKind
        {
            get
            {
                if (!ModEnabled) return "disabled";
                if (_session.Role == SessionRole.None)
                    return string.IsNullOrEmpty(_lastFault) ? "offline" : "error";
                if (_session.Status == SessionStatus.Faulted) return "error";
                if (_worldSyncBarrierActive) return "syncing";
                if (_session.Status == SessionStatus.Connecting ||
                    (_session.Role == SessionRole.Client &&
                     _phase != ClientWorldPhase.InSession))
                    return "connecting";
                return "connected";
            }
        }

        /// <summary>Short, contextual headline for every multiplayer UI.</summary>
        public string UiStatusTitle
        {
            get
            {
                if (!ModEnabled) return L10n.T(L10n.Key.TitleModDisabled);
                if (_session.Role == SessionRole.None)
                    return L10n.T(string.IsNullOrEmpty(_lastFault)
                        ? L10n.Key.StatusOffline
                        : L10n.Key.TitleConnectionFailed);
                if (_session.Status == SessionStatus.Faulted)
                    return L10n.T(L10n.Key.TitleConnectionFailed);
                if (_session.Role == SessionRole.Client && _session.AwaitingHostApproval)
                    return L10n.T(L10n.Key.TitleAwaitingApproval);
                if (_session.Status == SessionStatus.Connecting)
                    return L10n.T(L10n.Key.StateConnecting);
                if (_session.Role == SessionRole.Host && _worldSyncBarrierActive)
                    return HostWorldSyncTitle();
                if (_session.Role == SessionRole.Client)
                {
                    switch (_phase)
                    {
                        case ClientWorldPhase.Connecting:
                            return L10n.T(L10n.Key.StateConnecting);
                        case ClientWorldPhase.WaitingForMap:
                            return L10n.T(L10n.Key.PhaseWaitingForMap);
                        case ClientWorldPhase.LoadingMap:
                            return L10n.T(L10n.Key.PhaseLoadingMap);
                        case ClientWorldPhase.WaitingForResume:
                            return L10n.T(L10n.Key.PhaseFinishingSetup);
                    }
                }
                if (_worldSyncBarrierActive)
                    return L10n.T(L10n.Key.PhaseSynchronizing);
                return L10n.T(_session.Role == SessionRole.Host
                    ? L10n.Key.TitleHosting
                    : L10n.Key.StateConnected);
            }
        }

        /// <summary>Plain-language secondary line under the status headline.</summary>
        public string UiStatusDetail
        {
            get
            {
                if (!ModEnabled) return L10n.T(L10n.Key.DetailEnableMod);
                string kind = UiStatusKind;
                if (kind == "error")
                    return string.IsNullOrEmpty(_lastFault)
                        ? ""
                        : FriendlyFaultSummary(_lastFault);
                if (_session.Role == SessionRole.Host && _worldSyncBarrierActive)
                    return HostWorldSyncDetail();
                if (_session.Role == SessionRole.Client &&
                    (_session.Status == SessionStatus.Connecting ||
                     _phase != ClientWorldPhase.InSession))
                    return ClientWorldSyncDetail();
                if (kind != "connected") return "";

                int players = PlayerCount;
                var sb = new System.Text.StringBuilder();
                sb.Append(players == 1
                        ? L10n.T(L10n.Key.DetailPlayersOne)
                        : L10n.F(L10n.Key.DetailPlayersMany, players))
                  .Append(" | ")
                  .Append(L10n.T(_session.PasswordProtected
                      ? L10n.Key.DetailPasswordProtected
                      : L10n.Key.DetailOpenAccess));
                if (_session.PublicExposure)
                    sb.Append(" | ").Append(L10n.T(L10n.Key.DetailPublic));
                return sb.ToString();
            }
        }

        /// <summary>Actionable recovery steps for the current fault, or empty.</summary>
        public string UiStatusHelp =>
            UiStatusKind == "error" && !string.IsNullOrEmpty(_lastFault)
                ? FriendlyFaultHelp(_lastFault)
                : "";

        /// <summary>
        /// Progress presentation shared by the full-screen loader and in-game panel.
        /// Determinate is used only while bytes move. Saving and map loading use an
        /// activity sweep, so a completed transfer never looks frozen at 100%.
        /// </summary>
        public string UiProgressMode
        {
            get
            {
                if (!ModEnabled || UiStatusKind == "error") return "none";
                if (_session.Role == SessionRole.Host && _worldSyncBarrierActive)
                {
                    if (_hostWorldSyncUiStage == HostWorldSyncUiStage.WaitingForLoaded &&
                        _session.OutgoingBlobActive &&
                        _session.OutgoingBlobTotal > 0)
                        return "determinate";
                    return "indeterminate";
                }
                if (_session.Role == SessionRole.Client &&
                    (_session.Status == SessionStatus.Connecting ||
                     _phase != ClientWorldPhase.InSession))
                    return MapTransferPercent >= 0 ? "determinate" : "indeterminate";
                return "none";
            }
        }

        private string HostWorldSyncTitle()
        {
            if (_hostWorldSyncJoiningCount == 1 &&
                !string.IsNullOrEmpty(_hostWorldSyncJoiningName))
                return L10n.F(L10n.Key.TitlePlayerJoining, _hostWorldSyncJoiningName);
            if (_hostWorldSyncJoiningCount > 1)
                return L10n.F(L10n.Key.TitlePlayersJoining, _hostWorldSyncJoiningCount);
            return L10n.T(L10n.Key.TitleRefreshingWorld);
        }

        private string HostWorldSyncDetail()
        {
            switch (_hostWorldSyncUiStage)
            {
                case HostWorldSyncUiStage.WaitingForQuiescence:
                    return L10n.T(L10n.Key.DetailPausingWorld);
                case HostWorldSyncUiStage.Saving:
                    return L10n.T(L10n.Key.DetailSavingWorld);
                case HostWorldSyncUiStage.WaitingForLoaded:
                    if (_session.OutgoingBlobActive)
                        return L10n.T(L10n.Key.DetailSendingWorld);
                    if (_hostWorldSyncJoiningCount == 1 &&
                        !string.IsNullOrEmpty(_hostWorldSyncJoiningName))
                        return L10n.F(L10n.Key.DetailWaitingForPlayer,
                            _hostWorldSyncJoiningName);
                    int waiting = _hostWorldSyncJoiningCount > 1
                        ? _hostWorldSyncJoiningCount
                        : HandshakedPeerCount();
                    return L10n.F(L10n.Key.DetailWaitingForPlayers,
                        waiting < 1 ? 1 : waiting);
                default:
                    return L10n.T(L10n.Key.PhaseSynchronizing);
            }
        }

        private string ClientWorldSyncDetail()
        {
            if (_session.AwaitingHostApproval)
                return L10n.T(L10n.Key.DetailAwaitingApproval);
            if (_session.Status == SessionStatus.Connecting ||
                _phase == ClientWorldPhase.Connecting)
                return L10n.T(L10n.Key.DetailContactingHost);
            if (MapTransferPercent >= 0)
                return L10n.F(L10n.Key.WorldMapProgress, MapTransferPercent);
            switch (_phase)
            {
                case ClientWorldPhase.WaitingForMap:
                    return L10n.T(L10n.Key.DetailHostPreparing);
                case ClientWorldPhase.LoadingMap:
                    return L10n.T(L10n.Key.DetailWorldReceived);
                case ClientWorldPhase.WaitingForResume:
                    return L10n.T(L10n.Key.DetailWorldLoaded);
                default:
                    return "";
            }
        }

        private static bool FaultContains(string fault, string value) =>
            !string.IsNullOrEmpty(fault) &&
            fault.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;

        private const string DlcMismatchMarker = "DLC mismatch - ";

        /// <summary>
        /// The host names the differing DLCs in its reject reason. That naming is the only
        /// part that says what to change, so it is shown verbatim (English, like every
        /// other fault text) beside the translated advice.
        /// </summary>
        private static string DlcMismatchDetail(string fault)
        {
            if (string.IsNullOrEmpty(fault)) return "";
            int at = fault.IndexOf(DlcMismatchMarker, StringComparison.OrdinalIgnoreCase);
            return at < 0 ? "" : fault.Substring(at + DlcMismatchMarker.Length).Trim();
        }

        private static string FriendlyFaultSummary(string fault)
        {
            if (FaultContains(fault, "removed you") || FaultContains(fault, "kicked"))
                return L10n.T(L10n.Key.ErrorRemoved);
            if (FaultContains(fault, "declined") || FaultContains(fault, "did not respond to your join"))
                return L10n.T(L10n.Key.ErrorDeclined);
            if (FaultContains(fault, "Incorrect password") ||
                FaultContains(fault, "requires a password"))
                return L10n.T(L10n.Key.ErrorPassword);
            if (FaultContains(fault, "Protocol mismatch") ||
                FaultContains(fault, "Mod version mismatch"))
                return L10n.T(L10n.Key.ErrorModVersion);
            if (FaultContains(fault, "Game version mismatch"))
                return L10n.T(L10n.Key.ErrorGameVersion);
            if (FaultContains(fault, "DLC mismatch"))
                return L10n.T(L10n.Key.ErrorDlc);
            if (FaultContains(fault, "Server is full"))
                return L10n.T(L10n.Key.ErrorFull);
            if (FaultContains(fault, "HostNotFound") ||
                FaultContains(fault, "NoData") ||
                FaultContains(fault, "could not be resolved"))
                return L10n.T(L10n.Key.ErrorAddress);
            if (FaultContains(fault, "ConnectionRefused"))
                return L10n.T(L10n.Key.ErrorRefused);
            if (FaultContains(fault, "TimedOut") ||
                FaultContains(fault, "timed out"))
                return L10n.T(L10n.Key.ErrorTimeout);
            if (FaultContains(fault, "NetworkUnreachable") ||
                FaultContains(fault, "HostUnreachable"))
                return L10n.T(L10n.Key.ErrorNetwork);
            if (FaultContains(fault, "AddressAlreadyInUse"))
                return L10n.T(L10n.Key.ErrorPortInUse);
            return L10n.T(L10n.Key.ErrorGeneric);
        }

        private static string FriendlyFaultHelp(string fault)
        {
            if (FaultContains(fault, "removed you") || FaultContains(fault, "kicked"))
                return L10n.T(L10n.Key.ErrorRemovedHelp);
            if (FaultContains(fault, "declined") || FaultContains(fault, "did not respond to your join"))
                return L10n.T(L10n.Key.ErrorDeclinedHelp);
            if (FaultContains(fault, "Incorrect password") ||
                FaultContains(fault, "requires a password"))
                return L10n.T(L10n.Key.ErrorPasswordHelp);
            if (FaultContains(fault, "Protocol mismatch") ||
                FaultContains(fault, "Mod version mismatch"))
                return L10n.T(L10n.Key.ErrorModVersionHelp);
            if (FaultContains(fault, "Game version mismatch"))
                return L10n.T(L10n.Key.ErrorGameVersionHelp);
            if (FaultContains(fault, "DLC mismatch"))
            {
                string detail = DlcMismatchDetail(fault);
                return detail.Length > 0
                    ? detail + " " + L10n.T(L10n.Key.ErrorDlcHelp)
                    : L10n.T(L10n.Key.ErrorDlcHelp);
            }
            if (FaultContains(fault, "Server is full"))
                return L10n.T(L10n.Key.ErrorFullHelp);
            if (FaultContains(fault, "HostNotFound") ||
                FaultContains(fault, "NoData") ||
                FaultContains(fault, "could not be resolved"))
                return L10n.T(L10n.Key.ErrorAddressHelp);
            if (FaultContains(fault, "ConnectionRefused"))
                return L10n.T(L10n.Key.ErrorRefusedHelp);
            if (FaultContains(fault, "TimedOut") ||
                FaultContains(fault, "timed out"))
                return L10n.T(L10n.Key.ErrorTimeoutHelp);
            if (FaultContains(fault, "NetworkUnreachable") ||
                FaultContains(fault, "HostUnreachable"))
                return L10n.T(L10n.Key.ErrorNetworkHelp);
            if (FaultContains(fault, "AddressAlreadyInUse"))
                return L10n.T(L10n.Key.ErrorPortInUseHelp);
            return L10n.T(L10n.Key.ErrorGenericHelp);
        }

        /// <summary>
        /// Players in the session including this machine. The host counts its
        /// authenticated peers; a client counts recently relayed cursors.
        /// </summary>
        public int PlayerCount
        {
            get
            {
                if (_session.Role == SessionRole.Host)
                    return HandshakedPeerCount() + 1;
                if (_session.Role == SessionRole.Client)
                {
                    int count = 1;
                    long now = NowMs;
                    foreach (var player in _remotePlayers.Values)
                        if (now - player.LastUpdateMs < 10000) count++;
                    return count < 2 ? 2 : count;
                }
                return 0;
            }
        }
    }
}
