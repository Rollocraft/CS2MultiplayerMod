using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;

using CS2MultiplayerMod.Game.Sync.Infrastructure;
namespace CS2MultiplayerMod.Game.Sync.Systems.Net
{
    // Commit orchestration for NetSyncSystem. A remote net operation includes the objects and areas
    // its native generation updates as side effects; the complete local preview graph is temporarily
    // Disabled so an unrelated tool can remain selected without either transaction consuming the
    // other one's entities.
    // What happens after a batch is invalidated: the temps it left are tracked until the game has
    // drained them, the resync report is held while that is still in progress and withdrawn if the
    // drain completes, and isolation is released once the tool's own output is through.
    public partial class NetSyncSystem
    {
        /// <summary>The reason a stalled drain reports, shared by the report and its withdrawal.</summary>
        internal const string DrainFailedReason = "remote transaction failed to drain";

        /// <summary>
        /// What the outstanding "failed to drain" reports are about, so each can be withdrawn by
        /// name. A list, not one field: a graph that misses its commit window and then misses its
        /// quarantine window raises two, and withdrawing only the second would still reload the
        /// world for the first after the graph had actually finished.
        /// </summary>
        private readonly List<string> _outstandingDrainSubjects = new List<string>();

        private void NoteDrainReport(string subject)
        {
            if (!_outstandingDrainSubjects.Contains(subject))
                _outstandingDrainSubjects.Add(subject);
        }

        /// <summary>
        /// Take back every outstanding "failed to drain" report. A drain that finishes late is a
        /// window that was too short, and the log should say so instead of the world reloading.
        /// </summary>
        private void WithdrawDrainReport(string outcome)
        {
            if (_outstandingDrainSubjects.Count == 0) return;
            long now = Mod.Service != null ? Mod.Service.NowMs : 0L;
            for (int i = 0; i < _outstandingDrainSubjects.Count; i++)
                Diagnostics.ResyncArbiter.Withdraw("net", DrainFailedReason,
                    _outstandingDrainSubjects[i], now, outcome);
            _outstandingDrainSubjects.Clear();
        }

        /// <summary>
        /// The comparable part of a rejection. A replay regenerates the graph, so the appended
        /// entity detail can differ between two attempts that failed for exactly the same reason.
        /// </summary>
        private static string RejectionIdentity(string reason)
        {
            if (string.IsNullOrEmpty(reason)) return reason;
            int detail = reason.IndexOf(" (", System.StringComparison.Ordinal);
            return detail < 0 ? reason : reason.Substring(0, detail);
        }

        private void TrackInvalidatedTemps(EntityQuery query)
        {
            if (query.IsEmptyIgnoreFilter) return;
            NativeArray<Entity> entities = query.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                    TrackInvalidatedTemp(entities[i]);
            }
            finally
            {
                entities.Dispose();
            }
        }

        private void TrackInvalidatedTemps(List<Entity> entities)
        {
            for (int i = 0; i < entities.Count; i++) TrackInvalidatedTemp(entities[i]);
        }

        private void TrackInvalidatedTemp(Entity entity)
        {
            if (entity == Entity.Null || _invalidatedRemoteTemps.Contains(entity)) return;
            if (EntityManager.Exists(entity) && EntityManager.HasComponent<Temp>(entity))
                _invalidatedRemoteTemps.Add(entity);
        }

        private bool TrackedInvalidatedTempsRemain()
        {
            for (int i = 0; i < _invalidatedRemoteTemps.Count; i++)
            {
                Entity entity = _invalidatedRemoteTemps[i];
                if (EntityManager.Exists(entity) && EntityManager.HasComponent<Temp>(entity))
                    return true;
            }
            return false;
        }

        private void PruneInvalidatedTemps()
        {
            for (int i = _invalidatedRemoteTemps.Count - 1; i >= 0; i--)
            {
                Entity entity = _invalidatedRemoteTemps[i];
                if (!EntityManager.Exists(entity) || !EntityManager.HasComponent<Temp>(entity))
                    _invalidatedRemoteTemps.RemoveAt(i);
            }
        }

