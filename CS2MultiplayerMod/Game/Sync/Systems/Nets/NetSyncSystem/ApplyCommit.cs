using System.Collections.Generic;
using Game.Common;
using Game.Net;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;

using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Infrastructure;
namespace CS2MultiplayerMod.Game.Sync.Systems.Net
{
    // Committing an armed remote batch, logging its composition, and invalidating one that cannot commit.
    public partial class NetSyncSystem
    {
        private void CommitRemoteTemps(EntityQuery transactionQuery, int count)
        {
            MultiplayerService currentService = Mod.Service;
            if (_pendingTransactionKind == RemoteToolTransactionKind.Net)
                RecordPlacementOriginals(currentService != null ? currentService.NowMs : 0);

            int validateMs = System.Environment.TickCount - _validateStartTick;
            int applyStartTick = System.Environment.TickCount;
            _committingRemoteNetTemps.Clear();
            bool hasObjectTemps = false;
            bool hasAreaTemps = false;
            NativeArray<Entity> remoteTemps = transactionQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < remoteTemps.Length; i++)
                {
                    _committingRemoteNetTemps.Add(remoteTemps[i]);
                    hasObjectTemps |= EntityManager.HasComponent<global::Game.Objects.Object>(remoteTemps[i]);
                    hasAreaTemps |= EntityManager.HasComponent<global::Game.Areas.Area>(remoteTemps[i]);
                }
            }
            finally
            {
                remoteTemps.Dispose();
            }

            NoteTransactionComposition(_pendingTransactionKind, _committingRemoteNetTemps);

            try
            {
                if (IsObjectGraphTransaction(_pendingTransactionKind))
                {
                    // Native ApplyTool domain order: object owner resolution before its owned nets and areas.
                    _applyObjectsSystem.Update();
                    _applyNetSystem.Update();
                    _applyAreasSystem.Update();
                    _objectCommitThisFrame = true;
                }
                else if (IsRouteTransaction(_pendingTransactionKind))
                {
                    _applyRoutesSystem.Update();
                }
                else
                {
                    // Attached-object updates first, while the Temp net graph their parents reference is intact.
                    if (hasObjectTemps) _applyObjectsSystem.Update();
                    _applyNetSystem.Update();
                    if (hasAreaTemps) _applyAreasSystem.Update();
                    if (hasObjectTemps) _objectCommitThisFrame = true;
                }
            }
            catch (System.Exception ex)
            {
                SyncLog.Trace(LogTopic.Nets, "net isolated apply failed: " + ex.GetType().Name);
                InvalidateArmedBatch("isolated apply failed (" + ex.GetType().Name + ")", count);
                return;
            }

            // A moving tool runs Clear every frame; hide this consumed graph until ToolOutputBarrier so that
            // clear cannot cancel it.
            global::Game.Tools.ToolBaseSystem active = _toolSystem != null ? _toolSystem.activeTool : null;
            if (active != null && active.applyMode == global::Game.Tools.ApplyMode.Clear)
            {
                _protectedRemoteNetTemps.Clear();
                for (int i = 0; i < _committingRemoteNetTemps.Count; i++)
                {
                    Entity entity = _committingRemoteNetTemps[i];
                    if (!EntityManager.Exists(entity) || EntityManager.HasComponent<Disabled>(entity))
                        continue;
                    EntityManager.AddComponent<Disabled>(entity);
                    _protectedRemoteNetTemps.Add(entity);
                }
                SyncLog.Trace(LogTopic.Nets, "net commit shielded from preview clear temps=" +
                    _protectedRemoteNetTemps.Count);
            }

