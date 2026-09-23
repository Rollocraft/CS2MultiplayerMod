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
        private void HandleHandshakeChallenge(ConnectionId connection, HandshakeChallenge challenge)
        {
            if (Role != SessionRole.Client || _challengeAnswered) return;

            if (challenge.ProtocolVersion != ProtocolConstants.ProtocolVersion)
            {
                Fault("Protocol mismatch: host v" + challenge.ProtocolVersion +
                      ", this build v" + ProtocolConstants.ProtocolVersion + ". Update so both sides match.");
                return;
            }

            if (challenge.PasswordRequired && string.IsNullOrEmpty(_config.Password))
            {
                Fault("This server requires a password.");
                return;
            }

            _challengeAnswered = true;
            _log.Detail(LogTopic.Session, "Host challenge received (protocol v" +
                challenge.ProtocolVersion + ", password " +
                (challenge.PasswordRequired ? "required" : "not required") +
                "); sending handshake as '" + LocalPlayerName + "'.");
            byte[] binding = _transport.GetChannelBinding(ConnectionId.Server);
            byte[] proof = HandshakeAuth.ComputeProof(_config.Password, challenge.Nonce, binding);
            SendTo(connection, new HandshakeRequest(
                ProtocolConstants.ProtocolVersion, _config.ModVersion, _config.GameVersion,
                LocalPlayerName, proof, _config.DlcList));
        }

        private void HandleHandshakeRequest(ConnectionId connection, Peer peer, HandshakeRequest request, long nowUnixMs)
        {
            if (Role != SessionRole.Host || peer == null) return;

            // One handshake per connection: a second would mint a new player id.
            if (peer.Handshaked)
            {
                Punt(connection, peer, "repeated handshake", "HandshakeRequest");
                return;
            }

            _log.Detail(LogTopic.Session, "Handshake request from " + connection + " (" +
                (peer.RemoteAddress ?? "?") + "): name='" +
                WireGuard.SanitizePlayerName(request.PlayerName) + "' protocol=" +
                request.ProtocolVersion + " mod=" + (request.ModVersion ?? "?") + " game=" +
                (request.GameVersion ?? "?") + " dlcs=[" +
                string.Join(", ", request.DlcList ?? Array.Empty<string>()) + "]" +
                " passwordProof=" +
                (request.PasswordProof != null && request.PasswordProof.Length > 0 ? "present" : "missing") +
                ".");

            if (request.ProtocolVersion != ProtocolConstants.ProtocolVersion)
            {
                Reject(connection, "Protocol mismatch: host v" + ProtocolConstants.ProtocolVersion +
                                   ", client v" + request.ProtocolVersion + ".");
                return;
            }

            // Password first, so an unauthenticated prober cannot enumerate the host's setup from the reject
            // reasons below. The proof is bound to this nonce and TLS certificate, compared in fixed time.
            if (PasswordProtected)
            {
                byte[] binding = _transport.GetChannelBinding(connection);
                byte[] expected = HandshakeAuth.ComputeProof(_config.Password, peer.ChallengeNonce, binding);
                if (!HandshakeAuth.FixedTimeEquals(expected, request.PasswordProof))
                {
                    bool nowBanned = _failedAuth.RecordFailure(peer.RemoteAddress, nowUnixMs);
                    _log.Warn(LogTopic.Session, "Auth failure from " + connection + " (" +
                        (peer.RemoteAddress ?? "?") + ")" +
                        (nowBanned ? " - address temporarily banned." : "."));
                    Reject(connection, "Incorrect password.");
                    return;
                }
                _failedAuth.RecordSuccess(peer.RemoteAddress);
            }
            peer.ChallengeNonce = null; // single use

            // A different mod or game build can silently diverge in layouts, prefab names and behaviour.
            if (!string.IsNullOrEmpty(_config.ModVersion) &&
                !string.Equals(_config.ModVersion, request.ModVersion, StringComparison.Ordinal))
            {
                string mismatch = "Mod version mismatch: host " + _config.ModVersion +
                                  ", client " + (request.ModVersion ?? "?") + ".";
                if (!_config.IgnoreModCompatibilityChecks)
                {
                    Reject(connection, mismatch);
                    return;
                }

                _log.Warn(LogTopic.Session, mismatch +
                    " The host chose to ignore this check at their own risk; " +
                    "protocol compatibility is still enforced.");
            }

            if (!string.IsNullOrEmpty(_config.GameVersion) &&
                !string.Equals(_config.GameVersion, request.GameVersion, StringComparison.Ordinal))
            {
                Reject(connection, "Game version mismatch: host " + _config.GameVersion +
                                   ", client " + (request.GameVersion ?? "?") + ".");
                return;
            }

            // DLC preconditions (idea from CS2M): differing ownership means differing prefab catalogues.
            // Empty is a real set, not a wildcard.
            string dlcMismatch = DescribeDlcMismatch(_config.DlcList, request.DlcList);
            if (dlcMismatch != null)
            {
                Reject(connection, "DLC mismatch - " + dlcMismatch);
                return;
            }

            // Player cap (host counts as one seat).
            int seated = 1;
            foreach (var pair in _peers)
                if (pair.Value.Handshaked) seated++;
            if (seated >= _config.MaxPlayers)
            {
                Reject(connection, "Server is full (" + _config.MaxPlayers + " players).");
                return;
            }

            // FinalizeJoin de-duplicates names with a "(2)" suffix rather than rejecting.
            peer.Name = WireGuard.SanitizePlayerName(request.PlayerName);
            peer.ModVersion = request.ModVersion;
            peer.GameVersion = request.GameVersion;

            // Manual approval: the id is assigned now for the host UI, but the peer stays un-Handshaked until
            // FinalizeJoin.
            bool friendAutoApproved = false;
            if (_config.RequireJoinApproval && _config.AutoApprovePlatformFriends)
            {
                friendAutoApproved = _transport is IPlatformFriendLookup friendLookup &&
                                     friendLookup.IsPlatformFriend(connection);
            }

            if (_config.RequireJoinApproval && !friendAutoApproved)
            {
                peer.PlayerId = _nextPlayerId++;
                peer.AwaitingApproval = true;
                SendTo(connection, new HandshakePendingMessage());
                _log.Detail(LogTopic.Session, "Join from " + peer +
                    " passed the checks; awaiting host approval.");
                return;
            }

            if (friendAutoApproved)
                _log.Event(LogTopic.Session, "Auto-approved Steam friend " + peer + ".");

            FinalizeJoin(connection, peer, nowUnixMs);
        }

        /// <summary>
        /// Completes a join (id, unique name, authenticated, announced) for both the immediate and the
        /// approved path.
        /// </summary>
        private void FinalizeJoin(ConnectionId connection, Peer peer, long nowUnixMs)
        {
            if (peer.PlayerId == 0) peer.PlayerId = _nextPlayerId++;
            peer.Name = UniquePlayerName(peer.Name);
            // An approved peer may have waited longer than the timeout; its liveness starts at admission.
            peer.LastSeenUnixMs = nowUnixMs;
            peer.AwaitingApproval = false;
            peer.Handshaked = true;

            SendTo(connection, HandshakeResponse.Accept(peer.PlayerId, _config.SimulationSync));
            _log.Event(LogTopic.Session, "Accepted " + peer + ": mod " +
                (string.IsNullOrEmpty(peer.ModVersion) ? "?" : peer.ModVersion) + ", game " +
                (string.IsNullOrEmpty(peer.GameVersion) ? "?" : peer.GameVersion) + ".");
            NotifyPeerJoined(peer);

            // Clients get no OnPeerJoined for each other; this line is how they learn of joins.
            string notice = peer.Name + " joined.";
            BroadcastToAll(new ChatMessage(null, notice), ConnectionId.None);
            NotifyChat(null, notice);
        }

        /// <summary>
        /// Host only: admits a waiting join, re-checking the seat cap (declined if full). False when no
        /// pending join has this id.
        /// </summary>
        public bool ApproveJoin(int playerId, long nowUnixMs)
        {
            if (Role != SessionRole.Host) return false;
            Peer peer = FindPendingJoin(playerId);
            if (peer == null) return false;

            int seated = 1;
            foreach (var pair in _peers)
                if (pair.Value.Handshaked) seated++;
            if (seated >= _config.MaxPlayers)
            {
                Reject(peer.Connection, "Server is full (" + _config.MaxPlayers + " players).");
                return true;
            }

            FinalizeJoin(peer.Connection, peer, nowUnixMs);
            return true;
        }

        /// <summary>Host only: refuses a waiting join with a reason. False when no pending join has this id.</summary>
        public bool DeclineJoin(int playerId)
        {
            if (Role != SessionRole.Host) return false;
            Peer peer = FindPendingJoin(playerId);
            if (peer == null) return false;
            Reject(peer.Connection, "The host declined your request to join.");
            return true;
        }

        private Peer FindPendingJoin(int playerId)
        {
            foreach (var pair in _peers)
            {
                Peer peer = pair.Value;
                if (peer.AwaitingApproval && peer.PlayerId == playerId) return peer;
            }
            return null;
        }

        private void HandleHandshakePending(ConnectionId connection, Peer peer)
        {
            // Host -> client only; anyone else is disconnected.
            if (Role != SessionRole.Client)
            {
                Punt(connection, peer, "sent a host-only handshake-pending", "HandshakePending");
                return;
            }
            if (_awaitingHostApproval) return;
            _awaitingHostApproval = true;
            _log.Detail(LogTopic.Session,
                "The host received the join request; waiting for the host to approve it.");
        }

        /// <summary>Null when compatible, otherwise a summary naming exactly which DLCs differ.</summary>
        internal static string DescribeDlcMismatch(string[] hostDlcs, string[] clientDlcs)
        {
            if (hostDlcs == null) hostDlcs = Array.Empty<string>();
            if (clientDlcs == null) clientDlcs = Array.Empty<string>();

            var host = new HashSet<string>(hostDlcs, StringComparer.Ordinal);
            var client = new HashSet<string>(clientDlcs, StringComparer.Ordinal);
            if (host.SetEquals(client)) return null;

            var clientMissing = new List<string>();
            foreach (string dlc in hostDlcs)
                if (!client.Contains(dlc)) clientMissing.Add(dlc);

            var hostMissing = new List<string>();
            foreach (string dlc in clientDlcs)
                if (!host.Contains(dlc)) hostMissing.Add(dlc);

            var sb = new System.Text.StringBuilder();
            if (clientMissing.Count > 0)
                sb.Append("you are missing: ").Append(string.Join(", ", clientMissing.ToArray()));
            if (hostMissing.Count > 0)
            {
                if (sb.Length > 0) sb.Append("; ");
                sb.Append("the host is missing: ").Append(string.Join(", ", hostMissing.ToArray()));
            }
            sb.Append(". Both players need the same DLCs enabled.");
            return sb.ToString();
        }

        /// <summary>Suffixes " (2)", " (3)", ... to a taken name.</summary>
        private string UniquePlayerName(string name)
        {
            string candidate = name;
            int suffix = 2;
            while (NameTaken(candidate))
                candidate = name + " (" + suffix++ + ")";
            return candidate;
        }

        private bool NameTaken(string candidate)
        {
            if (string.Equals(candidate, LocalPlayerName, StringComparison.OrdinalIgnoreCase)) return true;
            foreach (var pair in _peers)
            {
                Peer other = pair.Value;
                if (other.Handshaked && string.Equals(candidate, other.Name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private void Reject(ConnectionId connection, string reason)
        {
            SendTo(connection, HandshakeResponse.Reject(reason));
            // Flush the reason first, or the client only sees "remote closed".
            _transport.DisconnectAfterFlush(connection);
            _peers.Remove(connection.Value);
            _log.Warn(LogTopic.Session, "Rejected " + connection + ": " + reason);
        }

        private void HandleHandshakeResponse(Peer peer, HandshakeResponse response)
        {
            if (Role != SessionRole.Client) return;

            // Accepted below or rejected into a fault; either way the wait is over.
            _awaitingHostApproval = false;

            if (!response.Accepted)
            {
                Fault("Host rejected join: " + response.Reason);
                return;
            }

            LocalPlayerId = response.AssignedPlayerId;
            _hostSimulationSync = response.SimulationSync;
            if (peer != null) peer.Handshaked = true;
            _log.Event(LogTopic.Session, "Join accepted by host; assigned player #" + LocalPlayerId +
                ", simulation sync " + (_hostSimulationSync ? "on" : "off") +
                ". Waiting for host world stream.");
            SetStatus(SessionStatus.Connected, "Joined as player #" + LocalPlayerId);
            if (peer != null) NotifyPeerJoined(peer);
        }
    }
}
