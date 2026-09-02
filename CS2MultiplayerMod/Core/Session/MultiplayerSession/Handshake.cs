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

            // A connection only gets one handshake; re-handshaking would mint a fresh
            // player id mid-session and confuse every origin check built on it.
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

            // Password FIRST, before any build-detail check: the mod/game/DLC reject
            // reasons below describe the host's setup, and an unauthenticated prober
            // must not be able to enumerate it. Challenge-response, in fixed time. The
            // expected proof is bound to this connection's nonce and TLS certificate,
            // so neither replaying an old handshake nor relaying through a
            // man-in-the-middle helps.
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

            // Build compatibility: a different mod build (or game build) means command
            // layouts, prefab names and simulation behavior can silently diverge.
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

            // DLC preconditions (idea from CS2M): differing DLC ownership means
            // differing prefab catalogues — a placement of a DLC building would desync
            // or crash the other side. Both lists are canonical (sorted, client-side
            // content excluded). Empty is a valid set (no content DLC), not a wildcard:
            // accepting a client with six DLCs into a zero-DLC host still desynchronizes.
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

            // Names key chat lines and join/leave notices, so two players with the same
            // name would be indistinguishable everywhere; FinalizeJoin de-duplicates by
            // suffixing "(2)" rather than rejecting, keeping the join frictionless.
            peer.Name = WireGuard.SanitizePlayerName(request.PlayerName);

            // Optional manual gate: hold the join and let the host admit it by hand. The
            // player is given an id now so the host UI can reference this exact request,
            // but stays un-Handshaked (no seat, no traffic) until FinalizeJoin runs.
            if (_config.RequireJoinApproval)
            {
                peer.PlayerId = _nextPlayerId++;
                peer.AwaitingApproval = true;
                SendTo(connection, new HandshakePendingMessage());
                _log.Detail(LogTopic.Session, "Join from " + peer +
                    " passed the checks; awaiting host approval.");
                return;
            }

            FinalizeJoin(connection, peer, nowUnixMs);
        }

        /// <summary>
        /// Complete a join: reserve an id if one was not already assigned, make the name
        /// unique, mark the peer authenticated, and announce the arrival to everyone. Shared
        /// by the immediate-accept path and the host's manual approval.
        /// </summary>
        private void FinalizeJoin(ConnectionId connection, Peer peer, long nowUnixMs)
        {
            if (peer.PlayerId == 0) peer.PlayerId = _nextPlayerId++;
            peer.Name = UniquePlayerName(peer.Name);
            // A manually approved peer may have been silent for longer than the normal
            // post-handshake timeout while waiting at the approval gate. Start its live
            // liveness window at admission, before the next reaper pass can classify the
            // old handshake timestamp as an immediate timeout.
            peer.LastSeenUnixMs = nowUnixMs;
            peer.AwaitingApproval = false;
            peer.Handshaked = true;

            SendTo(connection, HandshakeResponse.Accept(peer.PlayerId));
            _log.Event(LogTopic.Session, "Accepted " + peer + ".");
            NotifyPeerJoined(peer);

            // Surface a "joined" system line to everyone — the clients over the wire and
            // the host locally — so every machine's UI shows the same notice. Clients do
            // not get OnPeerJoined for each other, so this line is how they learn of joins.
            string notice = peer.Name + " joined.";
            BroadcastToAll(new ChatMessage(null, notice), ConnectionId.None);
            NotifyChat(null, notice);
        }

        /// <summary>
        /// Host-only: admit a join that is waiting for manual approval. The seat cap is
        /// re-checked at admit time (players may have joined since the request arrived); if
        /// the session filled up in between, the waiting player is declined with that reason
        /// instead. Returns false when no pending join carries this id.
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

        /// <summary>
        /// Host-only: refuse a join that is waiting for manual approval, delivering a clear
        /// reason before the socket closes. Returns false when no pending join carries this id.
        /// </summary>
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
            // Host -> client only. Anyone else sending it (a client to the host) is speaking
            // out of turn and is disconnected.
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

        /// <summary>
        /// Compare host and client DLC sets. Returns null when compatible; otherwise
        /// returns a human-readable summary
        /// naming exactly what differs - so the rejected player knows what to change
        /// instead of staring at a generic "incompatible" error.
        /// </summary>
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

        /// <summary>
        /// Make a joining player's name unique among the host and current peers by
        /// suffixing " (2)", " (3)", ... when taken.
        /// </summary>
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
            // Deliver the rejection reason before hanging up — an immediate disconnect would
            // race the asynchronous send and the client would only see "remote closed".
            _transport.DisconnectAfterFlush(connection);
            _peers.Remove(connection.Value);
            _log.Warn(LogTopic.Session, "Rejected " + connection + ": " + reason);
        }

        private void HandleHandshakeResponse(Peer peer, HandshakeResponse response)
        {
            if (Role != SessionRole.Client) return;

            // The wait is over either way — accepted below, or rejected (which includes a
            // manual decline) into a fault.
            _awaitingHostApproval = false;

            if (!response.Accepted)
            {
                Fault("Host rejected join: " + response.Reason);
                return;
            }

            LocalPlayerId = response.AssignedPlayerId;
            if (peer != null) peer.Handshaked = true;
            _log.Event(LogTopic.Session, "Join accepted by host; assigned player #" + LocalPlayerId +
                ". Waiting for host world stream.");
            SetStatus(SessionStatus.Connected, "Joined as player #" + LocalPlayerId);
            if (peer != null) NotifyPeerJoined(peer);
        }

    }
}
