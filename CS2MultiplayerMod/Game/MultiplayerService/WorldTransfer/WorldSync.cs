using System;
using System.Collections.Generic;
using CS2MultiplayerMod.Core.Networking;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;
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

        /// <summary>
        /// Capture which newly connected players caused this epoch. Periodic/manual
        /// re-syncs pass an empty list and receive neutral "refreshing world" copy.
        /// </summary>
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

        internal void SetHostWorldSyncUiStage(HostWorldSyncUiStage stage)
        {
            _hostWorldSyncUiStage = stage;
        }

        /// <summary>
        /// Host-side half of Begin. Captures the shared speed, pauses the local simulation, drops
        /// every pre-cut inbox, and closes <see cref="GameplaySyncReady"/> synchronously.
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
            _hostWorldSyncUiStage = HostWorldSyncUiStage.WaitingForQuiescence;
            SyncInbox.DrainAll();
            // Held evidence describes the world that is about to be replaced. Keeping it would let
            // a fault from before the barrier settle a second reload after it.
            Diagnostics.ResyncArbiter.Reset();
            MaintainWorldSyncBarrier();
            resumeSpeed = _worldSyncResumeSpeed;
            _log.Info("[MP] World sync epoch " + epoch +
                      " entered the local quiescence barrier (resume speed " + resumeSpeed + ").");
            Diagnostics.FlightRecorder.Note("world-sync begin epoch=" + epoch +
                                              " resumeSpeed=" + resumeSpeed);
            return true;
        }

        internal void CompleteHostWorldSync(long epoch, float resumeSpeed)
        {
            if (!_worldSyncBarrierActive || epoch != _activeWorldSyncEpoch) return;
            _worldSyncResumeSpeed = SanitizeSpeed(resumeSpeed);
            ResetWorldSyncState(restoreSpeed: true);
            _log.Info("[MP] World sync epoch " + epoch + " completed; gameplay resumed.");
            Diagnostics.FlightRecorder.Note("world-sync resume epoch=" + epoch);
        }

        internal void AbortHostWorldSync(long epoch, float resumeSpeed)
        {
            if (!_worldSyncBarrierActive || epoch != _activeWorldSyncEpoch) return;
            _worldSyncResumeSpeed = SanitizeSpeed(resumeSpeed);
            ResetWorldSyncState(restoreSpeed: true);
            _log.Warn("[MP] World sync epoch " + epoch +
                      " aborted before a snapshot was installed; previous world resumed.");
            Diagnostics.FlightRecorder.Note("world-sync abort epoch=" + epoch);
        }

        private void HandleWorldSyncControl(WorldSyncStage stage, long epoch, float resumeSpeed)
        {
            if (_session.Role != SessionRole.Client) return;

            if (stage == WorldSyncStage.Begin)
            {
                if (_worldSyncBarrierActive && epoch < _activeWorldSyncEpoch) return;
                if (!_worldSyncBarrierActive || epoch != _activeWorldSyncEpoch)
                {
                    _worldSyncHadUsableWorld = _phase == ClientWorldPhase.InSession;
                    _activeWorldSyncEpoch = epoch;
                    _worldSyncResumeSpeed = SanitizeSpeed(resumeSpeed);
                    _worldSyncBarrierActive = true;
                    _clientQuiescencePending = true;
                    _clientQuiescenceCleanFrames = 0;
                    _clientQuiescedEpoch = 0;
                    SyncInbox.DrainAll();
                    Diagnostics.ResyncArbiter.Reset();
                    SetPhase(ClientWorldPhase.WaitingForMap);
                    _log.Info("[MP] World sync epoch " + epoch +
                              " began; local gameplay is paused while native transactions drain.");
                    Diagnostics.FlightRecorder.Note(
                        "world-sync client draining native work epoch=" + epoch);
                }
                MaintainWorldSyncBarrier();
                if (_clientQuiescedEpoch == epoch)
                    _session.SendWorldSyncStage(epoch, WorldSyncStage.Quiesced);
                return;
            }

            if (!_worldSyncBarrierActive || epoch != _activeWorldSyncEpoch) return;

            if (stage == WorldSyncStage.Resume)
            {
                _worldSyncResumeSpeed = SanitizeSpeed(resumeSpeed);
                if (_phase != ClientWorldPhase.WaitingForResume)
                {
                    Diagnostics.SyncLog.ProdError(
                        "World sync: the host finished epoch " + epoch +
                        " before this city had installed its snapshot. Asking for the world again.");
                    ResetWorldSyncState(restoreSpeed: false);
                    SetPhase(ClientWorldPhase.WaitingForMap);
                    // Deliberately NOT the resync arbiter. Two things would swallow it: the session
                    // is still inside its epoch at this point and coalesces the request away, and
                    // the WaitingForMap phase set on the line above makes WorldRecoveryInFlight
                    // true, which is read as "a reload is already running". Neither is the case -
                    // nothing is coming - so the client sat in WaitingForMap for the rest of the
                    // session, waiting for a world nobody had been asked for.
                    RequestMapAgainNextTick();
                    return;
                }

                if (_worldInstallGeneration < long.MaxValue) _worldInstallGeneration++;
                ResetWorldSyncState(restoreSpeed: true);
                SetPhase(ClientWorldPhase.InSession);
                // The player watched the world reload and the simulation stop; say plainly that
                // it is over, rather than leaving them to infer it from the clock moving again.
                _session.NotifyChat(null, "World sync complete - your city matches the host's.");
                _log.Info("[MP] World sync epoch " + epoch +
                          " resumed after the authoritative snapshot was installed.");
                Diagnostics.FlightRecorder.Note("world-sync client resumed epoch=" + epoch);
                return;
            }

            if (stage == WorldSyncStage.Abort)
            {
                bool canResumeOldWorld = _worldSyncHadUsableWorld;
                _worldSyncResumeSpeed = SanitizeSpeed(resumeSpeed);
                ResetWorldSyncState(restoreSpeed: canResumeOldWorld);
                SetPhase(canResumeOldWorld ? ClientWorldPhase.InSession : ClientWorldPhase.WaitingForMap);
                _log.Warn("[MP] Host aborted world-sync epoch " + epoch + ".");
            }
        }

        /// <summary>Keep pause/tool quiescence enforced even if a state apply or map load resets it.</summary>
        private void MaintainWorldSyncBarrier()
        {
            if (!_worldSyncBarrierActive || _currentWorld == null) return;
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
                // Worlds are replaced between UI frames. A stale World reference is expected for
                // that one boundary frame; the next MultiplayerSystem supplies the new instance.
                Mod.Verbose("[MP] Could not enforce world-sync pause on this frame: " + ex.Message);
            }
        }

        /// <summary>
        /// A client acknowledges Begin only after its already-scheduled native transaction has
        /// actually left Temp state. Loading a replacement world while that graph is still being
        /// applied would recreate the same cleanup race the distributed barrier is meant to close.
        /// </summary>
        private void PumpClientWorldSyncQuiescence()
        {
            if (!_clientQuiescencePending || !_worldSyncBarrierActive ||
                _session.Role != SessionRole.Client || _activeWorldSyncEpoch <= 0)
                return;

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
                // A world is replaced only after this acknowledgement. Until then, inability to
                // inspect its native pipeline is not evidence that the pipeline is safe.
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
            _log.Info("[MP] World sync epoch " + epoch +
                      ": local native transactions drained; quiescence acknowledged.");
            Diagnostics.FlightRecorder.Note("world-sync client quiesced epoch=" + epoch);
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
                Mod.Verbose("[MP] Could not restore simulation speed after world sync: " + ex.Message);
            }
        }

        /// <summary>
        /// The speed to restore once a sync finishes. It arrives over the wire, so the upper
        /// bound matters as much as the lower one: the game's own selector tops out at 3, and a
        /// peer that sends a large finite value would otherwise have the simulation resume at it.
        /// </summary>
        private static float SanitizeSpeed(float speed) =>
            float.IsNaN(speed) || float.IsInfinity(speed) || speed < 0f
                ? 0f
                : Math.Min(speed, MaxResumeSpeed);

        /// <summary>Generous ceiling on a restored speed - well above the selector's 3.</summary>
        private const float MaxResumeSpeed = 8f;
    }
}
