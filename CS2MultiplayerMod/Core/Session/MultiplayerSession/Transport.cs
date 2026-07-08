using System;
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

                // Addresses that keep failing the password are refused before any
                // protocol work happens.
                if (_failedAuth.IsBanned(address, nowUnixMs))
                {
                    _log.Warn("[security] Refused " + connection + " (" + address +
                              "): temporarily banned after repeated auth failures.");
                    _transport.Disconnect(connection);
                    return;
                }

                // Cap the number of sockets sitting in the pre-handshake state.
                int pending = 0;
                foreach (var pair in _peers)
                    if (!pair.Value.Handshaked) pending++;
                if (pending >= TcpServerTransport.MaxPendingConnections)
                {
                    _log.Warn("[security] Refused " + connection + " (" + address +
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
                _log.Info("Client connecting on " + connection + " (" + address + "); challenged, awaiting handshake.");
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
            Peer peer;
            if (_peers.TryGetValue(connection.Value, out peer))
            {
                _peers.Remove(connection.Value);
                if (peer.Handshaked)
                {
                    NotifyPeerLeft(peer, reason);
                    if (Role == SessionRole.Host)
                    {
                        // Mirror of the join notice: clients get no OnPeerLeft for each
                        // other, so this system chat line is how every machine learns of
                        // a leave (and the host's own UI via NotifyChat).
                        string notice = peer.Name + " left.";
                        BroadcastToAll(new ChatMessage(null, notice), ConnectionId.None);
                        NotifyChat(null, notice);
                    }
                }
                else if (Role == SessionRole.Host)
                    // Without this line a client that connects but never authenticates
                    // (TLS failure, crash, wrong build) vanishes without a trace in the
                    // host's log — the single worst blind spot when debugging joins.
                    _log.Info("Connection " + connection + " (" + (peer.RemoteAddress ?? "?") +
                              ") closed before completing the handshake: " + reason);
            }

            if (Role == SessionRole.Client)
            {
                // Losing the host ends the session for a client. Distinguish the two cases:
                //  - still Connecting  → the join never completed (host absent, rejected, TLS/handshake
                //    failure): a genuine failure the player needs to see, so fault.
                //  - already Connected → the host closed a LIVE session (quit the game, stopped hosting,
                //    or simply left). That is a normal end of session, not a client-side error — hosts
                //    rarely wait for everyone to leave first. Treat it as a clean disconnect: no error
                //    log, no Faulted status, no OnError; just end the session quietly.
                if (Status == SessionStatus.Connecting)
                    Fault("Could not join: " + reason);
                else
                    EndByRemote(reason);
            }
        }

        /// <summary>
        /// End client session because host went away - normal, expected event, not a fault.
        /// Logs at Info, stops cleanly, and <see cref="Stop"/> sets Offline status.
        /// UI reports plain disconnect rather than error.
        /// </summary>
        private void EndByRemote(string reason)
        {
            _log.Info("Host ended the session (" + reason + "). Disconnecting cleanly.");
            Stop();
        }

        private void OnTransportData(ConnectionId connection, byte[] payload, long nowUnixMs)
        {
            Peer peer;
            if (_peers.TryGetValue(connection.Value, out peer))
                peer.LastSeenUnixMs = nowUnixMs;

            INetMessage message;
            try
            {
                message = _codec.Decode(payload);
            }
            catch (Exception ex)
            {
                // Malformed bytes from a peer must never take the session down — and a
                // peer that sends them has no business staying connected.
                Punt(connection, peer, "malformed payload (" + ex.Message + ")", "decode");
                return;
            }

            Dispatch(connection, peer, message, payload.Length, nowUnixMs);
        }

        private void Dispatch(ConnectionId connection, Peer peer, INetMessage message, int payloadBytes, long nowUnixMs)
        {
            // Security gate: until a connection has completed the handshake (and with it
            // the protocol-version and password checks), the only thing it may say is the
            // handshake itself. Without this, a raw TCP connection could inject commands,
            // chat, blobs or resync requests while skipping the password entirely.
            bool handshakeTraffic = message.Type == MessageType.HandshakeRequest ||
                                    message.Type == MessageType.HandshakeResponse ||
                                    message.Type == MessageType.HandshakeChallenge;
            if (!handshakeTraffic && (peer == null || !peer.Handshaked))
            {
                Punt(connection, peer, "sent " + message.Type + " before authenticating", message.Type.ToString());
                return;
            }

            // Traffic budgets: the host meters everything an authenticated client sends.
            if (Role == SessionRole.Host && peer != null && peer.Handshaked)
            {
                string violation = peer.RateLimiter.Account(
                    nowUnixMs, payloadBytes,
                    message.Type == MessageType.SimulationCommand,
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
                    HandleResyncRequest(connection, peer, nowUnixMs);
                    break;
            }
        }

        /// <summary>
        /// Disconnect a peer that violated the protocol, with a structured log line
        /// carrying everything needed to understand the event later: connection,
        /// player identity, remote address, offending message type, and reason.
        /// </summary>
        private void Punt(ConnectionId connection, Peer peer, string reason, string messageType)
        {
            // A client never disconnects the host for a stray message — losing the host
            // ends the whole session. Strays are logged and dropped instead.
            if (Role == SessionRole.Client)
            {
                _log.Warn("[security] Dropping " + messageType + " from host: " + reason + ".");
                return;
            }

            string who = peer != null
                ? "player #" + peer.PlayerId + " '" + (peer.Name ?? "<pending>") + "'"
                : "<unknown peer>";
            string address = peer != null && peer.RemoteAddress != null
                ? peer.RemoteAddress
                : (_transport != null ? _transport.GetRemoteAddress(connection) : null) ?? "?";

            _log.Warn("[security] Disconnecting " + connection + " " + who + " (" + address +
                      "): " + reason + " [type=" + messageType + "]");

            if (_transport != null) _transport.Disconnect(connection);
            // Removal + observer notification happen on the transport's Disconnected event.
        }

    }
}
