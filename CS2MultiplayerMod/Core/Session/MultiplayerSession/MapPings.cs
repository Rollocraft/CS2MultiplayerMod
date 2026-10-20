using System;
using CS2MultiplayerMod.Core.Networking;
using CS2MultiplayerMod.Core.Protocol.Messages;

namespace CS2MultiplayerMod.Core.Session
{
    public sealed partial class MultiplayerSession
    {
        public const long MapPingCooldownMs = 1000;
        private long? _lastLocalPingMs;

        public bool CanSendMapPing => Status == SessionStatus.Connected && !_worldSyncSuspended &&
            (!_lastLocalPingMs.HasValue || MonotonicClock.NowMs - _lastLocalPingMs.Value >= MapPingCooldownMs);

        public bool SendMapPing(float x, float y, float z)
        {
            if (!CanSendMapPing) return false;
            MapPingMessage.ValidatePosition(x, y, z);
            _lastLocalPingMs = MonotonicClock.NowMs;
            var ping = new MapPingMessage { PlayerId = LocalPlayerId, PlayerName = LocalPlayerName, X = x, Y = y, Z = z };
            if (Role == SessionRole.Host)
            {
                NotifyMapPing(ping);
                BroadcastToAll(ping, ConnectionId.None);
            }
            else SendTo(ConnectionId.Server, ping);
            return true;
        }

        private void HandleMapPing(ConnectionId from, Peer peer, MapPingMessage ping, long nowMs)
        {
            if (_worldSyncSuspended) return;
            if (Role == SessionRole.Host)
            {
                if (peer == null || !peer.Handshaked) return;
                if (peer.LastMapPingMs.HasValue && nowMs - peer.LastMapPingMs.Value < MapPingCooldownMs) return;
                peer.LastMapPingMs = nowMs;
                ping.PlayerId = peer.PlayerId;
                ping.PlayerName = peer.Name;
                // Echo the accepted ping to its sender too: everyone sees the same accepted markers.
                BroadcastToAll(ping, ConnectionId.None);
            }
            NotifyMapPing(ping);
        }

        private void NotifyMapPing(MapPingMessage ping)
        {
            for (int i = 0; i < _observers.Count; i++)
                try { _observers[i].OnMapPingReceived(ping); }
                catch (Exception ex) { LogObserverError("OnMapPingReceived", ex); }
        }
    }
}
