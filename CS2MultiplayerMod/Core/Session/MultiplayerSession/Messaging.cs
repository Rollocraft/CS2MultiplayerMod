using System;
using System.Collections.Generic;
using CS2MultiplayerMod.Core.Networking;
using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Protocol.Messages;

namespace CS2MultiplayerMod.Core.Session
{
    public sealed partial class MultiplayerSession
    {
        private void HandleHeartbeat(ConnectionId connection, Peer peer, Heartbeat heartbeat, long nowUnixMs)
        {
            // An echo returns a timestamp WE sent, so now − echo is a true round-trip on
            // our own clock — the peer's clock never enters the math (the two machines'
            // clocks are unrelated: each side passes its own monotonic ms). Echoes are
            // not echoed back, so a ping costs exactly one reply.
            if (heartbeat.EchoOfMs > 0)
            {
                long rtt = nowUnixMs - heartbeat.EchoOfMs;
                if (peer != null && rtt >= 0 && rtt < 60000) peer.LatencyMs = (int)rtt;
                return;
            }

            // A ping: return the sender's timestamp so IT can measure its round-trip.
            if (heartbeat.SentAtMs > 0)
                SendTo(connection, new Heartbeat(nowUnixMs, heartbeat.SentAtMs));
        }

        private void PumpHeartbeats(long nowUnixMs)
        {
            if (nowUnixMs - _lastHeartbeatMs < HeartbeatIntervalMs) return;
            _lastHeartbeatMs = nowUnixMs;

            var beat = new Heartbeat(nowUnixMs);
            if (Role == SessionRole.Host)
                BroadcastToAll(beat, ConnectionId.None);
            else
                SendTo(ConnectionId.Server, beat);
        }

        private void ReapTimedOutPeers(long nowUnixMs)
        {
            List<Peer> dead = null;
            foreach (var pair in _peers)
            {
                Peer peer = pair.Value;
                bool expired = peer.Handshaked
                    ? nowUnixMs - peer.LastSeenUnixMs > PeerTimeoutMs
                    // Pending sockets must finish the handshake promptly or make room.
                    : nowUnixMs - peer.ConnectedAtUnixMs > HandshakeTimeoutMs;
                if (!expired) continue;
                (dead ?? (dead = new List<Peer>())).Add(peer);
            }

            if (dead == null) return;
            foreach (Peer peer in dead)
            {
                _log.Warn((peer.Handshaked ? "Peer timed out: " : "Handshake timed out: ") + peer);
                _transport.Disconnect(peer.Connection);
                // The transport will also raise Disconnected; removal/notify happens there.
            }
        }

        /// <summary>
        /// Send a chat line. On the host it is relayed to all clients. "/sync" is a
        /// command, not a line: it asks the host for a fresh world stream instead.
        /// </summary>
        public void SendChat(string text)
        {
            if (Status != SessionStatus.Connected || string.IsNullOrEmpty(text)) return;
            if (IsSyncCommand(text)) { RequestWorldSync(); return; }

            text = WireGuard.SanitizeText(text, WireGuard.MaxChatLength);
            if (text.Length == 0) return;

            var message = new ChatMessage(LocalPlayerName, text);
            if (Role == SessionRole.Host)
                BroadcastToAll(message, ConnectionId.None);
            else
                SendTo(ConnectionId.Server, message);
        }

        private void HandleChat(ConnectionId from, Peer peer, ChatMessage chat, long nowUnixMs)
        {
            // A "/sync" line from a client (e.g. an older build that sends it as raw
            // chat) is treated as the command it means.
            if (Role == SessionRole.Host && IsSyncCommand(chat.Text))
            {
                HandleResyncRequest(from, peer, nowUnixMs);
                return;
            }

            // Whatever arrives is displayed and logged — so control characters, fake
            // newlines and kilometer-long lines are stripped before anything sees them.
            chat.Text = WireGuard.SanitizeText(chat.Text, WireGuard.MaxChatLength);

            // Never trust the sender's claimed name — display the one we authenticated.
            if (Role == SessionRole.Host && peer != null)
                chat.SenderName = peer.Name;
            else
                chat.SenderName = chat.SenderName == null
                    ? null // system notice
                    : WireGuard.SanitizePlayerName(chat.SenderName);

            NotifyChat(chat.SenderName, chat.Text);
            // Host fans a client's message out to the other clients.
            if (Role == SessionRole.Host)
                BroadcastToAll(chat, from);
        }

