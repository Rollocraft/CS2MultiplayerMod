using System;
using System.Collections.Generic;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Networking;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Infrastructure;
using CS2MultiplayerMod.Game.Sync.Systems.Net;
using Game.Simulation;
using Game.Tools;
using Unity.Entities;

namespace CS2MultiplayerMod.Game
{
    internal enum HostWorldSyncUiStage
    {
        None,
        WaitingForQuiescence,
        Saving,
        WaitingForLoaded,
    }

    public sealed partial class MultiplayerService
    {
        private World _currentWorld;
        private bool _worldSyncBarrierActive;
        private bool _worldSyncInputLocked;
        private bool _worldSyncBarrierOnly;
        private int _worldSyncGateDelayFrames;
        private bool _worldSyncHadUsableWorld;
        private long _activeWorldSyncEpoch;
        private bool _clientQuiescencePending;
        private int _clientQuiescenceCleanFrames;
        private long _clientQuiescedEpoch;
        private float _worldSyncResumeSpeed = 1f;
        private HostWorldSyncUiStage _hostWorldSyncUiStage;
        private string _hostWorldSyncJoiningName;
        private int _hostWorldSyncJoiningCount;
        private const int RequiredClientQuiescenceFrames = 2;

        /// <summary>True while all gameplay traffic and local tools are quiesced for a snapshot.</summary>
        public bool WorldSyncBarrierActive => _worldSyncBarrierActive;

        /// <summary>Joining players behind this epoch; resyncs pass an empty list and get neutral text.</summary>
        internal void PrepareHostWorldSyncUi(IList<ConnectionId> joiningPlayers)
        {
            _hostWorldSyncJoiningName = null;
            _hostWorldSyncJoiningCount = 0;
            if (joiningPlayers == null || joiningPlayers.Count == 0) return;

            for (int i = 0; i < joiningPlayers.Count; i++)
            {
                foreach (Peer peer in _session.Peers)
                {
                    if (!peer.Handshaked || peer.Connection != joiningPlayers[i]) continue;
                    _hostWorldSyncJoiningCount++;
                    if (_hostWorldSyncJoiningCount == 1)
                        _hostWorldSyncJoiningName = peer.Name;
                    break;
                }
            }
        }

        internal void SetHostWorldSyncUiStage(HostWorldSyncUiStage stage) => _hostWorldSyncUiStage = stage;

        /// <summary>
        /// Host half of Begin: capture speed, pause, drop pre-cut inboxes, close
        /// <see cref="GameplaySyncReady"/>.
        /// </summary>
        internal bool TryBeginHostWorldSync(long epoch, out float resumeSpeed)
        {
            resumeSpeed = 0f;
            if (_session.Role != SessionRole.Host || _worldSyncBarrierActive || epoch <= 0)
                return false;

            _worldSyncResumeSpeed = ReadSimulationSpeed();
            _worldSyncHadUsableWorld = true;
            _activeWorldSyncEpoch = epoch;
            _worldSyncBarrierActive = true;
            _worldSyncInputLocked = true;
            _hostWorldSyncUiStage = HostWorldSyncUiStage.WaitingForQuiescence;
            SyncInbox.DrainAll();
            // Evidence about the replaced world must not settle a second reload.
            Diagnostics.ResyncArbiter.Reset();
            MaintainWorldSyncBarrier();
            resumeSpeed = _worldSyncResumeSpeed;
            _log.Detail(LogTopic.WorldTransfer, "World sync epoch " + epoch +
                " entered the local quiescence barrier (resume speed " + resumeSpeed + ").");
            return true;
        }

        internal void CompleteHostWorldSync(long epoch, float resumeSpeed)
        {
            if (!_worldSyncBarrierActive || epoch != _activeWorldSyncEpoch) return;
            _worldSyncResumeSpeed = SanitizeSpeed(resumeSpeed);
            ResetWorldSyncState(restoreSpeed: true);
            _log.Event(LogTopic.WorldTransfer, "World sync epoch " + epoch +
                " completed; gameplay resumed.");
        }

        internal void AbortHostWorldSync(long epoch, float resumeSpeed)
        {
            if (!_worldSyncBarrierActive || epoch != _activeWorldSyncEpoch) return;
            _worldSyncResumeSpeed = SanitizeSpeed(resumeSpeed);
            ResetWorldSyncState(restoreSpeed: true);
            _log.Warn(LogTopic.WorldTransfer, "World sync epoch " + epoch +
                " aborted before a snapshot was installed; previous world resumed.");
        }

