using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Infrastructure;
namespace CS2MultiplayerMod.Game.Sync.Systems.Net
{
    // Commit orchestration for NetSyncSystem. A remote net operation includes the objects and areas
    // its native generation updates as side effects; the complete local preview graph is temporarily
    // Disabled so an unrelated tool can remain selected without either transaction consuming the
    // other one's entities.
    // Commit orchestration for NetSyncSystem. A remote net operation includes the objects and areas
    // its native generation updates as side effects; the complete local preview graph is temporarily
    // Disabled so an unrelated tool can remain selected without either transaction consuming the
    // other one's entities.
    //
    // This file holds the per-frame cycle and the state the rest reads: the realize frame, the
    // commit windows, and whether definitions can be built right now. The work it drives is split
    // across the sibling Apply*.cs files - temps, commit, drain, the three transaction validators,
    // owner resolution, topology checks, and arming.
    public partial class NetSyncSystem
    {
        /// <summary>How long an armed batch may wait for its commit before it is discarded and re-queued.</summary>
        private const int ApplyWindowMs = 3000;

        /// <summary>
        /// How long a committed batch's Temps may linger before recovery is considered.
        ///
        /// This is the base for a SMALL batch. The native apply pass is per-entity work, so a fixed
        /// window silently means "the bigger the edit, the less time it gets": a 311-Temp road
        /// replacement was quarantined here on the same three seconds that comfortably drained the
        /// 55- and 86-Temp batches around it. See <see cref="DrainWindowFor"/>.
        /// </summary>
        private const int DrainWindowMs = 3000;

        /// <summary>Extra drain time per tracked Temp, on top of <see cref="DrainWindowMs"/>.</summary>
        private const int DrainWindowMsPerTemp = 12;

        /// <summary>Ceiling, so a pathological batch still reaches a verdict.</summary>
        private const int MaxDrainWindowMs = 15000;

        /// <summary>
        /// The drain budget for a batch of <paramref name="temps"/> entities. Linear in the work
        /// the native pipeline actually has to do, and capped.
        /// </summary>
        private static int DrainWindowFor(int temps)
        {
            long budget = DrainWindowMs + (long)DrainWindowMsPerTemp * (temps > 0 ? temps : 0);
            return budget > MaxDrainWindowMs ? MaxDrainWindowMs : (int)budget;
        }

        /// <summary>
        /// A wall-clock stall is not evidence that the native pipeline is stuck. Observe several
        /// complete update frames before quarantining a committed graph.
        /// </summary>
        private const int MinimumDrainFramesBeforeRecovery = 8;

        /// <summary>
        /// Seeing no tracked Temp once is not enough to start another native transaction: deferred
        /// structural work from the completed apply can still play later in that update. Keep the
        /// coordinator closed until two consecutive ToolUpdate observations see a clean graph.
        /// </summary>
        private const int RequiredCleanDrainFrames = 2;

        /// <summary>
        /// Called by <see cref="SyncRealizeSystem"/> once per frame BEFORE any net-pipeline feeder
        /// (delete/replace/build) runs, so per-frame state is reset exactly once regardless of which
        /// feeder acts first.
        /// </summary>
        public void BeginRealizeFrame()
        {
            // A drain released during the prior ToolUpdate has now been separated from all new
            // native work by the rest of that frame. Re-open the coordinator before feeders run.
            _drainReleasedThisFrame = false;
            _prepDoneThisFrame = false;
            _realizeFrame++;
            // Last frame's commit-frame capture skip has served its purpose (the one-frame
            // Created tags it targeted are gone); a commit this frame re-sets it below.
            _suppressCaptureThisFrame = false;
            _objectCommitThisFrame = false;
            ProtectRemoteBatchForLocalToolOutput();
        }

        /// <summary>
        /// True on frames with an armed, not-yet-applied commit - the frames where
        /// <see cref="DefinitionGateSystem"/> must destroy the tool's freshly buffered definitions
        /// before they can materialise beside the isolated remote batch. The flag clears before the
        /// gate on the actual commit frame, so the player's preview resumes immediately afterward.
        /// </summary>
        public bool HasArmedToolCommit => _pendingApply;

        /// <summary>True only on the ToolUpdate frame an isolated object graph was committed.</summary>
        public bool DidCommitObjectGraphThisFrame => _objectCommitThisFrame;

