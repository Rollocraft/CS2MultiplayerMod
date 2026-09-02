using System.Collections.Generic;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Networking;
using CS2MultiplayerMod.Core.Protocol.Messages;

namespace CS2MultiplayerMod.Core.Session
{
    public sealed partial class MultiplayerSession
    {
        /// <summary>
        /// Host: atomically suspend gameplay traffic and send Begin to the exact peer set that
        /// will receive this snapshot. A peer joining later is intentionally not folded into a
        /// transfer whose causal cut is already being prepared.
        /// </summary>
        public bool BeginWorldSync(long epoch, float resumeSpeed, IList<ConnectionId> targets)
        {
            if (Role != SessionRole.Host || Status != SessionStatus.Connected || epoch <= 0 ||
                _worldSyncSuspended)
                return false;

            // A new barrier gets its own progress interval. Otherwise the completed percentage
            // from the previous snapshot would briefly appear while this one is still being saved.
            _outgoingBlobActive = false;
            _outgoingBlobTotal = 0;
            _outgoingBlobSent = 0;
            _worldSyncEpoch = epoch;
            _worldSyncSuspended = true;
            var begin = new WorldSyncControlMessage(epoch, WorldSyncStage.Begin, resumeSpeed);
            SendWorldSyncToTargets(begin, targets);
            _log.Event(LogTopic.WorldTransfer, "World sync epoch " + epoch + " began for " +
                (targets != null ? targets.Count : 0) + " peer(s); gameplay traffic suspended.");
            return true;
        }

        /// <summary>Client: acknowledge a stage for the active epoch.</summary>
        public void SendWorldSyncStage(long epoch, WorldSyncStage stage)
        {
            if (Role != SessionRole.Client || Status != SessionStatus.Connected ||
                !_worldSyncSuspended || epoch != _worldSyncEpoch)
                return;
            if (stage != WorldSyncStage.Quiesced && stage != WorldSyncStage.Loaded &&
                stage != WorldSyncStage.Failed)
                return;
            SendTo(ConnectionId.Server, new WorldSyncControlMessage(epoch, stage));
        }

        /// <summary>
        /// Host: queue Resume behind all snapshot chunks, then reopen local gameplay traffic.
        /// TCP ordering guarantees each client sees Resume before any later command.
        /// </summary>
        public bool ResumeWorldSync(long epoch, float resumeSpeed, IList<ConnectionId> targets)
        {
            if (Role != SessionRole.Host || !_worldSyncSuspended || epoch != _worldSyncEpoch)
                return false;

            SendWorldSyncToTargets(
                new WorldSyncControlMessage(epoch, WorldSyncStage.Resume, resumeSpeed), targets);
            _worldSyncSuspended = false;
            _worldSyncEpoch = 0;
            _log.Event(LogTopic.WorldTransfer, "World sync epoch " + epoch + " resumed.");
            return true;
        }

        /// <summary>Host: abandon a failed snapshot transaction and reopen the old world.</summary>
        public bool AbortWorldSync(long epoch, float resumeSpeed, IList<ConnectionId> targets)
        {
            if (Role != SessionRole.Host || !_worldSyncSuspended || epoch != _worldSyncEpoch)
                return false;

            SendWorldSyncToTargets(
                new WorldSyncControlMessage(epoch, WorldSyncStage.Abort, resumeSpeed), targets);
            _worldSyncSuspended = false;
            _worldSyncEpoch = 0;
            _log.Warn(LogTopic.WorldTransfer, "World sync epoch " + epoch +
                " aborted; previous world resumed.");
            return true;
        }

        /// <summary>Host-only administrative disconnect used when a barrier participant stalls.</summary>
        public void DisconnectPeer(ConnectionId connection)
        {
            if (Role == SessionRole.Host && _transport != null && !connection.IsNone)
                _transport.Disconnect(connection);
        }

        private void SendWorldSyncToTargets(WorldSyncControlMessage message,
            IList<ConnectionId> targets)
        {
            if (targets == null) return;
            for (int i = 0; i < targets.Count; i++)
            {
                Peer peer;
                if (_peers.TryGetValue(targets[i].Value, out peer) && peer.Handshaked)
                    SendTo(targets[i], message);
            }
        }

        private void HandleWorldSyncControl(ConnectionId from, Peer peer,
            WorldSyncControlMessage control)
        {
            if (control.Epoch <= 0)
            {
                Punt(from, peer, "invalid world-sync epoch", "WorldSyncControl");
                return;
            }

            if (Role == SessionRole.Host)
            {
                if (control.Stage != WorldSyncStage.Quiesced &&
                    control.Stage != WorldSyncStage.Loaded &&
                    control.Stage != WorldSyncStage.Failed)
                {
                    Punt(from, peer, "client sent host-only world-sync stage " + control.Stage,
                        "WorldSyncControl");
                    return;
                }
                if (!_worldSyncSuspended || control.Epoch != _worldSyncEpoch)
                {
                    _log.Warn(LogTopic.WorldTransfer, "Ignoring stale world-sync " + control.Stage +
                        " for epoch " + control.Epoch + " from " + from + ".");
                    return;
                }
                NotifyWorldSync(control.Stage, control.Epoch, 0f, from);
                return;
            }

            if (control.Stage == WorldSyncStage.Begin)
            {
                if (_worldSyncSuspended && control.Epoch < _worldSyncEpoch) return;
                if (!_worldSyncSuspended || control.Epoch != _worldSyncEpoch)
                {
                    _blobs.Clear();
                    _blobTransferIds.Clear();
                    ClearBlobProgress();
                    _worldSyncEpoch = control.Epoch;
                    _worldSyncSuspended = true;
                }
                // Duplicate Begin is deliberately delivered: the game layer re-sends Quiesced,
                // making a lost acknowledgement self-healing.
                NotifyWorldSync(control.Stage, control.Epoch, control.ResumeSpeed, from);
                return;
            }

            if (control.Stage != WorldSyncStage.Resume && control.Stage != WorldSyncStage.Abort)
            {
                Punt(from, peer, "host sent client-only world-sync stage " + control.Stage,
                    "WorldSyncControl");
                return;
            }
            if (!_worldSyncSuspended || control.Epoch != _worldSyncEpoch) return;

            NotifyWorldSync(control.Stage, control.Epoch, control.ResumeSpeed, from);
            _worldSyncSuspended = false;
            _worldSyncEpoch = 0;
            if (control.Stage == WorldSyncStage.Abort)
            {
                _blobs.Clear();
                _blobTransferIds.Clear();
                ClearBlobProgress();
            }
        }
    }
}
