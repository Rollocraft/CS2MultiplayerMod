using System.Collections.Generic;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;

using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Infrastructure;
namespace CS2MultiplayerMod.Game.Sync.Systems.Net
{
    // After invalidation: track the leftover Temps until drained, hold the resync report meanwhile
    // and withdraw it on completion, then release isolation.
    public partial class NetSyncSystem
    {
        /// <summary>The reason a stalled drain reports, shared by the report and its withdrawal.</summary>
        internal const string DrainFailedReason = "remote transaction failed to drain";

        /// <summary>
        /// Outstanding "failed to drain" reports; a graph can raise one per window, and all are withdrawn.
        /// </summary>
        private readonly List<string> _outstandingDrainSubjects = new List<string>();

        private void NoteDrainReport(string subject)
        {
            if (!_outstandingDrainSubjects.Contains(subject))
                _outstandingDrainSubjects.Add(subject);
        }

        /// <summary>A late drain means the window was too short, not a reason to reload.</summary>
        private void WithdrawDrainReport(string outcome)
        {
            if (_outstandingDrainSubjects.Count == 0) return;
            long now = Mod.Service != null ? Mod.Service.NowMs : 0L;
            for (int i = 0; i < _outstandingDrainSubjects.Count; i++)
                Diagnostics.ResyncArbiter.Withdraw("net", DrainFailedReason,
                    _outstandingDrainSubjects[i], now, outcome);
            _outstandingDrainSubjects.Clear();
        }

        /// <summary>The comparable part of a rejection; the entity detail differs between replays.</summary>
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
                    Diagnostics.SyncLog.Error(LogTopic.Nets,
                        "Road sync: a rejected road transaction is still held by the game's own " +
                        "apply pass; no further road work can run until it finishes.");
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

            // Two clean observations keep cleanup and the next graph in different native frames.
            if (++_invalidatedCleanFrames < RequiredCleanDrainFrames) return;

            System.Action replay = allowReplay ? _replayAfterInvalidatedDrain : null;
            _replayAfterInvalidatedDrain = null;
            _invalidatedRemoteTemps.Clear();
            _invalidatedBatchDraining = false;
            _invalidatedCleanFrames = 0;
            _invalidatedDrainTimedOut = false;
            _drainReleasedThisFrame = true;
            SyncLog.Trace(LogTopic.Nets, "invalidated net transaction fully drained");
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
                // ToolOutputSystem's own dispatch: Clear is safe after the isolated brush pass, Apply would rerun it.
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