        private void PumpInvalidatedBatchDrain(bool allowReplay)
        {
            PruneInvalidatedTemps();
            if (TrackedInvalidatedTempsRemain())
            {
                _invalidatedCleanFrames = 0;
                if (!_invalidatedDrainTimedOut &&
                    System.Environment.TickCount - _invalidatedDrainArmTick > DrainWindowMs)
                {
                    _invalidatedDrainTimedOut = true;
                    _replayAfterInvalidatedDrain = null;
                    Diagnostics.SyncLog.ProdError(
                        "Road sync: a rejected road transaction is still held by the game's own " +
                        "apply pass; no further road work can run until it finishes.");
                    Diagnostics.FlightRecorder.Note(
                        "quarantined net temps failed to drain; native work remains blocked");
                    NoteDrainReport("quarantined graph");
                    SyncInbox.RequestResync(Diagnostics.ResyncReport
                        .Create(DrainFailedReason, "net", Diagnostics.ResyncEvidence.Timeout)
                        .About("quarantined graph")
                        .Tried("waited " + DrainWindowMs + " ms for the game's apply pass to " +
                               "release the rejected entities")
                        .Fact("entities still held", _invalidatedRemoteTemps.Count));
                }
                return;
            }

            // Require two observations with no surviving Temp. This keeps the cleanup structural
            // changes and the new definition graph in different native update frames.
            if (++_invalidatedCleanFrames < RequiredCleanDrainFrames) return;

            System.Action replay = allowReplay ? _replayAfterInvalidatedDrain : null;
            _replayAfterInvalidatedDrain = null;
            _invalidatedRemoteTemps.Clear();
            _invalidatedBatchDraining = false;
            _invalidatedCleanFrames = 0;
            _invalidatedDrainTimedOut = false;
            _drainReleasedThisFrame = true;
            Diagnostics.FlightRecorder.Note("invalidated net transaction fully drained");
            // It drained after all. Withdraw the report before its hold matures: the window was
            // too short for this batch, which is a tuning fact, not a reason to reload a world.
            WithdrawDrainReport("the game's apply pass finished the batch after the window expired");
            if (replay != null) replay();
        }

        /// <summary>Finish structural isolation after ToolOutputBarrier has consumed this frame.</summary>
        public void FinishIsolationAfterToolOutput()
        {
            if (_protectedRemoteNetTemps.Count > 0) ReleaseTrackedTemps(_protectedRemoteNetTemps);
            _localToolOutputProtectedThisFrame = false;

            if (_clearLocalNetIsolationAfterBarrier)
            {
                int cleared = ClearTrackedTemps(_isolatedLocalTemps, clearPreview: true);
                _isolatedLocalTemps.Clear();
                _clearLocalNetIsolationAfterBarrier = false;
                if (cleared > 0) ForceActiveToolUpdate();
            }

            if (_isolatedLocalBrushTemps.Count > 0)
            {
                ReleaseTrackedTemps(_isolatedLocalBrushTemps);
                ForceActiveToolUpdate();
            }
        }

        private void ReleaseAllIsolation()
        {
            ReleaseTrackedTemps(_protectedRemoteNetTemps);
            ReleaseTrackedTemps(_isolatedLocalTemps);
            ReleaseTrackedTemps(_isolatedLocalBrushTemps);
            _localToolOutputProtectedThisFrame = false;
            _clearLocalNetIsolationAfterBarrier = false;
        }

        public bool CanApplyAuxiliaryTemps
        {
            get
            {
                if (IsCommitBusy) return false;
                // Match ToolOutputSystem's own dispatch source. Clear only cleans Temp previews and
                // is safe after the isolated brush pass; Apply would run ApplyBrushesSystem again.
                return _toolSystem == null ||
                       _toolSystem.applyMode != global::Game.Tools.ApplyMode.Apply;
            }
        }

        public void PrepareAuxiliaryTemps()
        {
            if (_isolatedLocalBrushTemps.Count > 0) ReleaseTrackedTemps(_isolatedLocalBrushTemps);
            DisableQueryEntities(_localBrushTemps, _isolatedLocalBrushTemps);
        }
    }
}
