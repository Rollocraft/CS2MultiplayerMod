using System;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Localization;

namespace CS2MultiplayerMod.Game
{
    public sealed partial class MultiplayerService
    {
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

        /// <summary>Host world-send progress (0-100), or -1 when nothing is streaming.</summary>
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

        /// <summary>disabled, offline, connecting, syncing, connected, or error.</summary>
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

        /// <summary>Allow-listed GitHub help page for the current fault, or empty.</summary>
        public string UiStatusHelpPage =>
            UiStatusKind == "error" && !string.IsNullOrEmpty(_lastFault)
                ? FriendlyFaultHelpPage(_lastFault)
                : "";

        /// <summary>Determinate only while bytes move; saving and loading sweep, so 100% never looks frozen.</summary>
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

        private const string DlcMismatchMarker = "DLC mismatch - ";

        /// <summary>Whatever a fault lists after its marker, or "" when it carries none.</summary>
        private static string MarkedDetail(string fault, string marker)
        {
            if (string.IsNullOrEmpty(fault)) return "";
            int at = fault.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            return at < 0 ? "" : fault.Substring(at + marker.Length).Trim();
        }

        /// <summary>A fault category: the phrases that identify it, its texts and its help page.</summary>
        private sealed class FaultKind
        {
            public string Summary, Help, Page, Marker, Separator;
            public string[] Phrases;
        }

        private static FaultKind Fault(string summary, string help, string page, params string[] phrases) =>
            new FaultKind { Summary = summary, Help = help, Page = page, Phrases = phrases };

        // First match wins. The relay entry only picks the help page; its texts come from a later entry.
        private static readonly FaultKind[] FaultKinds =
        {
            Fault(L10n.Key.ErrorRemoved, L10n.Key.ErrorRemovedHelp, HelpLinks.Removed, "removed you", "kicked"),
            Fault(L10n.Key.ErrorDeclined, L10n.Key.ErrorDeclinedHelp, HelpLinks.Declined,
                "declined", "did not respond to your join"),
            Fault(L10n.Key.ErrorPassword, L10n.Key.ErrorPasswordHelp, HelpLinks.Password,
                "Incorrect password", "requires a password"),
            Fault(L10n.Key.ErrorModVersion, L10n.Key.ErrorModVersionHelp, HelpLinks.ModVersion,
                "Protocol mismatch", "Mod version mismatch"),
            Fault(L10n.Key.ErrorGameVersion, L10n.Key.ErrorGameVersionHelp, HelpLinks.GameVersion,
                "Game version mismatch"),
            new FaultKind
            {
                Summary = L10n.Key.ErrorDlc, Help = L10n.Key.ErrorDlcHelp, Page = HelpLinks.Dlc,
                Phrases = new[] { "DLC mismatch" }, Marker = DlcMismatchMarker, Separator = " ",
            },
            new FaultKind
            {
                Summary = L10n.Key.ErrorMods, Help = L10n.Key.ErrorModsHelp, Page = HelpLinks.Mods,
                Phrases = new[] { ModsCheck.FaultMarker }, Marker = ModsCheck.FaultMarker, Separator = " - ",
            },
            Fault(L10n.Key.ErrorFull, L10n.Key.ErrorFullHelp, HelpLinks.SessionFull, "Server is full"),
            Fault(null, null, HelpLinks.Relay, "Steam relay", "join code"),
            Fault(L10n.Key.ErrorAddress, L10n.Key.ErrorAddressHelp, HelpLinks.Address,
                "HostNotFound", "NoData", "could not be resolved"),
            Fault(L10n.Key.ErrorRefused, L10n.Key.ErrorRefusedHelp, HelpLinks.DirectConnection, "ConnectionRefused"),
            Fault(L10n.Key.ErrorTimeout, L10n.Key.ErrorTimeoutHelp, HelpLinks.DirectConnection,
                "TimedOut", "timed out"),
            Fault(L10n.Key.ErrorNetwork, L10n.Key.ErrorNetworkHelp, HelpLinks.DirectConnection,
                "NetworkUnreachable", "HostUnreachable"),
            Fault(L10n.Key.ErrorPortInUse, L10n.Key.ErrorPortInUseHelp, HelpLinks.DirectConnection,
                "AddressAlreadyInUse"),
        };

        private static readonly FaultKind GenericFault =
            Fault(L10n.Key.ErrorGeneric, L10n.Key.ErrorGenericHelp, HelpLinks.Generic);

        /// <summary>The first matching kind; <paramref name="forPage"/> also considers page-only kinds.</summary>
        private static FaultKind ClassifyFault(string fault, bool forPage = false)
        {
            if (string.IsNullOrEmpty(fault)) return GenericFault;
            foreach (FaultKind kind in FaultKinds)
            {
                if (kind.Summary == null && !forPage) continue;
                foreach (string phrase in kind.Phrases)
                    if (fault.IndexOf(phrase, StringComparison.OrdinalIgnoreCase) >= 0) return kind;
            }
            return GenericFault;
        }

        private static string FriendlyFaultSummary(string fault) => L10n.T(ClassifyFault(fault).Summary);

        private static string FriendlyFaultHelp(string fault)
        {
            FaultKind kind = ClassifyFault(fault);
            string help = L10n.T(kind.Help);
            string detail = kind.Marker != null ? MarkedDetail(fault, kind.Marker) : "";
            return detail.Length > 0 ? detail + kind.Separator + help : help;
        }

        /// <summary>Every surfaced error maps to a troubleshooting page.</summary>
        private static string FriendlyFaultHelpPage(string fault) => ClassifyFault(fault, forPage: true).Page;

        /// <summary>Including this machine: the host counts authenticated peers, a client recent cursors.</summary>
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
