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
    // Committing an armed remote batch, recording what the transaction was made of for the log,
    // and invalidating a batch that cannot be committed.
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
                    // Preserve the native ApplyTool domain order. Owner resolution in the object
                    // pass must run before its owned connector nets and lot areas are committed.
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
                    // Net generation creates update Temps for objects attached to every touched
                    // node/edge. Apply them first so their parent references resolve while the Temp
                    // net graph is intact, matching the normal ApplyTool domain order.
                    if (hasObjectTemps) _applyObjectsSystem.Update();
                    _applyNetSystem.Update();
                    if (hasAreaTemps) _applyAreasSystem.Update();
                    if (hasObjectTemps) _objectCommitThisFrame = true;
                }
            }
            catch (System.Exception ex)
            {
                Diagnostics.FlightRecorder.Note("net isolated apply failed: " + ex.GetType().Name);
                InvalidateArmedBatch("isolated apply failed (" + ex.GetType().Name + ")", count);
                return;
            }

            // A moving tool commonly drives the global Clear pass every frame to replace its
            // preview. The isolated apply jobs have already consumed this remote graph; hide it
            // until ToolOutputBarrier so the later generic clear cannot cancel the same transaction.
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
                Diagnostics.FlightRecorder.Note("net commit shielded from preview clear temps=" +
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
            Diagnostics.FlightRecorder.Note("remote " +
                _committingTransactionKind.ToString().ToLowerInvariant() +
                " commit isolated (temps=" + count + ") validateMS=" + validateMs +
                " applyMS=" + (System.Environment.TickCount - applyStartTick));
        }

        /// <summary>Members named individually before the composition line is truncated.</summary>
        private const int MaxNotedTransactionMembers = 64;

        /// <summary>
        /// Record what an isolated apply pass is about to consume - shape, <see cref="TempFlags"/>
        /// and original per member - immediately before the native call. That call can end the
        /// process without unwinding, so this line is the only surviving description of the batch.
        ///
        /// The apply passes dereference the originals that nodes and edges name; lanes are the bulk
        /// of a large batch and explain nothing. Name the structural members first, so a batch far
        /// over the cap still describes the part a crash would have come from. A commit of 732
        /// members spent its whole budget on lanes and left every edge and node unnamed.
        /// </summary>
        private void NoteTransactionComposition(RemoteToolTransactionKind kind, List<Entity> members)
        {
            if (!Diagnostics.FlightRecorder.Enabled) return;

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
                // Two members naming one original is the shape the apply passes dereference without
                // a liveness check. Nothing rejects the batch for it yet - count it so a crash here
                // can be read off the log instead of reconstructed.
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

            Diagnostics.FlightRecorder.Note("commit composition kind=" +
                kind.ToString().ToLowerInvariant() + " temps=" + members.Count +
                " edge=" + edges + " node=" + nodes + " lane=" + lanes +
                " aggr=" + aggregates + " obj=" + objects + " area=" + areas +
                " deletedTag=" + deletedTagged + " missing=" + missing +
                " sharedOriginal=" + sharedOriginals + " members=[" + detail + "]");
        }

        /// <summary>
        /// How many of the committed batch's entities are still Temp.
        ///
        /// The count, not just "any": it is what tells a stuck pipeline apart from a slow one, and
        /// it is what the quarantine line used to be missing - it reported the batch size, so a
        /// graph that was one entity from done and one that had not moved at all logged the same
        /// number.
        /// </summary>
        private int CountCommittedRemoteTempsRemaining()
        {
            int remaining = 0;
            for (int i = 0; i < _committingRemoteNetTemps.Count; i++)
            {
                Entity entity = _committingRemoteNetTemps[i];
                // Deleted is only a request to the deferred cleanup pipeline. Treating that tag as
                // "gone" allowed the next native transaction to reuse a graph still being torn down.
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

            // A replay rebuilds the identical command against an unchanged world. Once an attempt
            // has already been spent and the rejection repeats, the remaining attempts are latency
            // in front of an unavoidable recovery, not another chance. Compare the reason alone:
            // the member count comes from a world-wide Temp query, so unrelated concurrent work
            // (a growable spawning, another peer's edit) moves it between two identical rejections.
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
                Mod.log.Warn("[MP] NetApply: " + reason + "; draining rejected Temps before " +
                             "re-queueing batch (attempt " +
                             _applyReplayBudget.AttemptsUsed + "/" +
                             _applyReplayBudget.MaximumAttempts + ").");
                Diagnostics.FlightRecorder.Note("net batch invalidated; drain then replay " +
                    _applyReplayBudget.AttemptsUsed + "/" + _applyReplayBudget.MaximumAttempts);
            }
            else
            {
                _replayAfterInvalidatedDrain = null;
                Mod.log.Warn("[MP] NetApply: " + reason + "; batch dropped" +
                             (repeatsPreviousAttempt
                                 ? " - the rejection repeated unchanged, so further replays cannot " +
                                   "succeed."
                                 : replay != null
                                     ? " after " + _applyReplayBudget.AttemptsUsed + " replays."
                                     : "."));
                Diagnostics.FlightRecorder.Note("net batch invalidated; dropped" +
                    (repeatsPreviousAttempt ? " (rejection is deterministic)" : string.Empty));
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