            _pendingApply = false;
            _committingTransactionKind = _pendingTransactionKind;
            _pendingTransactionKind = RemoteToolTransactionKind.None;
            _committingNetConstructionCharge = _pendingNetConstructionCharge;
            _committingNetConstructionChargeCourses = _pendingNetConstructionChargeCourses;
            _pendingNetConstructionCharge = 0;
            _pendingNetConstructionChargeCourses = 0;
            _onCommitLost = null;
            _applyReplayBudget.Reset();
            _lastInvalidReason = null;
            _awaitingDrain = true;
            _drainArmTick = System.Environment.TickCount;
            _drainFrames = 0;
            _drainCleanFrames = 0;
            _drainRemainingTemps = int.MaxValue;
            _suppressCaptureThisFrame = true;
            _clearLocalNetIsolationAfterBarrier = true;
            SyncLog.Trace(LogTopic.Nets, "remote " +
                _committingTransactionKind.ToString().ToLowerInvariant() +
                " commit isolated (temps=" + count + ") validateMS=" + validateMs + " applyMS=" +
                (System.Environment.TickCount - applyStartTick));
        }

        /// <summary>Members named individually before the composition line is truncated.</summary>
        private const int MaxNotedTransactionMembers = 64;

        /// <summary>
        /// Logs what an isolated apply pass is about to consume, just before the native call that can end
        /// the process. Structural members come first; lanes explain nothing.
        /// </summary>
        private void NoteTransactionComposition(RemoteToolTransactionKind kind, List<Entity> members)
        {
            if (!SyncLog.IsRecording(LogTopic.Nets)) return;

            int edges = 0, nodes = 0, lanes = 0, aggregates = 0, objects = 0, areas = 0;
            int deletedTagged = 0, missing = 0, sharedOriginals = 0;
            var originals = new HashSet<Entity>();
            var structural = new System.Text.StringBuilder();
            var rest = new System.Text.StringBuilder();
            int structuralNamed = 0, restNamed = 0;
            for (int i = 0; i < members.Count; i++)
            {
                Entity entity = members[i];
                if (!EntityManager.Exists(entity)) { missing++; continue; }
                if (EntityManager.HasComponent<Deleted>(entity)) deletedTagged++;

                string shape;
                bool isStructural = false;
                if (EntityManager.HasComponent<Edge>(entity))
                { edges++; shape = "edge"; isStructural = true; }
                else if (EntityManager.HasComponent<Node>(entity))
                { nodes++; shape = "node"; isStructural = true; }
                else if (EntityManager.HasComponent<Lane>(entity)) { lanes++; shape = "lane"; }
                else if (EntityManager.HasComponent<Aggregate>(entity)) { aggregates++; shape = "aggr"; }
                else if (EntityManager.HasComponent<global::Game.Objects.Object>(entity))
                { objects++; shape = "obj"; }
                else if (EntityManager.HasComponent<global::Game.Areas.Area>(entity))
                { areas++; shape = "area"; }
                else shape = "other";

                Entity original = Entity.Null;
                TempFlags flags = default(TempFlags);
                if (EntityManager.HasComponent<Temp>(entity))
                {
                    Temp temp = EntityManager.GetComponentData<Temp>(entity);
                    original = temp.m_Original;
                    flags = temp.m_Flags;
                }
                // Two members sharing one original is the shape the apply passes dereference unchecked; counted.
                if (original != Entity.Null && !originals.Add(original)) sharedOriginals++;

                System.Text.StringBuilder sink = isStructural ? structural : rest;
                if ((isStructural ? structuralNamed : restNamed) >= MaxNotedTransactionMembers)
                    continue;
                if (isStructural) structuralNamed++; else restNamed++;
                if (sink.Length > 0) sink.Append(' ');
                sink.Append(shape).Append('#').Append(entity.Index)
                    .Append('[').Append(flags.ToString().Replace(", ", "|")).Append(']');
                if (original != Entity.Null) sink.Append(">#").Append(original.Index);
            }

            var detail = new System.Text.StringBuilder(structural.ToString());
            if (rest.Length > 0)
            {
                if (detail.Length > 0) detail.Append(' ');
                detail.Append(rest);
            }
            int unnamed = members.Count - missing - structuralNamed - restNamed;
            if (unnamed > 0) detail.Append(" +").Append(unnamed).Append(" more");

            SyncLog.Trace(LogTopic.Nets, "commit composition kind=" +
                kind.ToString().ToLowerInvariant() + " temps=" + members.Count + " edge=" + edges +
                " node=" + nodes + " lane=" + lanes + " aggr=" + aggregates + " obj=" + objects +
                " area=" + areas + " deletedTag=" + deletedTagged + " missing=" + missing +
                " sharedOriginal=" + sharedOriginals + " members=[" + detail + "]");
        }

        /// <summary>How many committed entities are still Temp: tells a stuck pipeline from a slow one.</summary>
        private int CountCommittedRemoteTempsRemaining()
        {
            int remaining = 0;
            for (int i = 0; i < _committingRemoteNetTemps.Count; i++)
            {
                Entity entity = _committingRemoteNetTemps[i];
                // Deleted is only a request; the graph is still being torn down.
                if (EntityManager.Exists(entity) && EntityManager.HasComponent<Temp>(entity))
                    remaining++;
            }
            return remaining;
        }

        private void InvalidateArmedBatch(string reason, int count)
        {
            TrackInvalidatedTemps(ActiveTransactionQuery());
            TrackInvalidatedTemps(_committingRemoteNetTemps);
            _pendingApply = false;
            _awaitingDrain = false;
            _drainCleanFrames = 0;
            _pendingNetConstructionCharge = 0;
            _pendingNetConstructionChargeCourses = 0;
            if (count > 0) DiscardStaleTransactionTemps(reason);
            _pendingTransactionKind = RemoteToolTransactionKind.None;
            _committingTransactionKind = RemoteToolTransactionKind.None;
            _committingRemoteNetTemps.Clear();
            ReleaseTrackedTemps(_isolatedLocalTemps);

            // A repeated rejection of an identical replay cannot succeed. Compare the reason only: the member
            // count comes from a world-wide Temp query.
            string identity = RejectionIdentity(reason);
            bool repeatsPreviousAttempt = _applyReplayBudget.AttemptsUsed > 0 &&
                                          identity == _lastInvalidReason;
            _lastInvalidReason = identity;

            System.Action replay = _onCommitLost;
            _onCommitLost = null;
            _onCommitComplete = null;
            if (replay != null && !repeatsPreviousAttempt && _applyReplayBudget.TryConsume())
            {
                _replayAfterInvalidatedDrain = replay;
                SyncLog.Warn(LogTopic.Nets, "NetApply: " + reason +
                    "; draining rejected Temps before " + "re-queueing batch (attempt " +
                    _applyReplayBudget.AttemptsUsed + "/" + _applyReplayBudget.MaximumAttempts +
                    ").");
            }
            else
            {
                _replayAfterInvalidatedDrain = null;
                SyncLog.Warn(LogTopic.Nets, "NetApply: " + reason + "; batch dropped" +
                    (repeatsPreviousAttempt ? " - the rejection repeated unchanged, so further replays cannot " + "succeed." : replay != null ? " after " + _applyReplayBudget.AttemptsUsed + " replays." : "."));
                SyncInbox.RequestResync(Diagnostics.ResyncReport
                    .Create(repeatsPreviousAttempt
                            ? "remote transaction rejected deterministically"
                            : "remote transaction exhausted bounded replays",
                        "net", Diagnostics.ResyncEvidence.Contradiction)
                    .About(identity)
                    .Tried(replay == null
                        ? "nothing - this batch had no way to be rebuilt"
                        : "rebuilt and re-applied the batch " + _applyReplayBudget.AttemptsUsed +
                          " time(s) out of " + _applyReplayBudget.MaximumAttempts)
                    .Fact("why the batch was refused", reason)
                    .Fact("entities in the batch", count)
                    .Fact("the same refusal repeated", repeatsPreviousAttempt));
            }

            _invalidatedBatchDraining = true;
            _invalidatedDrainArmTick = System.Environment.TickCount;
            _invalidatedCleanFrames = 0;
            _invalidatedDrainTimedOut = false;
        }
    }
}
