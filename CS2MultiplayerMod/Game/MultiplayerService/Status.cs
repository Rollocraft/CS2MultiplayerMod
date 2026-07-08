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
                        : L10n.F(L10n.Key.OfflineFault, _lastFault);
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
                if (_session.Role == SessionRole.Host)
                    return L10n.T(_session.PublicExposure ? L10n.Key.ExposureInternet : L10n.Key.ExposureLan);
                if (_session.Role == SessionRole.Client) return L10n.T(L10n.Key.ConnectedToHost);
                return L10n.T(L10n.Key.NoSession);
            }
        }

        public string StatusWorldText
        {
            get
            {
                if (_session.Role == SessionRole.None) return L10n.T(L10n.Key.WorldNone);
                if (_session.Role == SessionRole.Host) return L10n.T(L10n.Key.WorldHosting);
                if (_session.IncomingBlobChannel == MapChannel && _session.IncomingBlobTotal > 0)
                    return L10n.F(L10n.Key.WorldMapProgress,
                        (int)(100L * _session.IncomingBlobReceived / _session.IncomingBlobTotal));
                return _phase == ClientWorldPhase.InSession ? L10n.T(L10n.Key.WorldLoaded) : PhaseText(_phase);
            }
        }

        public int MapTransferPercent
        {
            get
            {
                if (_session.IncomingBlobChannel != MapChannel || _session.IncomingBlobTotal <= 0) return -1;
                long percent = 100L * _session.IncomingBlobReceived / _session.IncomingBlobTotal;
                if (percent < 0) return 0;
                if (percent > 100) return 100;
                return (int)percent;
            }
        }

        /// <summary>
        /// Host-side world-send progress (0-100), or -1 when no world is streaming out.
        /// Lets the host show "Sending world X%" while the save drains to clients, instead
        /// of the window appearing frozen.
        /// </summary>
        public int WorldSendPercent
        {
            get
            {
                if (!_session.OutgoingBlobActive || _session.OutgoingBlobTotal <= 0) return -1;
                long percent = 100L * _session.OutgoingBlobSent / _session.OutgoingBlobTotal;
                if (percent < 0) return 0;
                if (percent > 100) return 100;
                return (int)percent;
            }
        }

        /// <summary>
        /// Coarse status bucket for the join dialog's colored indicator:
        /// "disabled", "offline", "connecting", "connected" or "error".
        /// </summary>
        public string UiStatusKind
        {
            get
            {
                if (!ModEnabled) return "disabled";
                if (_session.Role == SessionRole.None)
                    return string.IsNullOrEmpty(_lastFault) ? "offline" : "error";
                if (_session.Status == SessionStatus.Faulted) return "error";
                if (_session.Status == SessionStatus.Connecting ||
                    (_session.Role == SessionRole.Client && _phase != ClientWorldPhase.InSession))
                    return "connecting";
                return "connected";
            }
        }

        /// <summary>Short headline shown next to the join dialog's status indicator.</summary>
        public string UiStatusTitle
        {
            get
            {
                if (!ModEnabled) return L10n.T(L10n.Key.TitleModDisabled);
                if (_session.Role == SessionRole.None)
                    return L10n.T(string.IsNullOrEmpty(_lastFault) ? L10n.Key.StatusOffline : L10n.Key.TitleConnectionFailed);
                if (_session.Status == SessionStatus.Faulted) return L10n.T(L10n.Key.TitleConnectionFailed);
                if (_session.Status == SessionStatus.Connecting) return L10n.T(L10n.Key.StateConnecting);
                if (_session.Role == SessionRole.Client)
                {
                    switch (_phase)
                    {
                        case ClientWorldPhase.Connecting: return L10n.T(L10n.Key.StateConnecting);
                        case ClientWorldPhase.WaitingForMap: return L10n.T(L10n.Key.PhaseWaitingForMap);
                        case ClientWorldPhase.LoadingMap: return L10n.T(L10n.Key.PhaseLoadingMap);
                    }
                }
                return L10n.T(_session.Role == SessionRole.Host ? L10n.Key.TitleHosting : L10n.Key.StateConnected);
            }
        }

        /// <summary>Secondary line under the status headline; empty while a state is in flight.</summary>
        public string UiStatusDetail
        {
            get
            {
                if (!ModEnabled) return L10n.T(L10n.Key.DetailEnableMod);
                string kind = UiStatusKind;
                // Fault reasons are technical diagnostics produced by the session core;
                // they are passed through untranslated by design.
                if (kind == "error") return string.IsNullOrEmpty(_lastFault) ? "" : _lastFault;
                if (kind != "connected") return "";

                int peers = HandshakedPeerCount();
                var sb = new System.Text.StringBuilder();
                sb.Append(peers == 1 ? L10n.T(L10n.Key.DetailPlayersOne) : L10n.F(L10n.Key.DetailPlayersMany, peers))
                  .Append(" · ")
                  .Append(L10n.T(_session.PasswordProtected ? L10n.Key.DetailPasswordProtected : L10n.Key.DetailOpenAccess));
                if (_session.PublicExposure) sb.Append(" · ").Append(L10n.T(L10n.Key.DetailPublic));
                return sb.ToString();
            }
        }


        /// <summary>
        /// Players in the session including this machine. The host counts its
        /// authenticated peers; a client only ever talks to the host, so it counts
        /// the players whose relayed cursor states are fresh (everyone sends at
        /// ~10 Hz, so a player silent for 10 s has left or the relay is stale).
        /// </summary>
        public int PlayerCount
        {
            get
            {
                if (_session.Role == SessionRole.Host) return HandshakedPeerCount() + 1;
                if (_session.Role == SessionRole.Client)
                {
                    int count = 1;
                    long now = NowMs;
                    foreach (var player in _remotePlayers.Values)
                        if (now - player.LastUpdateMs < 10000) count++;
                    return count < 2 ? 2 : count; // at minimum self + host while connected
                }
                return 0;
            }
        }
    }
}
