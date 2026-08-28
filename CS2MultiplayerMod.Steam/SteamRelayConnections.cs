using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Networking.Tcp;
using CS2MultiplayerMod.Core.Protocol;
using Steamworks;

namespace CS2MultiplayerMod.Core.Networking.Steam
{
    // Connection bookkeeping: binding a Steam connection handle to a connection id, accepting an
    // incoming one, and closing and describing them.
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
                Endpoint found;
                return _byHandle.TryGetValue(handle, out found) ? found : null;
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
            // The callback is process-wide: it also carries connections belonging to the
            // game itself or to another transport instance. Anything we did not open is
            // not ours to answer.
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
                        _log.Info("Steam relay connection " + endpoint.Id + " established with " +
                                  endpoint.RemoteAddress + ".");
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
            // Only inbound connections carry our listen socket; a client's own outbound
            // dial reports Connecting too and must not be accepted.
            if (!_isHost) return;
            if (evt.m_info.m_hListenSocket != _listenSocket) return;

            ulong steamId = evt.m_info.m_identityRemote.GetSteamID64();

            int open;
            lock (_gate) { open = _byId.Count; }
            if (open >= MaxConnections)
            {
                _log.Warn("Refused Steam relay connection from " + steamId + ": too many open connections.");
                SteamNetworkingSockets.CloseConnection(evt.m_hConn, 0, "too many connections", false);
                return;
            }

            EResult accepted = SteamNetworkingSockets.AcceptConnection(evt.m_hConn);
            if (accepted != EResult.k_EResultOK)
            {
                _log.Warn("Could not accept Steam relay connection from " + steamId + ": " + accepted + ".");
                SteamNetworkingSockets.CloseConnection(evt.m_hConn, 0, "accept failed", false);
                return;
            }

            var id = new ConnectionId(_nextConnectionId++);
            Endpoint endpoint = Bind(id, evt.m_hConn, steamId);
            if (!SteamNetworkingSockets.SetConnectionPollGroup(evt.m_hConn, _pollGroup))
            {
                // Outside the poll group this connection is deaf; better to refuse it than
                // to leave a peer that handshakes and then goes quiet forever.
                _log.Warn("Could not add Steam relay connection " + id + " from " + steamId +
                          " to the poll group; refusing it.");
                Close(endpoint, "poll group rejected the connection", linger: false);
                return;
            }

            _log.Info("Accepted Steam relay connection " + id + " from " + steamId + ".");
            // Connected is announced on the Connected state, so the session never talks to
            // a connection that is still negotiating.
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
        /// Steam's own account of a connection. Worth the call on a failed send: closing
        /// from the send path unhooks the endpoint, so the status callback that would have
        /// carried this reason arrives to nothing and the log ends with no cause at all.
        /// </summary>
        private static string DescribeConnection(Endpoint endpoint)
        {
            try
            {
                SteamNetConnectionInfo_t info;
                if (!SteamNetworkingSockets.GetConnectionInfo(endpoint.Handle, out info))
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

        private void Enqueue(TransportEvent evt)
        {
            _events.Enqueue(evt);
        }
    }
}
