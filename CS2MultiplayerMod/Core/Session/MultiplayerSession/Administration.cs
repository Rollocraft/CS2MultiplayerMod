using CS2MultiplayerMod.Core.Networking;
using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Protocol.Messages;

namespace CS2MultiplayerMod.Core.Session
{
    public sealed partial class MultiplayerSession
    {
        public bool IsLobbyLocked { get; set; }
        public string Motd { get; set; }
        public System.Collections.Generic.IReadOnlyCollection<string> BannedAddresses => _hostBannedAddresses;

        private static readonly System.Collections.Concurrent.ConcurrentQueue<SimulationCommandMessage> _commandReplayBuffer =
            new System.Collections.Concurrent.ConcurrentQueue<SimulationCommandMessage>();
        private const int MaxReplayBufferSize = 200;

        public static void RecordReplayableCommand(SimulationCommandMessage cmd)
        {
            if (cmd == null) return;
            _commandReplayBuffer.Enqueue(cmd);
            while (_commandReplayBuffer.Count > MaxReplayBufferSize && _commandReplayBuffer.TryDequeue(out _)) { }
        }

        public void ReplayCommandsToPeer(ConnectionId connection)
        {
            if (Role != SessionRole.Host || Status != SessionStatus.Connected) return;
            foreach (var cmd in _commandReplayBuffer)
            {
                SendTo(connection, cmd);
            }
        }

        public bool UnbanAddress(string address)
        {
            if (string.IsNullOrEmpty(address)) return false;
            return _hostBannedAddresses.Remove(address);
        }

        /// <summary>
        /// Host-only administrative removal. The explanation is flushed to the selected
        /// client before the socket closes, so it sees a useful error instead of a generic
        /// "remote closed" message.
        /// </summary>
        public bool KickPlayer(int playerId)
        {
            return RemovePlayer(playerId, false);
        }

        /// <summary>
        /// Host-only removal that also blocks the client's address from reconnecting
        /// until the current hosting session ends.
        /// </summary>
        public bool BanPlayer(int playerId)
        {
            return RemovePlayer(playerId, true);
        }

        private bool RemovePlayer(int playerId, bool ban)
        {
            if (Role != SessionRole.Host || Status != SessionStatus.Connected ||
                playerId <= 0 || playerId == LocalPlayerId || _transport == null)
                return false;

            Peer selected = null;
            foreach (var pair in _peers)
            {
                Peer peer = pair.Value;
                if (peer.Handshaked && peer.PlayerId == playerId)
                {
                    selected = peer;
                    break;
                }
            }
            if (selected == null) return false;
            if (ban && string.IsNullOrEmpty(selected.RemoteAddress)) return false;

            if (ban) _hostBannedAddresses.Add(selected.RemoteAddress);

            string reason = ban
                ? "The host banned you for the rest of this hosting session."
                : "The host removed you from this multiplayer session.";
            _administrativeRemovals.Add(selected.Connection.Value);
            SendTo(selected.Connection, new DisconnectNoticeMessage(reason));
            _transport.DisconnectAfterFlush(selected.Connection);
            _log.Info("Host " + (ban ? "banned " : "removed ") + selected + " from the session.");
            return true;
        }

        private void HandleDisconnectNotice(ConnectionId from, Peer peer,
            DisconnectNoticeMessage notice)
        {
            if (Role != SessionRole.Client)
            {
                Punt(from, peer, "client sent a host-only disconnect notice", "DisconnectNotice");
                return;
            }

            string reason = WireGuard.SanitizeText(notice != null ? notice.Reason : null, 512);
            if (string.IsNullOrEmpty(reason))
                reason = "The host ended your multiplayer session.";

            // A graceful notice means the session simply ended - the host quit the game or
            // went back to the main menu. Nothing failed, so it must not surface as a
            // connection error; the player is told what happened and the session closes.
            if (notice != null && notice.Graceful)
            {
                NotifyChat(null, reason);
                EndByRemote(reason);
                return;
            }

            Fault(reason);
        }
    }
}