        /// <summary>
        /// Called by <see cref="SyncRealizeSystem"/> during the ToolUpdate phase, where the
        /// NetCourse definition is consumed by <c>GenerateNodesSystem</c>/<c>GenerateEdgesSystem</c>
        /// in the same frame's Modification1/2 - created any later it would be silently
        /// discarded (see <see cref="SyncRealizeSystem"/>).
        /// </summary>
        public void RealizePending()
        {
            MultiplayerService service = Mod.Service;
            if (service == null) return;

            bool gameplayReady = service.GameplaySyncReady;
            if (!gameplayReady)
            {
                // Begin closes admission synchronously, but a graph armed by the preceding
                // ToolUpdate already belongs to the world. Keep advancing only that graph until it
                // has committed and left Temp state; cancelling it here races native generation and
                // cleanup, while returning early strands it forever behind the barrier.
                DrainNetQueues();
                if (!service.WorldSyncBarrierActive || !IsCommitBusy)
                {
                    PruneRecentRealizedSpans();
                    return;
                }
            }

            // A rejected uncommitted graph may only be tagged Deleted, while a committed graph that
            // missed its drain window is deliberately left untouched. In both cases, keep native
            // work blocked until the exact tracked Temps disappear; rebuilding beside either graph
            // can make the apply pipeline consume stale ownership/connectivity state.
            if (_invalidatedBatchDraining)
            {
                // A replay is new native work. Recovery only waits for the old quarantined graph;
                // the authoritative snapshot makes replaying it unnecessary once the barrier opens.
                PumpInvalidatedBatchDrain(allowReplay: gameplayReady);
                PruneRecentRealizedSpans();
                return;
            }

            // Definitions created on the prior ToolUpdate have now become remote Temp net entities.
            // A quiet/preview-clear frame applies only that enabled net set. On a local Apply frame,
            // BeginRealizeFrame protects the remote set instead and this transaction waits intact.
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
                // Progress is progress. A large graph leaves Temp state in waves, and the window
                // exists to catch a pipeline that has STOPPED, not one that is merely slow. Every
                // frame that retires at least one Temp buys the rest of the batch a fresh window.
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
                    // Apply has already been scheduled for this graph. Tagging its entities Deleted
                    // here races the native apply/cleanup jobs and was the immediate precursor to a
                    // process crash. Leave the graph intact, keep its identities quarantined, and
                    // let the native pipeline drain it before recovery or any later transaction.
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

                    // The graph is quarantined either way - it may not be touched while native work
                    // is still scheduled against it. Whether that costs a world reload is a
                    // separate question, and one a stall alone does not answer.
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

            // Even the observation which releases a drain is a fence frame. RealizeIncoming and
            // sibling systems later in ToolUpdate must wait until BeginRealizeFrame re-opens the
            // coordinator on the following frame.
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

        /// <summary>
        /// A remote operation is resolved and armed inside a single frame, so a slow cycle is a
        /// visible stutter for the player who did not draw it. Report it with the two numbers that
        /// explain the cost - how many courses the operation carried and how much of the city the
        /// snapshot held - instead of leaving it to be inferred from a frame-rate complaint.
        /// </summary>
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
        /// True while a net-Temp commit is armed, draining, or held through its release ToolUpdate.
        /// Only one batch (build OR delete OR replace) enters any one net-domain pass - a split
        /// course and a delete of the same edge in the same commit can make ApplyNetSystem
        /// dereference a stale edge and native-crash.
        /// </summary>
        public bool IsCommitBusy => _pendingApply || _awaitingDrain || _invalidatedBatchDraining ||
                                    _drainReleasedThisFrame;

        /// <summary>
        /// Host recovery may take its save only after no armed/committing/quarantined remote native
        /// graph remains. Deleted-but-not-destroyed Temps are intentionally still considered live.
        /// </summary>
        public bool IsRecoveryQuiescent => !IsCommitBusy && !TrackedInvalidatedTempsRemain();

        /// <summary>
        /// True until queued placement courses and their commit/drain have become queryable network
        /// geometry. Systems that attach to or edit roads must not overtake this boundary.
        /// </summary>
        public bool HasPlacementBacklog => !_incoming.IsEmpty || _remoteDeferred.Count > 0 || IsCommitBusy;

        /// <summary>
        /// True when a feeder may create Temp-backed work. An interactive tool may stay selected and
        /// may continuously regenerate/clear its preview; only an actual city Apply gets priority.
        /// </summary>
        public bool CanBuildDefinitions
        {
            get
            {
                if (IsCommitBusy) return false;
                global::Game.Tools.ToolBaseSystem tool = _toolSystem != null ? _toolSystem.activeTool : null;
                // Clear is preview maintenance/cancellation, not a permanent city edit. Net tools
                // use it repeatedly while the cursor moves, so blocking here would starve remote
                // roads until the other player stopped drawing. Only an actual Apply gets priority.
                return tool == null || tool is global::Game.Tools.DefaultToolSystem ||
                       tool.applyMode != global::Game.Tools.ApplyMode.Apply;
            }
        }

        /// <summary>
        /// Isolate the complete active preview before remote definitions materialise. A building or
        /// network preview may span object, node, edge, and lane entities owned by one another; it
        /// must be frozen and restored as a unit.
        /// </summary>
        public void PrepareDefinitionFrame()
        {
            if (_prepDoneThisFrame) return;
            _prepDoneThisFrame = true;

            if (_isolatedLocalTemps.Count > 0) ReleaseTrackedTemps(_isolatedLocalTemps);
            DisableQueryEntities(_standingTemps, _isolatedLocalTemps);
            if (_isolatedLocalTemps.Count > 0)
                SyncLog.Trace(LogTopic.Nets, "tool preview isolated=" + _isolatedLocalTemps.Count);
        }

        /// <summary>
        /// Roll back a definition-frame reservation when its caller could not create or arm any
        /// transaction. No remote graph exists yet, so restoring the exact isolated preview set is
        /// sufficient and keeps the active tool responsive.
        /// </summary>
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