        private void HandleWorldSyncControl(WorldSyncStage stage, long epoch, float resumeSpeed)
        {
            if (_session.Role != SessionRole.Client) return;

            if (stage == WorldSyncStage.Begin || stage == WorldSyncStage.BeginBarrierOnly)
            {
                bool barrierOnly = stage == WorldSyncStage.BeginBarrierOnly;
                if (_worldSyncInputLocked && epoch < _activeWorldSyncEpoch) return;
                if (!_worldSyncInputLocked || epoch != _activeWorldSyncEpoch)
                {
                    _worldSyncHadUsableWorld = _phase == ClientWorldPhase.InSession;
                    _activeWorldSyncEpoch = epoch;
                    _worldSyncResumeSpeed = SanitizeSpeed(resumeSpeed);
                    _worldSyncInputLocked = true;
                    _worldSyncBarrierOnly = barrierOnly;
                    _clientQuiescencePending = true;
                    _clientQuiescenceCleanFrames = 0;
                    _clientQuiescedEpoch = 0;

                    if (barrierOnly)
                    {
                        // This city is kept, so its pre-cut commands must still apply: input locks now, the gameplay gate
                        // stays open two more frames.
                        _worldSyncGateDelayFrames = RequiredClientQuiescenceFrames;
                        _log.Detail(LogTopic.WorldTransfer, "World sync epoch " + epoch +
                            " began as barrier-only; this city keeps its world and is paused " +
                            "until the host resumes.");
                    }
                    else
                    {
                        _worldSyncBarrierActive = true;
                        _worldSyncGateDelayFrames = 0;
                        SyncInbox.DrainAll();
                        Diagnostics.ResyncArbiter.Reset();
                        SetPhase(ClientWorldPhase.WaitingForMap);
                        _log.Detail(LogTopic.WorldTransfer, "World sync epoch " + epoch +
                            " began; local gameplay is paused while native transactions drain.");
                    }
                }
                MaintainWorldSyncBarrier();
                if (_clientQuiescedEpoch == epoch)
                    _session.SendWorldSyncStage(epoch, WorldSyncStage.Quiesced);
                return;
            }

            if (!_worldSyncInputLocked || epoch != _activeWorldSyncEpoch) return;

            if (stage == WorldSyncStage.Resume)
            {
                _worldSyncResumeSpeed = SanitizeSpeed(resumeSpeed);
                // Barrier-only assumes this city holds the world; otherwise recover below.
                if (_worldSyncBarrierOnly && _phase == ClientWorldPhase.InSession)
                {
                    // Nothing installed, nothing stale: lift the pause.
                    ResetWorldSyncState(restoreSpeed: true);
                    _log.Event(LogTopic.WorldTransfer, "World sync epoch " + epoch +
                        " resumed; this city held the barrier without a snapshot.");
                    return;
                }
                if (_phase != ClientWorldPhase.WaitingForResume)
                {
                    Diagnostics.SyncLog.Error(LogTopic.WorldTransfer,
                        "World sync: the host finished epoch " + epoch +
                        " before this city had installed its snapshot. Asking for the world again.");
                    ResetWorldSyncState(restoreSpeed: false);
                    SetPhase(ClientWorldPhase.WaitingForMap);
                    // Not the arbiter: the session coalesces requests inside the epoch, and WaitingForMap reads as a
                    // reload in flight, so no world would ever be requested.
                    RequestMapAgainNextTick();
                    return;
                }

                if (_worldInstallGeneration < long.MaxValue) _worldInstallGeneration++;
                ResetWorldSyncState(restoreSpeed: true);
                SetPhase(ClientWorldPhase.InSession);
                _log.Event(LogTopic.WorldTransfer, "World sync epoch " + epoch +
                    " resumed after the authoritative snapshot was installed.");
                return;
            }

            if (stage == WorldSyncStage.Abort)
            {
                bool canResumeOldWorld = _worldSyncHadUsableWorld;
                _worldSyncResumeSpeed = SanitizeSpeed(resumeSpeed);
                ResetWorldSyncState(restoreSpeed: canResumeOldWorld);
                SetPhase(canResumeOldWorld ? ClientWorldPhase.InSession : ClientWorldPhase.WaitingForMap);
                _log.Warn(LogTopic.WorldTransfer, "Host aborted world-sync epoch " + epoch + ".");
            }
        }

