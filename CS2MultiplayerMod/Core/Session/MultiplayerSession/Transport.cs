using System;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Networking;
using CS2MultiplayerMod.Core.Networking.Tcp;
using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Protocol.Messages;

namespace CS2MultiplayerMod.Core.Session
{
    public sealed partial class MultiplayerSession
    {
        private void HandleEvent(TransportEvent evt, long nowUnixMs)
        {
            switch (evt.Type)
            {
                case TransportEventType.Connected:
                    OnTransportConnected(evt.Connection, nowUnixMs);
                    break;
                case TransportEventType.Disconnected:
                    OnTransportDisconnected(evt.Connection, evt.Detail);
                    break;
                case TransportEventType.Data:
                    OnTransportData(evt.Connection, evt.Payload, nowUnixMs);
                    break;
            }
        }

        private void OnTransportConnected(ConnectionId connection, long nowUnixMs)
        {
            if (Role == SessionRole.Host)
            {
                string address = _transport.GetRemoteAddress(connection);

                // Addresses that keep failing the password are refused before any protocol work.
                if (_failedAuth.IsBanned(address, nowUnixMs))
                {
                    _log.Warn(LogTopic.Transport, "Refused " + connection + " (" + address +
                        "): temporarily banned after repeated auth failures.");
                    _transport.Disconnect(connection);
                    return;
                }

                if (!string.IsNullOrEmpty(address) && _hostBannedAddresses.Contains(address))
                {
                    _log.Warn(LogTopic.Transport, "Refused " + connection + " (" + address +
                        "): banned by the host for this session.");
                    SendTo(connection, HandshakeResponse.Reject(
                        "The host banned this connection for the current hosting session."));
                    _transport.DisconnectAfterFlush(connection);
                    return;
                }

                // Cap the number of sockets sitting in the pre-handshake state.
                int pending = 0;
                foreach (var pair in _peers)
                    if (!pair.Value.Handshaked) pending++;
                if (pending >= TcpServerTransport.MaxPendingConnections)
                {
                    _log.Warn(LogTopic.Transport, "Refused " + connection + " (" + address +
                        "): too many connections awaiting handshake.");
                    _transport.Disconnect(connection);
                    return;
                }

                // A client socket arrived; challenge it and await its handshake.
                var peer = new Peer(connection)
                {
                    LastSeenUnixMs = nowUnixMs,
                    ConnectedAtUnixMs = nowUnixMs,
                    RemoteAddress = address,
                    ChallengeNonce = HandshakeAuth.NewNonce(),
                };
                _peers[connection.Value] = peer;
                SendTo(connection, new HandshakeChallenge(
                    ProtocolConstants.ProtocolVersion, PasswordProtected, peer.ChallengeNonce));
                _log.Detail(LogTopic.Transport, "Client connecting on " + connection + " (" +
                    address + "); challenged, awaiting handshake.");
            }
            else // Client: the socket to the host is up — wait for its challenge.
            {
                var peer = new Peer(connection)
                {
                    Name = "Host",
                    LastSeenUnixMs = nowUnixMs,
                    ConnectedAtUnixMs = nowUnixMs,
                    RemoteAddress = _transport.GetRemoteAddress(connection),
                };
                _peers[connection.Value] = peer;
            }
        }

        private void OnTransportDisconnected(ConnectionId connection, string reason)
        {
            _puntedConnections.Remove(connection.Value);
            bool closedHere = _localCloseReasons.TryGetValue(connection.Value, out string localReason);
            if (closedHere)
            {
                _localCloseReasons.Remove(connection.Value);
                reason = localReason;
            }
            if (_peers.TryGetValue(connection.Value, out Peer peer))
            {
                _peers.Remove(connection.Value);
                bool removedByHost = _administrativeRemovals.Remove(connection.Value);
                if (peer.Handshaked)
                {
                    NotifyPeerLeft(peer, reason);
                    if (Role == SessionRole.Host)
                    {
                        // Clients get no OnPeerLeft for each other; this line is how they learn of leaves.
                        string notice = removedByHost
                            ? peer.Name + " was removed by the host."
                            : peer.Name + " left.";
                        BroadcastToAll(new ChatMessage(null, notice), ConnectionId.None);
                        NotifyChat(null, notice);
                    }
                }
                else if (Role == SessionRole.Host)
                    // A connection that never authenticates must still show up in the host's log.
                    _log.Warn(LogTopic.Transport, "Connection " + connection + " (" +
                        (peer.RemoteAddress ?? "?") + ") closed before completing the handshake: " +
                        reason);
            }

            if (Role == SessionRole.Client)
            {
                // Losing the host while Connecting is a failed join (fault). While Connected the host ended a live
                // session: a normal end, so no error log, no Faulted, no OnError.
                if (Status == SessionStatus.Connecting)
                    Fault("Could not join: " + reason);
                else if (closedHere)
                    LoseHost(reason);
                else
                    EndByRemote(reason);
            }
        }

        /// <summary>This machine gave up on the host: not a fault, and not reported as the host closing.</summary>
        private void LoseHost(string reason)
        {
            _log.Warn(LogTopic.Transport, "Lost the connection to the host (" + reason + ").");
            Stop("Lost the connection to the host (" + reason + ").");
        }

        /// <summary>The host went away: a normal end, logged at Info and shown as a plain disconnect.</summary>
        private void EndByRemote(string reason)
        {
            _log.Event(LogTopic.Transport, "Host ended the session (" + reason +
                "). Disconnecting cleanly.");
            // Keep the reason: the game layer tells the player why the host world is closing.
            Stop(reason);
        }

