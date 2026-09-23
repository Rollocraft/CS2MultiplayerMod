using System;
using System.Collections.Generic;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Networking;
using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Protocol.Messages;

namespace CS2MultiplayerMod.Core.Session
{
    public sealed partial class MultiplayerSession
    {
        private void HandleHeartbeat(ConnectionId connection, Peer peer, Heartbeat heartbeat, long nowUnixMs)
        {
            // Round-trip on our own clock; echoes are not echoed back.
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

        /// <summary>
        /// Host only: names the command rate that ran past budget, from the pump so a burst that stops is
        /// still reported. Nobody is disconnected for it.
        /// </summary>
        private void ReportCommandOverruns(long nowUnixMs)
        {
            if (Role != SessionRole.Host) return;
            foreach (var pair in _peers)
            {
                Peer peer = pair.Value;
                if (!peer.Handshaked) continue;
                string overrun = peer.RateLimiter.TakeCommandOverrun(nowUnixMs);
                if (overrun != null)
                    _log.Warn(LogTopic.Transport, "Command rate from " + peer + ": " + overrun);
            }
        }

        private void ReapTimedOutPeers(long nowUnixMs)
        {
            List<Peer> dead = null;         // dropped silently at the socket
            List<Peer> unanswered = null;   // approvals the host never answered
            foreach (var pair in _peers)
            {
                Peer peer = pair.Value;
                if (peer.Handshaked)
                {
                    long silentMs = nowUnixMs - peer.LastSeenUnixMs;
                    if (silentMs <= PeerTimeoutMs) continue;

                    if (silentMs <= StalledPeerTimeoutMs && InboundStillArriving(peer.Connection))
                    {
                        if (peer.StallReportedForSeenMs != peer.LastSeenUnixMs)
                        {
                            peer.StallReportedForSeenMs = peer.LastSeenUnixMs;
                            _log.Warn(LogTopic.Session, "No complete message from " + peer + " for " +
                                (silentMs / 1000) + " s, but its traffic is still arriving; keeping the " +
                                "connection for up to " + (StalledPeerTimeoutMs / 1000) + " s.");
                        }
                        continue;
                    }
                    (dead ?? (dead = new List<Peer>())).Add(peer);
                }
                else if (peer.AwaitingApproval)
                {
                    // No host decision in time: auto-decline and tell the player.
                    if (nowUnixMs - peer.ConnectedAtUnixMs > JoinApprovalTimeoutMs)
                        (unanswered ?? (unanswered = new List<Peer>())).Add(peer);
                }
                // Pending sockets must finish the handshake promptly or make room.
                else if (nowUnixMs - peer.ConnectedAtUnixMs > HandshakeTimeoutMs)
                    (dead ?? (dead = new List<Peer>())).Add(peer);
            }

            if (dead != null)
                foreach (Peer peer in dead)
                {
                    string why = peer.Handshaked
                        ? "timed out: no complete message for " + ((nowUnixMs - peer.LastSeenUnixMs) / 1000) + " s"
                        : "handshake not completed within " + (HandshakeTimeoutMs / 1000) + " s";
                    _log.Warn(LogTopic.Session,
                        (peer.Handshaked ? "Peer timed out: " : "Handshake timed out: ") + peer + " (" + why + ").");
                    _localCloseReasons[peer.Connection.Value] = why;
                    _transport.Disconnect(peer.Connection);
                    // The transport will also raise Disconnected; removal/notify happens there.
                }

            if (unanswered != null)
                foreach (Peer peer in unanswered)
                    // Reject logs, delivers the reason, flushes, and removes the peer.
                    Reject(peer.Connection, "The host did not respond to your join request in time.");
        }

        private bool InboundStillArriving(ConnectionId connection)
        {
            if (_transport is not IInboundActivity activity) return false;
            long last = activity.LastInboundActivityMs(connection);
            return last != long.MinValue && MonotonicClock.NowMs - last <= PeerTimeoutMs;
        }

        /// <summary>Sends a chat line (the host relays it); "/sync" requests a world stream instead.</summary>
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
            // A raw "/sync" chat line from a client is treated as the command.
            if (Role == SessionRole.Host && IsSyncCommand(chat.Text))
            {
                HandleResyncRequest(from, peer, nowUnixMs);
                return;
            }

            // Displayed and logged: strip control characters and cap the length.
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
        /// Player drift correction: a client asks for the world, the host refreshes every client. The
        /// observer (game layer) does the save and stream.
        /// </summary>
        public void RequestWorldSync() => RequestWorldSync(ManualSyncReason, false);

        /// <summary>Recovery initiated by the mod, independently of a player's sync button.</summary>
        public void RequestAutomaticWorldSync(string reason) => RequestWorldSync(reason, true);

        private void RequestWorldSync(string reason, bool automatic)
        {
            if (Status != SessionStatus.Connected) return;
            reason = WireGuard.SanitizeText(reason, WireGuard.MaxResyncReasonLength);
            if (reason.Length == 0) reason = automatic ? "automatic recovery" : ManualSyncReason;
            if (_worldSyncSuspended)
            {
                _log.Detail(LogTopic.Session, "World sync request coalesced into active epoch " +
                    _worldSyncEpoch + " (" + reason + ").");
                return;
            }

            if (Role == SessionRole.Client)
            {
                SendTo(ConnectionId.Server, new ResyncRequestMessage(LocalPlayerId, reason, automatic));
                _log.Event(LogTopic.Session, "World sync request sent to host (" + reason + ").");
                NotifyChat(null, automatic
                    ? "The mod sent an automatic world sync request to the host."
                    : "World sync request sent to the host.");
            }
            else if (Role == SessionRole.Host)
            {
                _log.Event(LogTopic.Session,
                    (automatic ? "Automatic world recovery started on host (" :
                        "Host requested world sync for all clients (") + reason + ").");
                string notice = automatic
                    ? "The mod started an automatic world sync - streaming the city to all players."
                    : "World sync started - streaming the city to all players.";
                BroadcastToAll(new ChatMessage(null, notice), ConnectionId.None);
                NotifyResyncRequested(LocalPlayerId, ConnectionId.None);
                NotifyChat(null, notice);
            }
        }

        /// <summary>What a request carries when the player asked for it themselves.</summary>
        internal const string ManualSyncReason = "requested by the player";

        private void HandleResyncRequest(ConnectionId from, Peer peer, long nowUnixMs) =>
            HandleResyncRequest(from, peer, nowUnixMs, ManualSyncReason, false);

        private void HandleResyncRequest(ConnectionId from, Peer peer, long nowUnixMs, string reason,
            bool automatic)
        {
            if (Role != SessionRole.Host) return;

            reason = WireGuard.SanitizeText(reason, WireGuard.MaxResyncReasonLength);
            if (reason.Length == 0) reason = automatic ? "automatic recovery" : ManualSyncReason;
            ClientResyncPolicy resyncPolicy = _config != null
                ? _config.ClientResyncPolicy
                : ClientResyncPolicy.Allow;

            if (resyncPolicy == ClientResyncPolicy.HostOnly)
            {
                string deniedName = peer != null && peer.Name != null ? peer.Name : from.ToString();
                _log.Warn(LogTopic.Session, "Declined world sync request from " + deniedName +
                    " because the session policy is host-only.");
                SendTo(from, new ChatMessage(null,
                    "World sync request declined: this session allows host-initiated syncs only."));
                return;
            }

            if (resyncPolicy == ClientResyncPolicy.RequireApproval)
            {
                if (peer == null) return;
                peer.PendingResyncReason = reason;
                peer.PendingResyncAutomatic = automatic;
                if (!peer.AwaitingResyncApproval)
                {
                    peer.AwaitingResyncApproval = true;
                    _log.Event(LogTopic.Session, "World sync request from " + peer.Name +
                        " is waiting for host approval: " + reason + ".");
                    SendTo(from, new ChatMessage(null,
                        "World sync request received - waiting for host approval."));
                }
                else
                {
                    _log.Detail(LogTopic.Session, "Updated pending world sync request from " +
                        peer.Name + ": " + reason + ".");
                }
                return;
            }

            AcceptClientResyncRequest(from, peer, nowUnixMs, reason, automatic);
        }

        private bool AcceptClientResyncRequest(ConnectionId from, Peer peer, long nowUnixMs,
            string reason, bool automatic)
        {
            // Stops /sync spam from looping the host; per-peer budgets run on top.
            if (nowUnixMs - _lastResyncAcceptedUnixMs < ResyncRequestCooldownMs)
            {
                _log.Warn(LogTopic.Session, "Ignoring world sync request from " +
                    (peer != null ? peer.ToString() : from.ToString()) + " (" + reason +
                    "): a world sync ran moments ago.");
                return false;
            }
            _lastResyncAcceptedUnixMs = nowUnixMs;

            // The requester's identity comes from OUR peer table, never from the wire.
            string name = peer != null && peer.Name != null ? peer.Name : from.ToString();

            // The host log must tell a player's button from a client pipeline that gave up.
            _log.Event(LogTopic.Session, (automatic ? "Automatic world recovery requested by the mod on " :
                "World sync requested by ") + name + ": " + reason + ".");

            // Tell everyone the world is about to snap, then let the game layer stream it.
            string notice = automatic
                ? "The mod triggered an automatic world sync to recover synchronization."
                : name + " requested a world sync.";
            BroadcastToAll(new ChatMessage(null, notice), ConnectionId.None);
            NotifyChat(null, notice);
            NotifyResyncRequested(peer != null ? peer.PlayerId : -1, from);
            return true;
        }

        /// <summary>Host-only: accept a queued client world-sync request.</summary>
        public bool ApproveResyncRequest(int playerId, long nowUnixMs)
        {
            if (Role != SessionRole.Host) return false;
            Peer peer = FindPendingResyncRequest(playerId);
            if (peer == null) return false;

            string reason = peer.PendingResyncReason;
            bool automatic = peer.PendingResyncAutomatic;
            // Another approved sync just began: keep this card queued.
            if (AcceptClientResyncRequest(peer.Connection, peer, nowUnixMs, reason, automatic))
                ClearPendingResync(peer);
            return true;
        }

        /// <summary>Host-only: decline a queued client world-sync request.</summary>
        public bool DeclineResyncRequest(int playerId)
        {
            if (Role != SessionRole.Host) return false;
            Peer peer = FindPendingResyncRequest(playerId);
            if (peer == null) return false;

            ClearPendingResync(peer);
            _log.Event(LogTopic.Session, "Host declined the world sync request from " + peer.Name + ".");
            SendTo(peer.Connection, new ChatMessage(null, "The host declined your world sync request."));
            return true;
        }

        private Peer FindPendingResyncRequest(int playerId)
        {
            foreach (var pair in _peers)
            {
                Peer peer = pair.Value;
                if (peer.AwaitingResyncApproval && peer.PlayerId == playerId) return peer;
            }
            return null;
        }

        private static void ClearPendingResync(Peer peer)
        {
            peer.AwaitingResyncApproval = false;
            peer.PendingResyncReason = null;
            peer.PendingResyncAutomatic = false;
        }

        /// <summary>The host applies and relays; a client forwards to the host.</summary>
        public void SendCommand(long tick, ushort commandId, byte[] body)
        {
            if (Status != SessionStatus.Connected || _worldSyncSuspended) return;

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
            // Rejected across the snapshot cut: applying a suffix on one side recreates the drift.
            if (_worldSyncSuspended) return;

            // Unregistered ids are probing.
            if (_allowedCommandIds.Count > 0 && !_allowedCommandIds.Contains(command.CommandId))
            {
                Punt(from, peer, "unauthorized command id " + command.CommandId, "SimulationCommand");
                return;
            }

            // Stamp the origin from our peer table so nobody can impersonate another player.
            if (Role == SessionRole.Host && peer != null)
                command.OriginPlayerId = peer.PlayerId;

            NotifyCommand(command);
            if (Role == SessionRole.Host)
                BroadcastToAll(command, from); // relay to the other clients
        }

        /// <summary>Host only: broadcasts a state slice; ignored on a client.</summary>
        public void SendState(byte channelId, byte[] data)
        {
            if (Role != SessionRole.Host || Status != SessionStatus.Connected || _worldSyncSuspended) return;
            BroadcastToAll(new StateSnapshotMessage(channelId, data), ConnectionId.None);
        }

        private void HandleState(ConnectionId from, Peer peer, StateSnapshotMessage snapshot)
        {
            // A client sending state is impersonating the authority.
            if (Role == SessionRole.Host)
            {
                Punt(from, peer, "client sent a host-only state snapshot", "StateSnapshot");
                return;
            }
            if (_worldSyncSuspended) return;
            NotifyState(snapshot);
        }

        /// <summary>
        /// Client -> host edit in the channel's snapshot encoding. Host edits need no message: capture
        /// picks them up.
        /// </summary>
        public void SendStateEdit(byte channelId, byte[] data)
        {
            if (Role != SessionRole.Client || Status != SessionStatus.Connected || _worldSyncSuspended) return;
            SendTo(ConnectionId.Server, new StateEditMessage(LocalPlayerId, channelId, data));
        }

        private void HandleStateEdit(Peer peer, StateEditMessage edit)
        {
            // Only the host arbitrates edits; a client receiving one is a stray.
            if (Role != SessionRole.Host) return;
            if (_worldSyncSuspended) return;

            if (peer != null) edit.OriginPlayerId = peer.PlayerId; // no impersonation
            NotifyStateEdit(edit);
        }

        /// <summary>Publish the local player's camera and display-only hover outlines.</summary>
        public void SendPlayerState(float x, float y, float z, float eyeX, float eyeY, float eyeZ, float yaw,
            PlayerHoverShape[] hover = null)
        {
            if (Status != SessionStatus.Connected || _worldSyncSuspended) return;

            // 10 Hz presence: skip under backpressure rather than queue stale hovers behind city updates.
            if (_transport == null || _transport.PendingSendBytes > 16 * 1024) return;
            var message = new PlayerStateMessage(LocalPlayerId, x, y, z, eyeX, eyeY, eyeZ, yaw, hover);
            if (Role == SessionRole.Host)
                BroadcastToAll(message, ConnectionId.None);
            else
                SendTo(ConnectionId.Server, message);
        }

        private void HandlePlayerState(ConnectionId from, Peer peer, PlayerStateMessage state)
        {
            if (_worldSyncSuspended) return;
            // Same anti-impersonation stamp as commands: positions are keyed by player id.
            if (Role == SessionRole.Host && peer != null)
                state.PlayerId = peer.PlayerId;

            NotifyPlayerState(state);
            if (Role == SessionRole.Host && _transport != null && _transport.PendingSendBytes <= 16 * 1024)
                BroadcastToAll(state, from); // fan a client's position out to the others
        }
    }
}
