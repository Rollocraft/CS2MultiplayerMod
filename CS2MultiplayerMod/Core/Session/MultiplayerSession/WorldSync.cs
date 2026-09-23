using System.Collections.Generic;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Networking;
using CS2MultiplayerMod.Core.Protocol.Messages;

namespace CS2MultiplayerMod.Core.Session
{
    public sealed partial class MultiplayerSession
    {
        /// <summary>
        /// Host: suspend gameplay traffic and send Begin to this snapshot's exact peer set; later joiners
        /// are not folded in. <paramref name="snapshotTargets"/> receive a world, the rest hold the barrier
        /// only; null means all.
        /// </summary>
        public bool BeginWorldSync(long epoch, float resumeSpeed, IList<ConnectionId> targets,
            IList<ConnectionId> snapshotTargets = null)
        {
            if (Role != SessionRole.Host || Status != SessionStatus.Connected || epoch <= 0 ||
                _worldSyncSuspended)
                return false;

            // Fresh progress, so the last snapshot's percentage does not flash up.
            ClearOutgoingBlobs();
            _outgoingBlobActive = false;
            _outgoingBlobTotal = 0;
            _outgoingBlobSent = 0;
            _worldSyncEpoch = epoch;
            _worldSyncSuspended = true;
            int barrierOnly = 0;
            if (targets != null)
                for (int i = 0; i < targets.Count; i++)
                {
                    bool sendsWorld = snapshotTargets == null || snapshotTargets.Contains(targets[i]);
                    if (!sendsWorld) barrierOnly++;
                    SendWorldSyncTo(targets[i], new WorldSyncControlMessage(epoch,
                        sendsWorld ? WorldSyncStage.Begin : WorldSyncStage.BeginBarrierOnly,
                        resumeSpeed));
                }
            _log.Event(LogTopic.WorldTransfer, "World sync epoch " + epoch + " began for " +
                (targets != null ? targets.Count : 0) + " peer(s), " + barrierOnly +
                " of them barrier-only; gameplay traffic suspended.");
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

        /// <summary>Host: Resume queues behind every chunk, so TCP ordering puts it before any later command.</summary>
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

            ClearOutgoingBlobs();
            _outgoingBlobActive = false;
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
                SendWorldSyncTo(targets[i], message);
        }

        private void SendWorldSyncTo(ConnectionId target, WorldSyncControlMessage message)
        {
            if (_peers.TryGetValue(target.Value, out Peer peer) && peer.Handshaked)
                SendTo(target, message);
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

            if (control.Stage == WorldSyncStage.Begin ||
                control.Stage == WorldSyncStage.BeginBarrierOnly)
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
                // A duplicate Begin is delivered: the game layer re-sends Quiesced, healing a lost ack.
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
