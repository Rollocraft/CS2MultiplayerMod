using Unity.Entities;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Infrastructure;
namespace CS2MultiplayerMod.Game.Sync.Systems.Net
{
    // Commit orchestration. The local preview graph is Disabled while a remote operation commits, so
    // neither transaction consumes the other's entities. The work is split across the Apply*.cs files.
    public partial class NetSyncSystem
    {
        /// <summary>How long an armed batch may wait for its commit before it is discarded and re-queued.</summary>
        private const int ApplyWindowMs = 3000;

        /// <summary>Base drain window for a small batch; scales with size, see <see cref="DrainWindowFor"/>.</summary>
        private const int DrainWindowMs = 3000;

        /// <summary>Extra drain time per tracked Temp, on top of <see cref="DrainWindowMs"/>.</summary>
        private const int DrainWindowMsPerTemp = 12;

        /// <summary>Ceiling, so a pathological batch still reaches a verdict.</summary>
        private const int MaxDrainWindowMs = 15000;

        /// <summary>Linear in the batch's Temp count, capped.</summary>
        private static int DrainWindowFor(int temps)
        {
            long budget = DrainWindowMs + (long)DrainWindowMsPerTemp * (temps > 0 ? temps : 0);
            return budget > MaxDrainWindowMs ? MaxDrainWindowMs : (int)budget;
        }

        /// <summary>A wall-clock stall is not proof; wait several frames before quarantining.</summary>
        private const int MinimumDrainFramesBeforeRecovery = 8;

        /// <summary>
        /// Deferred structural work can still play after an apply; require two clean ToolUpdates before
        /// the next transaction.
        /// </summary>
        private const int RequiredCleanDrainFrames = 2;

        /// <summary>Once per frame before any net feeder runs.</summary>
        public void BeginRealizeFrame()
        {
            // A drain released last ToolUpdate has been fenced by the rest of that frame.
            _drainReleasedThisFrame = false;
            _prepDoneThisFrame = false;
            _realizeFrame++;
            _suppressCaptureThisFrame = false;
            _objectCommitThisFrame = false;
            ProtectRemoteBatchForLocalToolOutput();
        }

        /// <summary>
        /// An armed, not-yet-applied commit: <see cref="DefinitionGateSystem"/> must destroy the tool's
        /// buffered definitions. Clears before the gate on the commit frame.
        /// </summary>
        public bool HasArmedToolCommit => _pendingApply;

        /// <summary>
        /// The remote batch stood down so this frame's local Apply commits; capture must publish it.
        /// </summary>
        public bool LocalToolOutputProtectedThisFrame => _localToolOutputProtectedThisFrame;

        /// <summary>True only on the ToolUpdate frame an isolated object graph was committed.</summary>
        public bool DidCommitObjectGraphThisFrame => _objectCommitThisFrame;

