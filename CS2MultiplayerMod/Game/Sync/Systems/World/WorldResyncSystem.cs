using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Networking;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Infrastructure;
using CS2MultiplayerMod.Game.Sync.Systems.Net;
using Game;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// World replacement as a distributed transaction: Begin, clients quiesce, native work drains,
    /// save, epoch-tagged map, clients load, Resume. A second save never overtakes an active stream.
    /// </summary>
    public partial class WorldResyncSystem : GameSystemBase
    {
        private const long QuiesceTimeoutMs = 20000;
        private const long LoadTimeoutMs = 180000;
        private const int RequiredCleanFrames = 2;

        private enum RecoveryState : byte
        {
            Idle,
            WaitingForQuiescence,
            Saving,
            WaitingForLoaded,
        }

        private struct ControlEvent
        {
            public WorldSyncStage Stage;
            public long Epoch;
            public ConnectionId Connection;
        }

        private struct RecoveryRequest
        {
            public ConnectionId Connection;
            public bool IsJoin;
        }

        private readonly ConcurrentQueue<RecoveryRequest> _requests =
            new ConcurrentQueue<RecoveryRequest>();
        private readonly ConcurrentQueue<ControlEvent> _controls =
            new ConcurrentQueue<ControlEvent>();
        private readonly ConcurrentQueue<ConnectionId> _leaves =
            new ConcurrentQueue<ConnectionId>();
        private readonly List<ConnectionId> _participants = new List<ConnectionId>();
        private readonly HashSet<int> _quiesced = new HashSet<int>();
        private readonly HashSet<int> _loaded = new HashSet<int>();
        private readonly HashSet<int> _pendingJoinRequests = new HashSet<int>();
        private readonly List<ConnectionId> _joiningParticipants = new List<ConnectionId>();
        private readonly List<ConnectionId> _snapshotTargets = new List<ConnectionId>();

        private NetSyncSystem _netSync;
        private Observer _observer;
        private Task<BlobSource> _saveTask;
        private CancellationTokenSource _saveCancellation;
        private RecoveryState _state;
        private bool _recoveryRequested;
        private bool _rerunRequested;
        private bool _fullSnapshotRequested;
        private long _epochCounter;
        private long _epoch;
        private long _deadlineMs;
        private long _saveStartMs;
        private long _lastTransferProgress;
        private float _resumeSpeed;
        private int _cleanFrames;

        protected override void OnCreate()
        {
            base.OnCreate();
            _netSync = World.GetOrCreateSystemManaged<NetSyncSystem>();

            _observer = SyncObserverBinding.Bind(
                () => new Observer(_requests, _controls, _leaves));
        }

        protected override void OnDestroy()
        {
            CancelSave();
            SyncObserverBinding.Unbind(_observer);
            MultiplayerService service = Mod.Service;
            if (_state != RecoveryState.Idle && service != null &&
                service.Session.Role == SessionRole.Host)
                AbortEpoch(service, "world-sync system was destroyed");
            base.OnDestroy();
        }

        protected override void OnUpdate()
        {
            using (Diagnostics.SyncProfiler.Measure("WorldResync"))
            {
                MultiplayerService service = Mod.Service;
                if (service == null) return;
                MultiplayerSession session = service.Session;
                if (session.Role != SessionRole.Host || session.Status != SessionStatus.Connected)
                {
                    ResetInactiveState();
                    return;
                }

                long now = service.NowMs;
                DrainObserverEvents(session);

                if (_state == RecoveryState.Idle)
                {
                    if (_recoveryRequested) StartEpoch(service, session, now);
                    return;
                }

                PruneDisconnectedParticipants(session);
                if (_participants.Count == 0)
                {
                    AbortEpoch(service, "all snapshot participants disconnected");
                    return;
                }

                switch (_state)
                {
                    case RecoveryState.WaitingForQuiescence:
                        PumpQuiescence(service, session, now);
                        break;
                    case RecoveryState.Saving:
                        PumpSave(service, session, now);
                        break;
                    case RecoveryState.WaitingForLoaded:
                        PumpLoaded(service, session, now);
                        break;
                }
            }
        }

        /// <summary>Survives session restarts: forget the previous session's observers and labels.</summary>
        private void ResetInactiveState()
        {
            while (_requests.TryDequeue(out RecoveryRequest request)) { }
            while (_controls.TryDequeue(out ControlEvent control)) { }
            while (_leaves.TryDequeue(out ConnectionId left)) { }

            _state = RecoveryState.Idle;
            CancelSave();
            _participants.Clear();
            _quiesced.Clear();
            _loaded.Clear();
            _pendingJoinRequests.Clear();
            _joiningParticipants.Clear();
            _recoveryRequested = false;
            _rerunRequested = false;
            _fullSnapshotRequested = false;
            _snapshotTargets.Clear();
            _epoch = 0;
            _deadlineMs = 0;
            _cleanFrames = 0;
        }

        private void DrainObserverEvents(MultiplayerSession session)
        {
            while (_requests.TryDequeue(out RecoveryRequest request))
            {
                if (request.IsJoin && !request.Connection.IsNone)
                    _pendingJoinRequests.Add(request.Connection.Value);
                else if (!request.IsJoin)
                    _fullSnapshotRequested = true;
                if (_state == RecoveryState.Idle) _recoveryRequested = true;
                else _rerunRequested = true;
            }

            while (_leaves.TryDequeue(out ConnectionId left))
            {
                _pendingJoinRequests.Remove(left.Value);
                RemoveParticipant(left);
            }

            while (_controls.TryDequeue(out ControlEvent evt))
            {
                if (_state == RecoveryState.Idle || evt.Epoch != _epoch ||
                    !ContainsParticipant(evt.Connection))
                    continue;

                if (evt.Stage == WorldSyncStage.Quiesced &&
                    _state == RecoveryState.WaitingForQuiescence)
                    _quiesced.Add(evt.Connection.Value);
                else if (evt.Stage == WorldSyncStage.Loaded &&
                         _state == RecoveryState.WaitingForLoaded)
                    _loaded.Add(evt.Connection.Value);
                else if (evt.Stage == WorldSyncStage.Failed &&
                         _state == RecoveryState.WaitingForLoaded)
                {
                    SyncLog.Error(LogTopic.Resync, DescribePeer(session, evt.Connection) +
                        " could not install world-sync epoch " + _epoch +
                        "; disconnecting it rather than resuming divergent worlds.");
                    session.DisconnectPeer(evt.Connection);
                    RemoveParticipant(evt.Connection);
                }
            }
        }

        private void StartEpoch(MultiplayerService service, MultiplayerSession session, long now)
        {
            if (_saveTask != null)
            {
                if (!_saveTask.IsCompleted) return;
                ObserveFinishedSave();
            }
            _recoveryRequested = false;
            _participants.Clear();
            foreach (Peer peer in session.Peers)
                if (peer.Handshaked) _participants.Add(peer.Connection);
            if (_participants.Count == 0) return;

            _joiningParticipants.Clear();
            for (int i = 0; i < _participants.Count; i++)
                if (_pendingJoinRequests.Contains(_participants[i].Value))
                    _joiningParticipants.Add(_participants[i]);
            service.PrepareHostWorldSyncUi(_joiningParticipants);

            // A join streams only to the joiner; divergence and player-requested epochs re-baseline everyone.
            _snapshotTargets.Clear();
            if (_fullSnapshotRequested || _joiningParticipants.Count == 0)
                _snapshotTargets.AddRange(_participants);
            else
                _snapshotTargets.AddRange(_joiningParticipants);
            _fullSnapshotRequested = false;

            _epoch = ++_epochCounter;
            if (!service.TryBeginHostWorldSync(_epoch, out _resumeSpeed))
            {
                SyncLog.Error(LogTopic.Resync, "Could not enter the local world-sync barrier.");
                ResetEpoch();
                return;
            }
            if (!session.BeginWorldSync(_epoch, _resumeSpeed, _participants, _snapshotTargets))
            {
                service.AbortHostWorldSync(_epoch, _resumeSpeed);
                SyncLog.Error(LogTopic.Resync, "Could not open world-sync epoch " + _epoch + ".");
                ResetEpoch();
                return;
            }
            for (int i = 0; i < _joiningParticipants.Count; i++)
                _pendingJoinRequests.Remove(_joiningParticipants[i].Value);

            _quiesced.Clear();
            _loaded.Clear();
            _cleanFrames = 0;
            _deadlineMs = now + QuiesceTimeoutMs;
            _state = RecoveryState.WaitingForQuiescence;
            SyncLog.Event(LogTopic.Resync, "World sync epoch " + _epoch + " waiting for " +
                _participants.Count + " client quiescence acknowledgement(s).");
        }

        private void PumpQuiescence(MultiplayerService service, MultiplayerSession session, long now)
        {
            if (now > _deadlineMs)
            {
                DisconnectMissing(session, _quiesced, "quiescence acknowledgement");
                if (_participants.Count == 0)
                {
                    AbortEpoch(service, "no client crossed the quiescence barrier");
                    return;
                }
                if (AllParticipantsIn(_quiesced) && _netSync != null &&
                    !_netSync.IsRecoveryQuiescent)
                {
                    AbortEpoch(service, "local native transaction did not quiesce safely");
                    return;
                }
            }

            if (!AllParticipantsIn(_quiesced) ||
                (_netSync != null && !_netSync.IsRecoveryQuiescent))
            {
                _cleanFrames = 0;
                return;
            }

            // Two clean frames close races with a tool apply already scheduled when Begin arrived.
            if (++_cleanFrames < RequiredCleanFrames) return;
            StartSave(service, now);
        }

        private void StartSave(MultiplayerService service, long now)
        {
            try
            {
                if (_saveTask != null && !_saveTask.IsCompleted) return;
                ObserveFinishedSave();
                _saveCancellation = new CancellationTokenSource();
                _saveTask = service.CreateWorldSnapshot(World, _epoch, _saveCancellation.Token);
                long savingEpoch = _epoch;
                CancellationToken saveToken = _saveCancellation.Token;
                _saveTask.ContinueWith(task =>
                {
                    if (task.IsFaulted)
                        SyncLog.Error(LogTopic.Resync, "World snapshot epoch " + savingEpoch +
                            " failed: " + task.Exception.GetBaseException().Message);
                    else if (task.Status == TaskStatus.RanToCompletion && saveToken.IsCancellationRequested)
                        task.Result?.Dispose();
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                _saveStartMs = now;
                _state = RecoveryState.Saving;
                service.SetHostWorldSyncUiStage(HostWorldSyncUiStage.Saving);
                SyncLog.Event(LogTopic.Resync, "World sync epoch " + _epoch +
                    ": barrier closed; saving the authoritative world.");
            }
            catch (Exception ex)
            {
                AbortEpoch(service, "could not start authoritative save: " + ex.Message);
            }
        }

        private void PumpSave(MultiplayerService service, MultiplayerSession session, long now)
        {
            if (_saveTask != null && !_saveTask.IsCompleted) return;
            if (_saveTask == null || _saveTask.IsFaulted || _saveTask.IsCanceled)
            {
                string detail = _saveTask != null && _saveTask.Exception != null
                    ? _saveTask.Exception.GetBaseException().Message
                    : (_saveTask != null && _saveTask.IsCanceled ? "save was canceled" : "save task disappeared");
                AbortEpoch(service, "authoritative save failed: " + detail);
                return;
            }

            BlobSource snapshot = _saveTask.Result;
            _saveTask = null;
            ObserveFinishedSave();
            if (snapshot == null || snapshot.Length == 0)
            {
                AbortEpoch(service, "authoritative save produced no snapshot data");
                return;
            }
            string saveName = MultiplayerService.WorldSnapshotFileName;

            try
            {
                for (int i = 0; i < _snapshotTargets.Count; i++)
                    service.StreamWorldSnapshot(_snapshotTargets[i], _epoch, snapshot, saveName);
            }
            catch (Exception ex)
            {
                AbortEpoch(service, "could not stream snapshot: " + ex.Message);
                return;
            }
            finally { snapshot.Dispose(); }

            _loaded.Clear();
            // Barrier-only participants install nothing, so they never acknowledge a load.
            for (int i = 0; i < _participants.Count; i++)
                if (!_snapshotTargets.Contains(_participants[i]))
                    _loaded.Add(_participants[i].Value);
            _lastTransferProgress = 0;
            _deadlineMs = now + LoadTimeoutMs;
            _state = RecoveryState.WaitingForLoaded;
            service.SetHostWorldSyncUiStage(HostWorldSyncUiStage.WaitingForLoaded);
            SyncLog.Event(LogTopic.Resync, "World sync epoch " + _epoch + ": queued one " +
                (snapshot.Length / 1024) + " KB snapshot for " + _snapshotTargets.Count +
                " of " + _participants.Count +
                " participant(s); waiting for load acknowledgement(s). Save took " +
                (now - _saveStartMs) + " ms.");
        }

        private void PumpLoaded(MultiplayerService service, MultiplayerSession session, long now)
        {
            if (session.OutgoingBlobSent > _lastTransferProgress)
            {
                _lastTransferProgress = session.OutgoingBlobSent;
                _deadlineMs = now + LoadTimeoutMs;
            }
            if (AllParticipantsIn(_loaded))
            {
                CompleteEpoch(service, session, now);
                return;
            }
            if (now <= _deadlineMs) return;

            DisconnectMissing(session, _loaded, "snapshot load acknowledgement");
            if (_participants.Count == 0)
                AbortEpoch(service, "no client installed the authoritative snapshot");
            else
                CompleteEpoch(service, session, now);
        }

        private void CompleteEpoch(MultiplayerService service, MultiplayerSession session, long now)
        {
            var targets = new List<ConnectionId>(_participants);
            // A peer that asked mid-epoch may not have been streamed to; rerun for it.
            bool needsRerun = _rerunRequested &&
                (_fullSnapshotRequested || HasNewParticipant(session, targets));
            // Resume first, then reopen command sends: Resume-before-command order on every connection.
            session.ResumeWorldSync(_epoch, _resumeSpeed, targets);
            service.CompleteHostWorldSync(_epoch, _resumeSpeed);
            SyncLog.Event(LogTopic.Resync, "World sync epoch " + _epoch + " completed for " +
                targets.Count + " participant(s).");
            ResetEpoch();

            // Open the next Begin in the same update: no gameplay frame between epochs.
            _rerunRequested = false;
            if (needsRerun)
            {
                _recoveryRequested = true;
                StartEpoch(service, session, now);
            }
        }

        private void AbortEpoch(MultiplayerService service, string reason)
        {
            MultiplayerSession session = service.Session;
            var targets = new List<ConnectionId>(_participants);
            if (_epoch > 0)
            {
                session.AbortWorldSync(_epoch, _resumeSpeed, targets);
                service.AbortHostWorldSync(_epoch, _resumeSpeed);
            }
            SyncLog.Error(LogTopic.Resync, "World sync epoch " + _epoch + " aborted: " + reason +
                ".");
            ResetEpoch();
        }

        private void CancelSave()
        {
            _saveCancellation?.Cancel();
            if (_saveTask == null || _saveTask.IsCompleted) ObserveFinishedSave();
        }

        private void ObserveFinishedSave()
        {
            if (_saveTask != null && !_saveTask.IsCompleted) return;
            if (_saveTask != null && _saveTask.IsFaulted) _ = _saveTask.Exception;
            if (_saveTask != null && _saveTask.Status == TaskStatus.RanToCompletion)
                _saveTask.Result?.Dispose();
            _saveTask = null;
            _saveCancellation?.Dispose();
            _saveCancellation = null;
        }

        private void ResetEpoch()
        {
            _state = RecoveryState.Idle;
            CancelSave();
            _participants.Clear();
            _quiesced.Clear();
            _loaded.Clear();
            _joiningParticipants.Clear();
            _snapshotTargets.Clear();
            _epoch = 0;
            _deadlineMs = 0;
            _cleanFrames = 0;
        }

        private void DisconnectMissing(MultiplayerSession session, HashSet<int> acknowledgements,
            string expected)
        {
            for (int i = _participants.Count - 1; i >= 0; i--)
            {
                ConnectionId connection = _participants[i];
                if (acknowledgements.Contains(connection.Value)) continue;
                SyncLog.Error(LogTopic.Resync, DescribePeer(session, connection) +
                    " timed out waiting for " + expected + " in epoch " + _epoch +
                    "; disconnecting it.");
                session.DisconnectPeer(connection);
                RemoveParticipant(connection);
            }
        }

        private void PruneDisconnectedParticipants(MultiplayerSession session)
        {
            for (int i = _participants.Count - 1; i >= 0; i--)
                if (!IsConnected(session, _participants[i])) RemoveParticipant(_participants[i]);
        }

        private static bool IsConnected(MultiplayerSession session, ConnectionId connection)
        {
            foreach (Peer peer in session.Peers)
                if (peer.Connection == connection && peer.Handshaked) return true;
            return false;
        }

        private static string DescribePeer(MultiplayerSession session, ConnectionId connection)
        {
            foreach (Peer peer in session.Peers)
                if (peer.Connection == connection) return peer.ToString();
            return connection.ToString();
        }

        private void RemoveParticipant(ConnectionId connection)
        {
            _participants.Remove(connection);
            _snapshotTargets.Remove(connection);
            _quiesced.Remove(connection.Value);
            _loaded.Remove(connection.Value);
        }

        private bool ContainsParticipant(ConnectionId connection) =>
            _participants.Contains(connection);

        private bool AllParticipantsIn(HashSet<int> set)
        {
            if (_participants.Count == 0) return false;
            for (int i = 0; i < _participants.Count; i++)
                if (!set.Contains(_participants[i].Value)) return false;
            return true;
        }

        private static bool HasNewParticipant(MultiplayerSession session,
            List<ConnectionId> completedParticipants)
        {
            foreach (Peer peer in session.Peers)
                if (peer.Handshaked && !completedParticipants.Contains(peer.Connection)) return true;
            return false;
        }

        private sealed class Observer : SessionObserver
        {
            private readonly ConcurrentQueue<RecoveryRequest> _requests;
            private readonly ConcurrentQueue<ControlEvent> _controls;
            private readonly ConcurrentQueue<ConnectionId> _leaves;

            public Observer(ConcurrentQueue<RecoveryRequest> requests,
                ConcurrentQueue<ControlEvent> controls, ConcurrentQueue<ConnectionId> leaves)
            {
                _requests = requests;
                _controls = controls;
                _leaves = leaves;
            }

            public override void OnPeerJoined(Peer peer)
            {
                _requests.Enqueue(new RecoveryRequest
                {
                    Connection = peer.Connection,
                    IsJoin = true,
                });
                SyncLog.Event(LogTopic.Resync, "Queued atomic initial world sync for " + peer +
                    ".");
            }

            public override void OnPeerLeft(Peer peer, string reason) =>
                _leaves.Enqueue(peer.Connection);

            public override void OnResyncRequested(int playerId, ConnectionId connection)
            {
                _requests.Enqueue(new RecoveryRequest
                {
                    Connection = connection,
                    IsJoin = false,
                });
                SyncLog.Event(LogTopic.Resync, "Queued atomic world-sync for peer #" +
                    playerId + ".");
            }

            public override void OnWorldSyncControl(WorldSyncStage stage, long epoch,
                float resumeSpeed, ConnectionId connection)
            {
                if (stage != WorldSyncStage.Quiesced && stage != WorldSyncStage.Loaded &&
                    stage != WorldSyncStage.Failed)
                    return;
                _controls.Enqueue(new ControlEvent
                {
                    Stage = stage,
                    Epoch = epoch,
                    Connection = connection,
                });
            }
        }
    }
}