        private static bool IsSyncCommand(string text) =>
            text != null && text.Trim().Equals("/sync", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Player-initiated drift correction. A client asks the host to stream it the
        /// current world; on the host it refreshes every client. The actual save+stream
        /// is done by the observer (the game layer owns savegames).
        /// </summary>
        public void RequestWorldSync()
        {
            if (Status != SessionStatus.Connected) return;

            if (Role == SessionRole.Client)
            {
                SendTo(ConnectionId.Server, new ResyncRequestMessage(LocalPlayerId));
                _log.Info("World sync request sent to host.");
                NotifyChat(null, "World sync requested - the host will stream you its city.");
            }
            else if (Role == SessionRole.Host)
            {
                _log.Info("Host requested world sync for all clients.");
                NotifyResyncRequested(LocalPlayerId, ConnectionId.None);
                NotifyChat(null, "World sync started - streaming the city to all players.");
            }
        }

        private void HandleResyncRequest(ConnectionId from, Peer peer, long nowUnixMs)
        {
            if (Role != SessionRole.Host) return;

            // Rate limit: a misbehaving client spamming /sync would otherwise keep the
            // host in a permanent save+stream loop. (Per-peer budgets run on top.)
            if (nowUnixMs - _lastResyncAcceptedUnixMs < ResyncRequestCooldownMs)
            {
                _log.Warn("Ignoring /sync from " + (peer != null ? peer.ToString() : from.ToString()) +
                          ": a world sync ran moments ago.");
                return;
            }
            _lastResyncAcceptedUnixMs = nowUnixMs;

            // The requester's identity comes from OUR peer table, never from the wire.
            string name = peer != null && peer.Name != null ? peer.Name : from.ToString();

            // Tell everyone the world is about to snap, then let the game layer stream it.
            string notice = name + " requested a world sync.";
            BroadcastToAll(new ChatMessage(null, notice), ConnectionId.None);
            NotifyChat(null, notice);
            NotifyResyncRequested(peer != null ? peer.PlayerId : -1, from);
        }

        /// <summary>
        /// Submit a simulation command for synchronization. The host applies it locally
        /// and relays to clients; a client forwards it to the host, which then relays.
        /// </summary>
        public void SendCommand(long tick, ushort commandId, byte[] body)
        {
            if (Status != SessionStatus.Connected) return;

            var message = new SimulationCommandMessage(LocalPlayerId, tick, commandId, body);
            if (Role == SessionRole.Host)
            {
                NotifyCommand(message);                    // apply on the host
                BroadcastToAll(message, ConnectionId.None); // and to clients
            }
            else
            {
                SendTo(ConnectionId.Server, message);       // host will echo/relay back
            }
        }

        private void HandleCommand(ConnectionId from, Peer peer, SimulationCommandMessage command)
        {
            // Only command ids the game layer registered are legitimate; anything else
            // is a peer probing the surface.
            if (_allowedCommandIds.Count > 0 && !_allowedCommandIds.Contains(command.CommandId))
            {
                Punt(from, peer, "unauthorized command id " + command.CommandId, "SimulationCommand");
                return;
            }

            // The origin id drives every echo-skip; stamp it from OUR peer table so a
            // client cannot impersonate another player (or the host) on the wire.
            if (Role == SessionRole.Host && peer != null)
                command.OriginPlayerId = peer.PlayerId;

            NotifyCommand(command);
            if (Role == SessionRole.Host)
                BroadcastToAll(command, from); // relay to the other clients
        }

        /// <summary>
        /// Broadcast an authoritative state slice to all clients. Host-only: replicated
        /// state flows one way, from the authority outward. A client call is ignored.
        /// </summary>
        public void SendState(byte channelId, byte[] data)
        {
            if (Role != SessionRole.Host || Status != SessionStatus.Connected) return;
            BroadcastToAll(new StateSnapshotMessage(channelId, data), ConnectionId.None);
        }

        private void HandleState(ConnectionId from, Peer peer, StateSnapshotMessage snapshot)
        {
            // Only clients apply replicated state; a client pushing "authoritative"
            // state at the host is impersonating the authority.
            if (Role == SessionRole.Host)
            {
                Punt(from, peer, "client sent a host-only state snapshot", "StateSnapshot");
                return;
            }
            NotifyState(snapshot);
        }

        /// <summary>
        /// Client -> host: submit an edit of a player-editable state channel (taxes, policies, ...).
        /// Body uses channel's snapshot encoding; host applies it in next broadcast. Host-side edits
        /// need no message - host's capture already picks them up.
        /// </summary>
        public void SendStateEdit(byte channelId, byte[] data)
        {
            if (Role != SessionRole.Client || Status != SessionStatus.Connected) return;
            SendTo(ConnectionId.Server, new StateEditMessage(LocalPlayerId, channelId, data));
        }

        private void HandleStateEdit(Peer peer, StateEditMessage edit)
        {
            // Only the host arbitrates edits; a client receiving one is a stray.
            if (Role != SessionRole.Host) return;

            if (peer != null) edit.OriginPlayerId = peer.PlayerId; // no impersonation
            NotifyStateEdit(edit);
        }

        /// <summary>Publish the local player's camera focus and eye position to the others.</summary>
        public void SendPlayerState(float x, float y, float z, float eyeX, float eyeY, float eyeZ, float yaw)
        {
            if (Status != SessionStatus.Connected) return;

            var message = new PlayerStateMessage(LocalPlayerId, x, y, z, eyeX, eyeY, eyeZ, yaw);
            if (Role == SessionRole.Host)
                BroadcastToAll(message, ConnectionId.None);
            else
                SendTo(ConnectionId.Server, message);
        }

        private void HandlePlayerState(ConnectionId from, Peer peer, PlayerStateMessage state)
        {
            // Same anti-impersonation stamp as commands: positions are keyed by player id.
            if (Role == SessionRole.Host && peer != null)
                state.PlayerId = peer.PlayerId;

            NotifyPlayerState(state);
            if (Role == SessionRole.Host)
                BroadcastToAll(state, from); // fan a client's position out to the others
        }

    }
}