        /// <summary>
        /// ToolUpdate: the NetCourse is consumed in the same frame's Modification1/2; later it is discarded.
        /// </summary>
        public void RealizePending()
        {
            MultiplayerService service = Mod.Service;
            if (service == null) return;

            bool gameplayReady = service.GameplaySyncReady;
            if (!gameplayReady)
            {
                // A graph armed last ToolUpdate already belongs to the world: keep advancing it until it leaves
                // Temp. Cancelling races native generation; returning strands it behind the barrier.
                DrainNetQueues();
                if (!service.WorldSyncBarrierActive || !IsCommitBusy)
                {
                    PruneRecentRealizedSpans();
                    return;
                }
            }

            // Keep native work blocked until a quarantined graph's tracked Temps disappear.
            if (_invalidatedBatchDraining)
            {
                // Replaying is new work; during recovery the snapshot makes it unnecessary.
                PumpInvalidatedBatchDrain(allowReplay: gameplayReady);
                PruneRecentRealizedSpans();
                return;
            }

            // Last ToolUpdate's definitions are Temps now. A quiet frame applies them; on a local Apply frame
            // they wait intact.
            if (_pendingApply && !_localToolOutputProtectedThisFrame)
            {
                EntityQuery transactionQuery = ActiveTransactionQuery();
                int isolatedCount = transactionQuery.CalculateEntityCount();
                string invalidReason;
                bool valid;
                _validateStartTick = System.Environment.TickCount;
                if (IsObjectGraphTransaction(_pendingTransactionKind))
                    valid = ValidateArmedObjectTransaction(out invalidReason);
                else if (IsRouteTransaction(_pendingTransactionKind))
                    valid = ValidateArmedRouteTransaction(out invalidReason);
                else
                    valid = ValidateArmedNetTransaction(out invalidReason);
                if (isolatedCount > 0 && !valid)
                {
                    InvalidateArmedBatch(invalidReason, isolatedCount);
                }
                else if (isolatedCount > 0)
                {
                    CommitRemoteTemps(transactionQuery, isolatedCount);
                }
                else if (System.Environment.TickCount - _armTick > ApplyWindowMs)
                {
                    InvalidateArmedBatch("apply window expired before the batch materialised", isolatedCount);
                }
            }
            else if (_awaitingDrain)
            {
                _drainFrames++;
                int remainingTemps = CountCommittedRemoteTempsRemaining();
                bool committedTempsRemain = remainingTemps > 0;
                // Any retired Temp buys the rest a fresh window: the window catches a stopped pipeline, not a slow one.
                if (remainingTemps < _drainRemainingTemps)
                {
                    _drainRemainingTemps = remainingTemps;
                    _drainArmTick = System.Environment.TickCount;
                }
                if (!committedTempsRemain)
                {
                    if (++_drainCleanFrames >= RequiredCleanDrainFrames)
                    {
                        ChargeCommittedNetConstruction();
                        _committingRemoteNetTemps.Clear();
                        _awaitingDrain = false;
                        _drainCleanFrames = 0;
                        _committingTransactionKind = RemoteToolTransactionKind.None;
                        _drainReleasedThisFrame = true;
                        System.Action completed = _onCommitComplete;
                        _onCommitComplete = null;
                        if (completed != null) completed();
                        SyncLog.Trace(LogTopic.Nets,
                            "remote transaction drain completed after clean-frame fence");
                        WithdrawDrainReport("the batch drained on its own");
                    }
                }
                else
                {
                    _drainCleanFrames = 0;
                }

                if (committedTempsRemain &&
                    _drainFrames >= MinimumDrainFramesBeforeRecovery &&
                    System.Environment.TickCount - _drainArmTick >
                        DrainWindowFor(_committingRemoteNetTemps.Count))
                {
                    int trackedCount = _committingRemoteNetTemps.Count;
                    TrackInvalidatedTemps(_committingRemoteNetTemps);
                    // Apply is already scheduled: tagging Deleted here races native jobs and crashed the process.
                    // Leave the graph intact and quarantined until it drains.
                    ReleaseTrackedTemps(_protectedRemoteNetTemps);
                    _committingRemoteNetTemps.Clear();
                    _committingNetConstructionCharge = 0;
                    _committingNetConstructionChargeCourses = 0;
                    _awaitingDrain = false;
                    _drainCleanFrames = 0;
                    _committingTransactionKind = RemoteToolTransactionKind.None;
                    _onCommitComplete = null;
                    _replayAfterInvalidatedDrain = null;
                    _invalidatedBatchDraining = true;
                    _invalidatedDrainArmTick = System.Environment.TickCount;
                    _invalidatedCleanFrames = 0;
                    _invalidatedDrainTimedOut = false;
                    SyncLog.Trace(LogTopic.Nets, "net isolated commit quarantined frames=" +
                        _drainFrames + " tracked=" + trackedCount + " remaining=" + remainingTemps);

                    // Quarantined either way; whether that costs a reload is the arbiter's call.
                    string drainSubject = "commit of " + trackedCount + " entities";
                    NoteDrainReport(drainSubject);
                    SyncInbox.RequestResync(Diagnostics.ResyncReport
                        .Create(DrainFailedReason, "net", Diagnostics.ResyncEvidence.Timeout)
                        .About(drainSubject)
                        .Tried("waited " + _drainFrames + " frames and " +
                               DrainWindowFor(trackedCount) + " ms, restarting the window on every " +
                               "frame that retired at least one entity")
                        .Fact("entities in the commit", trackedCount)
                        .Fact("still not applied", remainingTemps)
                        .Fact("frames waited", _drainFrames));
                }
            }

            PruneRecentRealizedSpans();

            // The releasing frame is still a fence; the coordinator reopens next frame.
            if (!gameplayReady || IsCommitBusy) return;

            MultiplayerSession session = service.Session;
            long startTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            _rzCycleCourses = 0;
            _rzCyclePool = 0;
            RealizeIncoming(session, service.NowMs);
            ReportSlowRealizeCycle(startTicks);
        }

