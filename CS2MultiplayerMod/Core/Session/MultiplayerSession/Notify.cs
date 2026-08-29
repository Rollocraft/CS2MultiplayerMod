using System;
using CS2MultiplayerMod.Core.Networking;
using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Protocol.Messages;

namespace CS2MultiplayerMod.Core.Session
{
    public sealed partial class MultiplayerSession
    {
        private void SendTo(ConnectionId connection, INetMessage message)
        {
            if (_transport == null) return;
            _transport.Send(connection, _codec.Encode(message));
        }

        private void BroadcastToAll(INetMessage message, ConnectionId exclude)
        {
            if (_transport == null) return;
            byte[] payload = _codec.Encode(message);
            foreach (var pair in _peers)
            {
                Peer peer = pair.Value;
                if (!peer.Handshaked) continue;
                if (peer.Connection == exclude) continue;
                _transport.Send(peer.Connection, payload);
            }
        }

        private void SetStatus(SessionStatus status, string detail)
        {
            Status = status;
            _log.Info("Session status: " + status + " (" + detail + ")");
            for (int i = 0; i < _observers.Count; i++)
                try { _observers[i].OnStatusChanged(status, detail); }
                catch (Exception ex) { LogObserverError("OnStatusChanged", ex); }
        }

        private void Fault(string message)
        {
            _log.Error("Session fault: " + message);
            SetStatus(SessionStatus.Faulted, message);
            for (int i = 0; i < _observers.Count; i++)
                try { _observers[i].OnError(message); }
                catch (Exception ex) { LogObserverError("OnError", ex); }
            Stop();
        }

        private void NotifyPeerJoined(Peer peer)
        {
            for (int i = 0; i < _observers.Count; i++)
                try { _observers[i].OnPeerJoined(peer); }
                catch (Exception ex) { LogObserverError("OnPeerJoined", ex); }
        }

        private void NotifyPeerLeft(Peer peer, string reason)
        {
            for (int i = 0; i < _observers.Count; i++)
                try { _observers[i].OnPeerLeft(peer, reason); }
                catch (Exception ex) { LogObserverError("OnPeerLeft", ex); }
        }

        internal void NotifyChat(string sender, string text)
        {
            for (int i = 0; i < _observers.Count; i++)
                try { _observers[i].OnChatReceived(sender, text); }
                catch (Exception ex) { LogObserverError("OnChatReceived", ex); }
        }

        private void NotifyCommand(SimulationCommandMessage command)
        {
            for (int i = 0; i < _observers.Count; i++)
                try { _observers[i].OnCommandReceived(command); }
                catch (Exception ex) { LogObserverError("OnCommandReceived", ex); }
        }

        private void NotifyState(StateSnapshotMessage snapshot)
        {
            for (int i = 0; i < _observers.Count; i++)
                try { _observers[i].OnStateReceived(snapshot); }
                catch (Exception ex) { LogObserverError("OnStateReceived", ex); }
        }

        private void NotifyStateEdit(StateEditMessage edit)
        {
            for (int i = 0; i < _observers.Count; i++)
                try { _observers[i].OnStateEditReceived(edit); }
                catch (Exception ex) { LogObserverError("OnStateEditReceived", ex); }
        }

        private void NotifyResyncRequested(int playerId, ConnectionId connection)
        {
            for (int i = 0; i < _observers.Count; i++)
                try { _observers[i].OnResyncRequested(playerId, connection); }
                catch (Exception ex) { LogObserverError("OnResyncRequested", ex); }
        }

        private void NotifyPlayerState(PlayerStateMessage state)
        {
            for (int i = 0; i < _observers.Count; i++)
                try { _observers[i].OnPlayerStateReceived(state); }
                catch (Exception ex) { LogObserverError("OnPlayerStateReceived", ex); }
        }

        private void NotifyBlob(string channel, long transferId, byte[] data)
        {
            _log.Info("Blob '" + channel + "' transfer " + transferId + " received (" +
                      data.Length + " bytes).");
            for (int i = 0; i < _observers.Count; i++)
                try { _observers[i].OnBlobReceived(channel, transferId, data); }
                catch (Exception ex) { LogObserverError("OnBlobReceived", ex); }
        }

        private void NotifyWorldSync(WorldSyncStage stage, long epoch, float resumeSpeed,
            ConnectionId connection)
        {
            for (int i = 0; i < _observers.Count; i++)
                try { _observers[i].OnWorldSyncControl(stage, epoch, resumeSpeed, connection); }
                catch (Exception ex) { LogObserverError("OnWorldSyncControl", ex); }
        }

        private void LogObserverError(string callback, Exception ex)
        {
            _log.Error("Observer crashed in " + callback + " (session continues): " + ex);
        }

    }
}