        private void OnTransportData(ConnectionId connection, byte[] payload, long nowUnixMs)
        {
            // Everything this connection had already sent before it was punted is discarded.
            if (_puntedConnections.Contains(connection.Value)) return;

            if (_peers.TryGetValue(connection.Value, out Peer peer))
                peer.LastSeenUnixMs = nowUnixMs;

            INetMessage message;
            try
            {
                message = _codec.Decode(payload);
            }
            catch (Exception ex)
            {
                // Malformed bytes never take the session down; their sender is disconnected.
                Punt(connection, peer, "malformed payload (" + ex.Message + ")", "decode");
                return;
            }

            Dispatch(connection, peer, message, payload.Length, nowUnixMs);
        }

        private void Dispatch(ConnectionId connection, Peer peer, INetMessage message, int payloadBytes, long nowUnixMs)
        {
            // Before the handshake (version and password checks) a connection may only send the handshake;
            // otherwise a raw connection could inject traffic without the password.
            bool handshakeTraffic = message.Type == MessageType.HandshakeRequest ||
                                    message.Type == MessageType.HandshakeResponse ||
                                    message.Type == MessageType.HandshakeChallenge ||
                                    message.Type == MessageType.HandshakePending;
            if (!handshakeTraffic && (peer == null || !peer.Handshaked))
            {
                Punt(connection, peer, "sent " + message.Type + " before authenticating", message.Type.ToString());
                return;
            }

            // Traffic budgets: the host meters everything an authenticated client sends.
            if (Role == SessionRole.Host && peer != null && peer.Handshaked)
            {
                int commandId = message.Type == MessageType.SimulationCommand
                    ? ((SimulationCommandMessage)message).CommandId
                    : -1;

                string violation = peer.RateLimiter.Account(
                    nowUnixMs, payloadBytes, commandId,
                    message.Type == MessageType.Chat,
                    message.Type == MessageType.ResyncRequest);
                if (violation != null)
                {
                    Punt(connection, peer, "rate limit exceeded: " + violation, message.Type.ToString());
                    return;
                }
            }

            switch (message.Type)
            {
                case MessageType.HandshakeChallenge:
                    HandleHandshakeChallenge(connection, (HandshakeChallenge)message);
                    break;
                case MessageType.HandshakeRequest:
                    HandleHandshakeRequest(connection, peer, (HandshakeRequest)message, nowUnixMs);
                    break;
                case MessageType.HandshakeResponse:
                    HandleHandshakeResponse(peer, (HandshakeResponse)message);
                    break;
                case MessageType.HandshakePending:
                    HandleHandshakePending(connection, peer);
                    break;
                case MessageType.Heartbeat:
                    HandleHeartbeat(connection, peer, (Heartbeat)message, nowUnixMs);
                    break;
                case MessageType.Chat:
                    HandleChat(connection, peer, (ChatMessage)message, nowUnixMs);
                    break;
                case MessageType.SimulationCommand:
                    HandleCommand(connection, peer, (SimulationCommandMessage)message);
                    break;
                case MessageType.StateSnapshot:
                    HandleState(connection, peer, (StateSnapshotMessage)message);
                    break;
                case MessageType.StateEdit:
                    HandleStateEdit(peer, (StateEditMessage)message);
                    break;
                case MessageType.PlayerState:
                    HandlePlayerState(connection, peer, (PlayerStateMessage)message);
                    break;
                case MessageType.BlobChunk:
                    HandleBlobChunk(connection, peer, (BlobChunkMessage)message, nowUnixMs);
                    break;
                case MessageType.ResyncRequest:
                    HandleResyncRequest(connection, peer, nowUnixMs,
                        ((ResyncRequestMessage)message).Reason,
                        ((ResyncRequestMessage)message).IsAutomatic);
                    break;
                case MessageType.WorldSyncControl:
                    HandleWorldSyncControl(connection, peer, (WorldSyncControlMessage)message);
                    break;
                case MessageType.DisconnectNotice:
                    HandleDisconnectNotice(connection, peer, (DisconnectNoticeMessage)message);
                    break;
            }
        }

        /// <summary>Disconnects a protocol violator with connection, player, address, message type and reason logged.</summary>
        private void Punt(ConnectionId connection, Peer peer, string reason, string messageType)
        {
            // A client never disconnects its host over a stray message; it logs and drops it.
            if (Role == SessionRole.Client)
            {
                _log.Warn(LogTopic.Transport, "Dropping " + messageType + " from host: " + reason +
                    ".");
                return;
            }

            // One security action per connection: a queued flood is one event, not one per frame.
            if (!_puntedConnections.Add(connection.Value)) return;

            string who = peer != null
                ? "player #" + peer.PlayerId + " '" + (peer.Name ?? "<pending>") + "'"
                : "<unknown peer>";
            string address = peer != null && peer.RemoteAddress != null
                ? peer.RemoteAddress
                : (_transport != null ? _transport.GetRemoteAddress(connection) : null) ?? "?";

            _log.Warn(LogTopic.Transport, "Disconnecting " + connection + " " + who + " (" + address +
                "): " + reason + " [type=" + messageType + "]");

            _localCloseReasons[connection.Value] = reason;
            if (_transport != null) _transport.Disconnect(connection);
            // Removal + observer notification happen on the transport's Disconnected event.
        }
    }
}
