using System;
using CS2MultiplayerMod.Core.Diagnostics;
using Steamworks;

namespace CS2MultiplayerMod.Core.Networking.Steam
{
    // Binding, accepting, closing and describing Steam connections.
    public sealed partial class SteamRelayTransport
    {
        // ---- connection lifecycle -------------------------------------------------

        private Endpoint Bind(ConnectionId id, HSteamNetConnection handle, ulong steamId)
        {
            var endpoint = new Endpoint(id, handle, steamId);
            lock (_gate)
            {
                _byId[id.Value] = endpoint;
                _byHandle[handle.m_HSteamNetConnection] = endpoint;
            }
            ApplySendRate(endpoint, SendRateStartBytesPerSecond);
            return endpoint;
        }

        private Endpoint Find(uint handle)
        {
            lock (_gate)
            {
                return _byHandle.TryGetValue(handle, out Endpoint found) ? found : null;
            }
        }

        private void Drop(Endpoint endpoint)
        {
            lock (_gate)
            {
                _byId.Remove(endpoint.Id.Value);
                _byHandle.Remove(endpoint.Handle.m_HSteamNetConnection);
            }
        }

        private void OnConnectionStatusChanged(SteamNetConnectionStatusChangedCallback_t evt)
        {
            // The callback is process-wide; connections we did not open are not ours.
            if (!_active) return;

            Endpoint endpoint = Find(evt.m_hConn.m_HSteamNetConnection);
            ESteamNetworkingConnectionState state = evt.m_info.m_eState;

            switch (state)
            {
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting:
                    if (endpoint == null) AcceptIncoming(evt);
                    break;

                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected:
                    if (endpoint != null && !endpoint.Announced)
                    {
                        endpoint.Announced = true;
                        _log.Detail(LogTopic.Transport, "Steam relay connection " + endpoint.Id +
                            " established with " + endpoint.RemoteAddress + ".");
                        Enqueue(TransportEvent.Connected(endpoint.Id));
                    }
                    break;

                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer:
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally:
                    if (endpoint != null) Close(endpoint, DescribeClose(evt), linger: false);
                    break;
            }
        }

        private void AcceptIncoming(SteamNetConnectionStatusChangedCallback_t evt)
        {
            // Only inbound connections carry our listen socket; a client's own dial also reports Connecting.
            if (!_isHost) return;
            if (evt.m_info.m_hListenSocket != _listenSocket) return;

            ulong steamId = evt.m_info.m_identityRemote.GetSteamID64();

            int open;
            lock (_gate) { open = _byId.Count; }
            if (open >= MaxConnections)
            {
                _log.Warn(LogTopic.Transport, "Refused Steam relay connection from " + steamId +
                    ": too many open connections.");
                SteamNetworkingSockets.CloseConnection(evt.m_hConn, 0, "too many connections", false);
                return;
            }

            EResult accepted = SteamNetworkingSockets.AcceptConnection(evt.m_hConn);
            if (accepted != EResult.k_EResultOK)
            {
                _log.Warn(LogTopic.Transport, "Could not accept Steam relay connection from " +
                    steamId + ": " + accepted + ".");
                SteamNetworkingSockets.CloseConnection(evt.m_hConn, 0, "accept failed", false);
                return;
            }

            var id = new ConnectionId(_nextConnectionId++);
            Endpoint endpoint = Bind(id, evt.m_hConn, steamId);
            if (!SteamNetworkingSockets.SetConnectionPollGroup(evt.m_hConn, _pollGroup))
            {
                // Outside the poll group the connection is deaf; refuse it.
                _log.Warn(LogTopic.Transport, "Could not add Steam relay connection " + id +
                    " from " + steamId + " to the poll group; refusing it.");
                Close(endpoint, "poll group rejected the connection", linger: false);
                return;
            }

            _log.Detail(LogTopic.Transport, "Accepted Steam relay connection " + id + " from " +
                steamId + ".");
            // Connected only on the Connected state, never mid-negotiation.
        }

        private static string DescribeClose(SteamNetConnectionStatusChangedCallback_t evt)
        {
            string debug = evt.m_info.m_szEndDebug;
            bool byPeer = evt.m_info.m_eState ==
                          ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer;
            string prefix = byPeer ? "closed by peer" : "connection problem";
            return string.IsNullOrEmpty(debug) ? prefix : prefix + ": " + debug;
        }

        /// <summary>
        /// Steam's account of a connection, for a failed send: closing from the send path unhooks the
        /// endpoint, so the status callback's reason would be lost.
        /// </summary>
        private static string DescribeConnection(Endpoint endpoint)
        {
            try
            {
                if (!SteamNetworkingSockets.GetConnectionInfo(endpoint.Handle, out SteamNetConnectionInfo_t info))
                    return "Steam no longer knows the connection.";
                return "Steam reports state=" + info.m_eState + " endReason=" + info.m_eEndReason +
                       " \"" + info.m_szEndDebug + "\".";
            }
            catch (Exception ex)
            {
                return "Steam could not describe the connection (" + ex.Message + ").";
            }
        }

        private void Close(Endpoint endpoint, string reason, bool linger)
        {
            Drop(endpoint);
            try { SteamNetworkingSockets.CloseConnection(endpoint.Handle, 0, reason, linger); }
            catch (Exception) { /* already gone */ }
            if (endpoint.Announced || !_isHost)
                Enqueue(TransportEvent.Disconnected(endpoint.Id, reason));
        }

        private void Enqueue(TransportEvent evt) => _events.Enqueue(evt);
    }
}