        /// <summary>Keep pause/tool quiescence enforced even if a state apply or map load resets it.</summary>
        private void MaintainWorldSyncBarrier()
        {
            if (!_worldSyncInputLocked || _currentWorld == null) return;
            try
            {
                SimulationSystem simulation =
                    _currentWorld.GetOrCreateSystemManaged<SimulationSystem>();
                if (simulation != null && !simulation.selectedSpeed.Equals(0f))
                    simulation.selectedSpeed = 0f;

                ToolSystem tools = _currentWorld.GetOrCreateSystemManaged<ToolSystem>();
                DefaultToolSystem defaultTool =
                    _currentWorld.GetOrCreateSystemManaged<DefaultToolSystem>();
                if (tools != null && defaultTool != null && tools.activeTool != defaultTool)
                    tools.activeTool = defaultTool;
            }
            catch (Exception ex)
            {
                // Expected for one frame at a world boundary.
                SyncLog.Warn(LogTopic.WorldTransfer,
                    "Could not enforce world-sync pause on this frame: " + ex.Message);
            }
        }

        /// <summary>
        /// Acknowledge Begin only once the scheduled native transaction has left Temp; loading earlier
        /// recreates the cleanup race the barrier closes.
        /// </summary>
        private void PumpClientWorldSyncQuiescence()
        {
            if (!_clientQuiescencePending || !_worldSyncInputLocked ||
                _session.Role != SessionRole.Client || _activeWorldSyncEpoch <= 0)
                return;

            // A barrier-only peer keeps applying its queued pre-cut commands for these frames.
            if (_worldSyncGateDelayFrames > 0)
            {
                _worldSyncGateDelayFrames--;
                return;
            }
            _worldSyncBarrierActive = true;

            bool quiescent = false;
            try
            {
                NetSyncSystem netSync = _currentWorld != null
                    ? _currentWorld.GetExistingSystemManaged<NetSyncSystem>()
                    : null;
                quiescent = netSync == null || netSync.IsRecoveryQuiescent;
            }
            catch
            {
                // An uninspectable pipeline is not a safe one.
                quiescent = false;
            }

            if (!quiescent)
            {
                _clientQuiescenceCleanFrames = 0;
                return;
            }
            if (++_clientQuiescenceCleanFrames < RequiredClientQuiescenceFrames) return;

            long epoch = _activeWorldSyncEpoch;
            _clientQuiescencePending = false;
            _clientQuiescedEpoch = epoch;
            _session.SendWorldSyncStage(epoch, WorldSyncStage.Quiesced);
            _log.Detail(LogTopic.WorldTransfer, "World sync epoch " + epoch +
                ": local native transactions drained; quiescence acknowledged.");
        }

        private float ReadSimulationSpeed()
        {
            if (_currentWorld == null) return 1f;
            try
            {
                SimulationSystem simulation =
                    _currentWorld.GetOrCreateSystemManaged<SimulationSystem>();
                return simulation != null ? SanitizeSpeed(simulation.selectedSpeed) : 1f;
            }
            catch { return 1f; }
        }

        private void ResetWorldSyncState(bool restoreSpeed)
        {
            float speed = _worldSyncResumeSpeed;
            _worldSyncBarrierActive = false;
            _worldSyncInputLocked = false;
            _worldSyncBarrierOnly = false;
            _worldSyncGateDelayFrames = 0;
            _activeWorldSyncEpoch = 0;
            _deferredMapTransferId = 0;
            _deferredMapData = null;
            _worldSyncHadUsableWorld = false;
            _clientQuiescencePending = false;
            _clientQuiescenceCleanFrames = 0;
            _clientQuiescedEpoch = 0;
            _hostWorldSyncUiStage = HostWorldSyncUiStage.None;
            _hostWorldSyncJoiningName = null;
            _hostWorldSyncJoiningCount = 0;

            if (!restoreSpeed || _currentWorld == null) return;
            try
            {
                SimulationSystem simulation =
                    _currentWorld.GetOrCreateSystemManaged<SimulationSystem>();
                if (simulation != null) simulation.selectedSpeed = SanitizeSpeed(speed);
            }
            catch (Exception ex)
            {
                SyncLog.Warn(LogTopic.WorldTransfer,
                    "Could not restore simulation speed after world sync: " + ex.Message);
            }
        }

        private static float SanitizeSpeed(float speed) =>
            float.IsNaN(speed) || float.IsInfinity(speed) || speed < 0f ? 0f : speed;
    }
}