        /// <summary>How long one realize cycle may take before it is worth a log line.</summary>
        private const double SlowRealizeCycleMs = 25d;

        /// <summary>A slow realize cycle is a stutter for the other player; report its course count and snapshot size.</summary>
        private void ReportSlowRealizeCycle(long startTicks)
        {
            double elapsedMs = (System.Diagnostics.Stopwatch.GetTimestamp() - startTicks) * 1000d /
                               System.Diagnostics.Stopwatch.Frequency;
            if (elapsedMs < SlowRealizeCycleMs) return;
            SyncLog.Detail(LogTopic.Nets, "NetSync realize cycle took " + elapsedMs.ToString("F0") +
                " ms (" + _rzCycleCourses + " course(s), " + _rzCyclePool +
                " indexed net entities).");
        }

        /// <summary>
        /// A net commit is armed, draining, or held. One batch (build, delete or replace) per pass: a split
        /// and a delete of one edge together can crash ApplyNetSystem natively.
        /// </summary>
        public bool IsCommitBusy => _pendingApply || _awaitingDrain || _invalidatedBatchDraining ||
                                    _drainReleasedThisFrame;

        /// <summary>No armed, committing or quarantined graph remains; Deleted Temps still count as live.</summary>
        public bool IsRecoveryQuiescent => !IsCommitBusy && !TrackedInvalidatedTempsRemain();

        /// <summary>Queued courses have not yet become queryable geometry; road-dependent work waits.</summary>
        public bool HasPlacementBacklog => !_incoming.IsEmpty || _remoteDeferred.Count > 0 || IsCommitBusy;

        /// <summary>A feeder may create Temp work; only a local Apply frame has priority.</summary>
        public bool CanBuildDefinitions
        {
            get
            {
                if (IsCommitBusy) return false;
                global::Game.Tools.ToolBaseSystem tool = _toolSystem != null ? _toolSystem.activeTool : null;
                // Clear is preview maintenance the net tool repeats while the cursor moves.
                return tool == null || tool is global::Game.Tools.DefaultToolSystem ||
                       tool.applyMode != global::Game.Tools.ApplyMode.Apply;
            }
        }

        /// <summary>Isolates the whole active preview (objects, nodes, edges, lanes) as a unit.</summary>
        public void PrepareDefinitionFrame()
        {
            if (_prepDoneThisFrame) return;
            _prepDoneThisFrame = true;

            if (_isolatedLocalTemps.Count > 0) ReleaseTrackedTemps(_isolatedLocalTemps);
            DisableQueryEntities(_standingTemps, _isolatedLocalTemps);
            if (_isolatedLocalTemps.Count > 0)
                SyncLog.Trace(LogTopic.Nets, "tool preview isolated=" + _isolatedLocalTemps.Count);
        }

        /// <summary>Rolls back a reservation that armed nothing, restoring the isolated preview.</summary>
        public void CancelPreparedDefinitionFrame()
        {
            if (IsCommitBusy) return;
            if (_isolatedLocalTemps.Count > 0)
            {
                ReleaseTrackedTemps(_isolatedLocalTemps);
                ForceActiveToolUpdate();
            }
            _prepDoneThisFrame = false;
        }
    }
}
