using System;
using CS2MultiplayerMod.Core.Networking;
using CS2MultiplayerMod.Core.Protocol.Messages;

namespace CS2MultiplayerMod.Core.Session
{
    public sealed partial class MultiplayerSession
    {
        private long _localStateRepairId;
        private long _nextStateRepairId;
        private long _localStateRepairStartedMs;
        private long? _lastStateRequestMs;
        private long _stateRepairNowMs;
        public bool StateRepairPending => _localStateRepairId != 0;
        public bool CanRequestStateRepair => Role == SessionRole.Client && Status == SessionStatus.Connected &&
            !_worldSyncSuspended && !StateRepairPending &&
            (!_lastStateRequestMs.HasValue || MonotonicClock.NowMs - _lastStateRequestMs.Value >= 5000);
        public bool IsStateRepairCurrent(long id) => id > 0 && id == _localStateRepairId && !_worldSyncSuspended;

        public bool RequestStateRepair(StateDigest[] digests)
        {
            if (!CanRequestStateRepair) return false;
            long id = ++_nextStateRepairId;
            var request = new ResyncRequestMessage(LocalPlayerId, "smart state refresh")
                { StateRequestId = id, Digests = digests };
            // Encode validation happens before marking a request pending.
            byte[] payload = _codec.Encode(request);
            _localStateRepairId = id;
            _localStateRepairStartedMs = MonotonicClock.NowMs;
            _lastStateRequestMs = _localStateRepairStartedMs;
            _transport.Send(ConnectionId.Server, payload);
            NotifyChat(null, "Smart sync requested: checking selected city state with the host.");
            return true;
        }

        private bool BeginStateRepair(ConnectionId connection, Peer peer, long id, StateDigest[] digests, long nowMs)
        {
            if (peer == null || _worldSyncSuspended || peer.ActiveStateRepairId != 0)
            {
                DeclineStateRepair(connection, id);
                return true;
            }
            peer.ActiveStateRepairId = id;
            peer.StateRepairDeadlineMs = nowMs + 30000;
            for (int i = 0; i < _observers.Count; i++)
                try { _observers[i].OnStateResyncRequested(connection, id, digests); }
                catch (Exception ex) { LogObserverError("OnStateResyncRequested", ex); }
            return true;
        }

        public void SendStateRepair(ConnectionId connection, long id, StateSnapshotMessage[] snapshots)
        {
            if (Role != SessionRole.Host || _worldSyncSuspended ||
                !_peers.TryGetValue(connection.Value, out Peer peer) || peer.ActiveStateRepairId != id) return;
            SendTo(connection, new StateRepairMessage { RequestId = id, Stage = StateRepairStage.Snapshot, Snapshots = snapshots });
        }

        public void CompleteStateRepair(long id, bool applied)
        {
            if (Role != SessionRole.Client || id != _localStateRepairId || id <= 0) return;
            _localStateRepairId = 0;
            if (!_worldSyncSuspended)
                SendTo(ConnectionId.Server, new StateRepairMessage { RequestId = id,
                    Stage = applied ? StateRepairStage.Applied : StateRepairStage.Failed });
            NotifyChat(null, applied ? "Smart sync completed for the checked city state. No world reload was needed."
                : "Smart sync could not repair the state. Requesting an authoritative world snapshot.");
        }

        public void FallBackStateRepair(ConnectionId connection, long id)
        {
            if (Role != SessionRole.Host || _worldSyncSuspended ||
                !_peers.TryGetValue(connection.Value, out Peer peer) || id <= 0 || peer.ActiveStateRepairId != id) return;
            peer.ActiveStateRepairId = 0;
            if (!peer.RateLimiter.TryAcceptRepairFallback(_stateRepairNowMs))
            {
                DeclineStateRepair(connection, id);
                SendTo(connection, new ChatMessage(null, "Smart sync fallback is cooling down after recent world sync requests. Try Sync World later."));
                return;
            }
            NotifyResyncRequested(peer.PlayerId, connection);
            SendTo(connection, new ChatMessage(null, "Smart sync is falling back to a world snapshot for this client."));
        }

        private void DeclineStateRepair(ConnectionId connection, long id)
        {
            if (id > 0) SendTo(connection, new StateRepairMessage { RequestId = id, Stage = StateRepairStage.Declined });
        }

        private void HandleStateRepair(ConnectionId from, Peer peer, StateRepairMessage message)
        {
            if (_worldSyncSuspended) return;
            if (Role == SessionRole.Host)
            {
                if (message.Stage != StateRepairStage.Applied && message.Stage != StateRepairStage.Failed)
                {
                    Punt(from, peer, "client sent host-only repair state", "StateRepair");
                    return;
                }
                if (peer == null || peer.ActiveStateRepairId != message.RequestId) return;
                if (message.Stage == StateRepairStage.Failed) FallBackStateRepair(from, message.RequestId);
                else peer.ActiveStateRepairId = 0;
                return;
            }
            if (message.Stage != StateRepairStage.Snapshot && message.Stage != StateRepairStage.Declined)
            {
                Punt(from, peer, "host sent client-only repair acknowledgement", "StateRepair");
                return;
            }
            if (message.RequestId != _localStateRepairId) return;
            if (message.Stage == StateRepairStage.Declined)
            {
                _localStateRepairId = 0;
                NotifyChat(null, "The host declined the smart sync request.");
                return;
            }
            for (int i = 0; i < _observers.Count; i++)
                try { _observers[i].OnStateRepairReceived(message); }
                catch (Exception ex) { LogObserverError("OnStateRepairReceived", ex); }
        }

        private void PumpStateRepairs(long nowMs)
        {
            _stateRepairNowMs = nowMs;
            if (_worldSyncSuspended) { ClearStateRepairs(); return; }
            if (Role == SessionRole.Host)
            {
                System.Collections.Generic.List<Peer> expired = null;
                foreach (var peer in _peers.Values)
                    if (peer.ActiveStateRepairId != 0 && nowMs >= peer.StateRepairDeadlineMs)
                        (expired ?? (expired = new System.Collections.Generic.List<Peer>())).Add(peer);
                if (expired != null)
                    foreach (var peer in expired)
                        FallBackStateRepair(peer.Connection, peer.ActiveStateRepairId);
            }
            else if (StateRepairPending && MonotonicClock.NowMs - _localStateRepairStartedMs >= 120000)
            {
                _localStateRepairId = 0;
                NotifyChat(null, "Smart sync timed out. Try again or request Sync World.");
            }
        }

        private void ClearStateRepairs()
        {
            _localStateRepairId = 0;
            foreach (var peer in _peers.Values)
            {
                peer.ActiveStateRepairId = 0;
                if (peer.PendingStateRequestId > 0) ClearPendingResync(peer);
            }
        }
    }
}
