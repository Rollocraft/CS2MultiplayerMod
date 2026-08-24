using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using CS2MultiplayerMod.Core.Session;

using CS2MultiplayerMod.Game.Sync.Infrastructure;
namespace CS2MultiplayerMod.Game.Sync.Systems.Net
{
    // Commit orchestration for NetSyncSystem. A remote net operation includes the objects and areas
    // its native generation updates as side effects; the complete local preview graph is temporarily
    // Disabled so an unrelated tool can remain selected without either transaction consuming the
    // other one's entities.
    public partial class NetSyncSystem
    {
        /// <summary>How long an armed batch may wait for its commit before it is discarded and re-queued.</summary>
        private const int ApplyWindowMs = 3000;

        /// <summary>How long a committed batch's Temps may linger before recovery is requested.</summary>
        private const int DrainWindowMs = 3000;

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
                bool committedTempsRemain = CommittedRemoteTempsRemain();
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
                        Diagnostics.FlightRecorder.Note(
                            "remote transaction drain completed after clean-frame fence");
                    }
                }
                else
                {
                    _drainCleanFrames = 0;
                }

                if (committedTempsRemain &&
                    _drainFrames >= MinimumDrainFramesBeforeRecovery &&
                    System.Environment.TickCount - _drainArmTick > DrainWindowMs)
                {
                    TrackInvalidatedTemps(_committingRemoteNetTemps);
                    int quarantinedCount = _committingRemoteNetTemps.Count;
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
                    Mod.log.Warn("[MP] NetApply: isolated remote commit remained Temp after " +
                                 _drainFrames + " frames (tracked=" + quarantinedCount +
                                 "); quarantined without destructive cleanup and requesting " +
                                 "world recovery.");
                    Diagnostics.FlightRecorder.Note(
                        "net isolated commit quarantined frames=" + _drainFrames +
                        " tracked=" + quarantinedCount);
                    SyncInbox.RequestResync("remote transaction failed to drain");
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
            Mod.log.Info("[MP] NetSync realize cycle took " + elapsedMs.ToString("F0") + " ms (" +
                         _rzCycleCourses + " course(s), " + _rzCyclePool + " indexed net entities).");
            Diagnostics.FlightRecorder.Note("net realize cycle ms=" + elapsedMs.ToString("F0") +
                                              " courses=" + _rzCycleCourses +
                                              " pool=" + _rzCyclePool);
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
                Diagnostics.FlightRecorder.Note("tool preview isolated=" + _isolatedLocalTemps.Count);
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

        private void DisableQueryEntities(EntityQuery query, List<Entity> destination)
        {
            NativeArray<Entity> entities = query.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity entity = entities[i];
                    if (!EntityManager.Exists(entity) || EntityManager.HasComponent<Disabled>(entity)) continue;
                    EntityManager.AddComponent<Disabled>(entity);
                    destination.Add(entity);
                }
            }
            finally
            {
                entities.Dispose();
            }
        }

        private void ReleaseTrackedTemps(List<Entity> entities)
        {
            for (int i = 0; i < entities.Count; i++)
            {
                Entity entity = entities[i];
                if (EntityManager.Exists(entity) && EntityManager.HasComponent<Disabled>(entity))
                    EntityManager.RemoveComponent<Disabled>(entity);
            }
            entities.Clear();
        }

        private int ClearTrackedTemps(List<Entity> entities, bool clearPreview)
        {
            int cleared = 0;
            for (int i = 0; i < entities.Count; i++)
            {
                Entity entity = entities[i];
                if (!EntityManager.Exists(entity)) continue;
                if (clearPreview && ClearTempEntity(entity)) cleared++;
                if (EntityManager.Exists(entity) && EntityManager.HasComponent<Disabled>(entity))
                    EntityManager.RemoveComponent<Disabled>(entity);
            }
            return cleared;
        }

        private bool ClearTempEntity(Entity e)
        {
            if (!EntityManager.Exists(e) || EntityManager.HasComponent<Deleted>(e) ||
                !EntityManager.HasComponent<Temp>(e)) return false;

            Temp temp = EntityManager.GetComponentData<Temp>(e);
            bool handledSubObject = false;
            Entity owner = Entity.Null;
            if (EntityManager.HasComponent<Owner>(e))
            {
                owner = EntityManager.GetComponentData<Owner>(e).m_Owner;
                handledSubObject = EntityManager.HasComponent<Lane>(e) ||
                    (EntityManager.HasComponent<global::Game.Objects.Object>(e) &&
                     !EntityManager.HasComponent<global::Game.Vehicles.Vehicle>(e) &&
                     !EntityManager.HasComponent<global::Game.Creatures.Creature>(e) &&
                     !EntityManager.HasComponent<global::Game.Buildings.Building>(e) &&
                     !EntityManager.HasComponent<global::Game.Buildings.ServiceUpgrade>(e));
            }

            // Match the normal tool-clear ownership rule. Non-essential lane/object children of a
            // Temp owner are removed with that owner; independently tagging both sides can make
            // cleanup process the child after its ownership graph has already vanished.
            bool deleteEntity = !handledSubObject || (temp.m_Flags & TempFlags.Essential) != 0 ||
                                owner == Entity.Null || !EntityManager.Exists(owner) ||
                                !EntityManager.HasComponent<Temp>(owner);

            if (deleteEntity && temp.m_Original != Entity.Null && EntityManager.Exists(temp.m_Original)
                && EntityManager.HasComponent<Hidden>(temp.m_Original))
            {
                EntityManager.RemoveComponent<Hidden>(temp.m_Original);
                EntityManager.AddComponent<BatchesUpdated>(temp.m_Original);
            }
            if (EntityManager.HasBuffer<AggregateElement>(e))
            {
                DynamicBuffer<AggregateElement> buffer =
                    EntityManager.GetBuffer<AggregateElement>(e, isReadOnly: true);
                var elements = new NativeArray<Entity>(
                    buffer.AsNativeArray().Reinterpret<Entity>(), Allocator.Temp);
                try
                {
                    for (int j = 0; j < elements.Length; j++)
                    {
                        if (!EntityManager.Exists(elements[j])) continue;
                        EntityManager.AddComponent<BatchesUpdated>(elements[j]);
                        if (EntityManager.HasComponent<Highlighted>(elements[j]))
                            EntityManager.RemoveComponent<Highlighted>(elements[j]);
                    }
                }
                finally
                {
                    elements.Dispose();
                }
            }
            if (deleteEntity) EntityManager.AddComponent<Deleted>(e);
            return deleteEntity;
        }

        /// <summary>
        /// Mark every live Temp matched by <paramref name="query"/> as Deleted, the way the game's
        /// own clear pass does: restore an original the preview was hiding, drop the highlight on
        /// street-name aggregates, then tag the Temp. Returns how many were cleared.
        /// </summary>
        private int ClearTempEntities(EntityQuery query)
        {
            if (query.IsEmptyIgnoreFilter) return 0;

            int cleared = 0;
            NativeArray<Entity> tempEntities = query.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < tempEntities.Length; i++)
                {
                    if (ClearTempEntity(tempEntities[i])) cleared++;
                }
            }
            finally
            {
                tempEntities.Dispose();
            }
            return cleared;
        }

        private void ProtectRemoteBatchForLocalToolOutput()
        {
            _localToolOutputProtectedThisFrame = false;
            if (!_pendingApply) return;

            global::Game.Tools.ToolBaseSystem tool = _toolSystem != null ? _toolSystem.activeTool : null;
            if (tool == null || tool.applyMode != global::Game.Tools.ApplyMode.Apply) return;

            _protectedRemoteNetTemps.Clear();
            DisableQueryEntities(ActiveTransactionQuery(), _protectedRemoteNetTemps);
            // A local Apply owns its complete standing preview, regardless of the selected
            // tool. Releasing only the road-shaped portion can commit a building without its owned
            // driveway, or clear a subnet while leaving its owner behind.
            ReleaseTrackedTemps(_isolatedLocalTemps);
            _localToolOutputProtectedThisFrame = true;
            Diagnostics.FlightRecorder.Note("net remote batch protected for local " + tool.applyMode +
                " (remote=" + _protectedRemoteNetTemps.Count + ")");
        }

        private EntityQuery ActiveTransactionQuery()
        {
            if (IsObjectGraphTransaction(_pendingTransactionKind))
                return _objectTransactionTemps;
            if (IsRouteTransaction(_pendingTransactionKind))
                return _routeTransactionTemps;
            return _netOperationTemps;
        }

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

        private bool CommittedRemoteTempsRemain()
        {
            for (int i = 0; i < _committingRemoteNetTemps.Count; i++)
            {
                Entity entity = _committingRemoteNetTemps[i];
                // Deleted is only a request to the deferred cleanup pipeline. Treating that tag as
                // "gone" allowed the next native transaction to reuse a graph still being torn down.
                if (EntityManager.Exists(entity) && EntityManager.HasComponent<Temp>(entity))
                    return true;
            }
            return false;
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
                SyncInbox.RequestResync(repeatsPreviousAttempt
                    ? "remote transaction rejected deterministically"
                    : "remote transaction exhausted bounded replays");
            }

            _invalidatedBatchDraining = true;
            _invalidatedDrainArmTick = System.Environment.TickCount;
            _invalidatedCleanFrames = 0;
            _invalidatedDrainTimedOut = false;
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
                    Mod.log.Error("[MP] NetApply: quarantined native transaction did not leave Temp " +
                                  "state; blocking further native work and requesting world recovery.");
                    Diagnostics.FlightRecorder.Note(
                        "quarantined net temps failed to drain; native work remains blocked");
                    SyncInbox.RequestResync("quarantined native transaction failed to drain");
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

        /// <summary>
        /// Verify the complete route graph immediately before its isolated apply. Route application
        /// dereferences every root buffer entry and every non-null original, so a missing child or
        /// stale original rejects the whole graph instead of allowing a partially connected line.
        /// </summary>
        private bool ValidateArmedRouteTransaction(out string reason)
        {
            NativeArray<Entity> temps = _routeTransactionTemps.ToEntityArray(Allocator.Temp);
            try
            {
                if (temps.Length == 0)
                {
                    reason = "the generated route transaction was empty";
                    return false;
                }

                int routeCount = 0;
                int waypointCount = 0;
                int segmentCount = 0;
                for (int i = 0; i < temps.Length; i++)
                {
                    Entity entity = temps[i];
                    if (!EntityManager.Exists(entity) ||
                        EntityManager.HasComponent<Deleted>(entity))
                    {
                        reason = "the generated route transaction contains a deleted entity";
                        return false;
                    }

                    bool isRoute =
                        EntityManager.HasComponent<global::Game.Routes.Route>(entity);
                    bool isWaypoint =
                        EntityManager.HasComponent<global::Game.Routes.Waypoint>(entity);
                    bool isSegment =
                        EntityManager.HasComponent<global::Game.Routes.Segment>(entity);
                    if (!isRoute && !isWaypoint && !isSegment)
                    {
                        reason = "the route transaction contains an unknown Temp entity";
                        return false;
                    }

                    Temp temp = EntityManager.GetComponentData<Temp>(entity);
                    if (temp.m_Original != Entity.Null)
                    {
                        if (!EntityManager.Exists(temp.m_Original) ||
                            EntityManager.HasComponent<Deleted>(temp.m_Original) ||
                            EntityManager.HasComponent<Temp>(temp.m_Original))
                        {
                            reason = "a generated route entity has a stale original";
                            return false;
                        }
                        if ((isRoute &&
                             !EntityManager.HasComponent<global::Game.Routes.Route>(
                                 temp.m_Original)) ||
                            (isWaypoint &&
                             !EntityManager.HasComponent<global::Game.Routes.Waypoint>(
                                 temp.m_Original)) ||
                            (isSegment &&
                             !EntityManager.HasComponent<global::Game.Routes.Segment>(
                                 temp.m_Original)))
                        {
                            reason = "a generated route entity has a mismatched original";
                            return false;
                        }
                    }

                    if (isWaypoint)
                    {
                        waypointCount++;
                        if (EntityManager.HasComponent<global::Game.Routes.Connected>(entity))
                        {
                            Entity connected =
                                EntityManager
                                    .GetComponentData<global::Game.Routes.Connected>(entity)
                                    .m_Connected;
                            if (connected != Entity.Null &&
                                (!EntityManager.Exists(connected) ||
                                 EntityManager.HasComponent<Deleted>(connected)))
                            {
                                reason = "a generated route waypoint has a stale connection";
                                return false;
                            }
                        }
                    }
                    if (isSegment) segmentCount++;
                    if (!isRoute) continue;

                    routeCount++;
                    if (!EntityManager.HasBuffer<global::Game.Routes.RouteWaypoint>(entity) ||
                        !EntityManager.HasBuffer<global::Game.Routes.RouteSegment>(entity))
                    {
                        reason = "the generated route root is missing its graph buffers";
                        return false;
                    }
                    if (temp.m_Original != Entity.Null &&
                        (!EntityManager
                             .HasBuffer<global::Game.Routes.RouteWaypoint>(temp.m_Original) ||
                         !EntityManager
                             .HasBuffer<global::Game.Routes.RouteSegment>(temp.m_Original)))
                    {
                        reason = "the existing route root is missing its graph buffers";
                        return false;
                    }

                    DynamicBuffer<global::Game.Routes.RouteWaypoint> waypoints =
                        EntityManager.GetBuffer<global::Game.Routes.RouteWaypoint>(
                            entity, isReadOnly: true);
                    DynamicBuffer<global::Game.Routes.RouteSegment> segments =
                        EntityManager.GetBuffer<global::Game.Routes.RouteSegment>(
                            entity, isReadOnly: true);
                    if (waypoints.Length < 2 || segments.Length != waypoints.Length)
                    {
                        reason = "the generated route graph is incomplete";
                        return false;
                    }
                    for (int j = 0; j < waypoints.Length; j++)
                    {
                        Entity child = waypoints[j].m_Waypoint;
                        if (child == Entity.Null || !EntityManager.Exists(child) ||
                            !EntityManager.HasComponent<Temp>(child) ||
                            !EntityManager
                                .HasComponent<global::Game.Routes.Waypoint>(child) ||
                            EntityManager.HasComponent<Deleted>(child))
                        {
                            reason = "the generated route has a missing waypoint";
                            return false;
                        }
                    }
                    for (int j = 0; j < segments.Length; j++)
                    {
                        Entity child = segments[j].m_Segment;
                        if (child == Entity.Null || !EntityManager.Exists(child) ||
                            !EntityManager.HasComponent<Temp>(child) ||
                            !EntityManager
                                .HasComponent<global::Game.Routes.Segment>(child) ||
                            EntityManager.HasComponent<Deleted>(child))
                        {
                            reason = "the generated route has a missing segment";
                            return false;
                        }
                    }
                }

                if (routeCount != 1)
                {
                    reason = "the route transaction contains " + routeCount +
                             " roots instead of one";
                    return false;
                }
                if (waypointCount < 2 || segmentCount < 2)
                {
                    reason = "the route transaction is missing owned graph entities";
                    return false;
                }

                reason = null;
                return true;
            }
            finally
            {
                temps.Dispose();
            }
        }

        /// <summary>
        /// Validate the exact union consumed by the object, net, and area apply passes. The checks
        /// intentionally run on the main thread immediately before scheduling those jobs because
        /// their observed runtime behaviour assumes owner/original/buffer references are live.
        /// </summary>
        private bool ValidateArmedObjectTransaction(out string reason)
        {
            _relinkedOwners = 0;
            NativeArray<Entity> temps = _objectTransactionTemps.ToEntityArray(Allocator.Temp);
            try
            {
                if (temps.Length == 0)
                {
                    reason = "the generated object transaction was empty";
                    return false;
                }

                var members = new HashSet<Entity>();
                for (int i = 0; i < temps.Length; i++)
                {
                    Entity entity = temps[i];
                    if (!EntityManager.Exists(entity) || !EntityManager.HasComponent<Temp>(entity) ||
                        EntityManager.HasComponent<Deleted>(entity) ||
                        EntityManager.HasComponent<Disabled>(entity))
                    {
                        reason = "the generated object transaction became partial before commit";
                        return false;
                    }
                    members.Add(entity);
                }
                HashSet<Entity> enabledTransactionConnections =
                    CollectEnabledTransactionConnections(members);

                int objectRoots = 0;
                int netStructures = 0;
                for (int i = 0; i < temps.Length; i++)
                {
                    Entity entity = temps[i];
                    Temp temp = EntityManager.GetComponentData<Temp>(entity);
                    bool isObject = EntityManager.HasComponent<global::Game.Objects.Object>(entity);
                    bool isNode = EntityManager.HasComponent<Node>(entity);
                    bool isEdge = EntityManager.HasComponent<Edge>(entity);
                    bool isLane = EntityManager.HasComponent<Lane>(entity);
                    bool isAggregate = EntityManager.HasComponent<Aggregate>(entity);
                    bool isArea = EntityManager.HasComponent<global::Game.Areas.Area>(entity);
                    if (isNode || isEdge) netStructures++;
                    if (!isObject && !isNode && !isEdge && !isLane && !isAggregate && !isArea)
                    {
                        reason = "the generated object transaction contains an unsupported Temp shape";
                        return false;
                    }

                    if (!ValidateTransactionOwner(entity, members, out reason)) return false;
                    if (!ValidateOwnedBuffers(entity, members, out reason)) return false;

                    if (isObject)
                    {
                        if (!EntityManager.HasComponent<Owner>(entity) ||
                            !members.Contains(EntityManager.GetComponentData<Owner>(entity).m_Owner))
                            objectRoots++;
                        if ((temp.m_Flags & TempFlags.Delete) == 0 &&
                            !ValidateObjectPrefabReference(entity, out reason)) return false;
                        if (!ValidateObjectOriginal(temp, out reason)) return false;
                        if (!ValidateAttachment(entity, members, out reason)) return false;
                    }

                    if (isNode || isEdge)
                    {
                        if (!ValidateTransactionOriginal(entity, temp, isNode, isEdge,
                                enabledTransactionConnections, out reason))
                            return false;
                        if (isNode && !ValidateTempNode(entity, temp, out reason)) return false;
                        if (isEdge && !ValidateTempEdge(entity, temp, members,
                                enabledTransactionConnections, out reason)) return false;
                    }

                    if (isLane && !ValidateLaneOriginal(temp, out reason)) return false;
                    if (isArea && !ValidateAreaEntity(entity, temp, out reason)) return false;

                    bool missingReplacementOriginal =
                        isEdge && (temp.m_Flags & (TempFlags.Replace | TempFlags.Combine)) != 0 ||
                        isLane && (temp.m_Flags & TempFlags.Replace) != 0;
                    if (missingReplacementOriginal && temp.m_Original == Entity.Null)
                    {
                        reason = "a generated object-graph replacement has no original entity";
                        return false;
                    }
                }

                if (_pendingTransactionKind == RemoteToolTransactionKind.AssetStampGraph)
                {
                    if (netStructures == 0)
                    {
                        reason = "the generated asset-stamp transaction has no network graph";
                        return false;
                    }
                }
                else if (objectRoots == 0)
                {
                    reason = "the generated object transaction has no top-level object";
                    return false;
                }

                reason = null;
                Diagnostics.FlightRecorder.Note("object transaction validated temps=" + temps.Length +
                    (_relinkedOwners > 0 ? " ownersRelinked=" + _relinkedOwners : string.Empty));
                return true;
            }
            finally
            {
                temps.Dispose();
            }
        }

        private bool ValidateObjectPrefabReference(Entity entity, out string reason)
        {
            reason = null;
            if (!EntityManager.HasComponent<global::Game.Prefabs.PrefabRef>(entity))
            {
                reason = "a generated object has no prefab reference";
                return false;
            }
            Entity prefab = EntityManager.GetComponentData<global::Game.Prefabs.PrefabRef>(entity).m_Prefab;
            if (prefab == Entity.Null || !EntityManager.Exists(prefab) ||
                !EntityManager.HasComponent<global::Game.Prefabs.PrefabData>(prefab) ||
                !EntityManager.HasComponent<global::Game.Prefabs.ObjectData>(prefab))
            {
                reason = "a generated object references an invalid object prefab";
                return false;
            }
            return true;
        }

        private bool ValidateObjectOriginal(Temp temp, out string reason)
        {
            reason = null;
            if (temp.m_Original == Entity.Null) return true;
            Entity original = temp.m_Original;
            if (!EntityManager.Exists(original) || EntityManager.HasComponent<Deleted>(original) ||
                EntityManager.HasComponent<Temp>(original) ||
                !EntityManager.HasComponent<global::Game.Objects.Object>(original))
            {
                reason = "an object definition references a stale or non-object original";
                return false;
            }
            return ValidateOwnedBuffers(original, null, out reason);
        }

        /// <summary>
        /// A lane Temp naming an original reaches the apply pass's lane update, which adds the
        /// apply-updated component set to that original with no existence test - and its delete and
        /// replace branches only null-check it. A destroyed original therefore becomes a command
        /// buffer entry that faults when the tool barrier plays it back, with the process ending
        /// inside the playback rather than at the system that recorded it.
        ///
        /// Nodes, edges, objects and areas were already checked here. Lanes were not, and they are
        /// the bulk of every large batch - 573 of 732 members in one observed fatal commit.
        /// </summary>
        private bool ValidateLaneOriginal(Temp temp, out string reason)
        {
            reason = null;
            Entity original = temp.m_Original;
            if (original == Entity.Null) return true;
            if (!EntityManager.Exists(original) || EntityManager.HasComponent<Deleted>(original) ||
                EntityManager.HasComponent<Temp>(original) ||
                !EntityManager.HasComponent<Lane>(original))
            {
                reason = "a generated lane references a stale original";
                return false;
            }
            return true;
        }

        private bool ValidateAttachment(Entity entity, HashSet<Entity> members, out string reason)
        {
            reason = null;
            if (!EntityManager.HasComponent<global::Game.Objects.Attached>(entity)) return true;
            global::Game.Objects.Attached attached =
                EntityManager.GetComponentData<global::Game.Objects.Attached>(entity);
            return ValidateLiveOrMemberReference(attached.m_Parent, members, "attachment parent", out reason) &&
                   ValidateLiveOrMemberReference(attached.m_OldParent, members, "old attachment parent", out reason);
        }

        private bool ValidateAreaEntity(Entity entity, Temp temp, out string reason)
        {
            reason = null;
            if ((temp.m_Flags & TempFlags.Delete) == 0)
            {
                if (!EntityManager.HasComponent<global::Game.Prefabs.PrefabRef>(entity))
                {
                    reason = "a generated area has no prefab reference";
                    return false;
                }
                Entity prefab = EntityManager.GetComponentData<global::Game.Prefabs.PrefabRef>(entity).m_Prefab;
                if (prefab == Entity.Null || !EntityManager.Exists(prefab) ||
                    !EntityManager.HasComponent<global::Game.Prefabs.AreaData>(prefab) ||
                    !EntityManager.HasBuffer<global::Game.Areas.Node>(entity))
                {
                    reason = "a generated area is missing prefab or node data";
                    return false;
                }
            }
            if (temp.m_Original != Entity.Null &&
                (!EntityManager.Exists(temp.m_Original) ||
                 EntityManager.HasComponent<Deleted>(temp.m_Original) ||
                 !EntityManager.HasComponent<global::Game.Areas.Area>(temp.m_Original) ||
                 !EntityManager.HasBuffer<global::Game.Areas.Node>(temp.m_Original)))
            {
                reason = "an area definition references a stale original";
                return false;
            }
            return true;
        }

        private bool ValidateOwnedBuffers(Entity entity, HashSet<Entity> members, out string reason)
        {
            reason = null;
            if (EntityManager.HasBuffer<global::Game.Objects.SubObject>(entity))
            {
                DynamicBuffer<global::Game.Objects.SubObject> buffer =
                    EntityManager.GetBuffer<global::Game.Objects.SubObject>(entity, isReadOnly: true);
                for (int i = 0; i < buffer.Length; i++)
                    if (!ValidateLiveOrMemberReference(buffer[i].m_SubObject, members,
                            "SubObject", out reason)) return false;
            }
            if (EntityManager.HasBuffer<global::Game.Net.SubNet>(entity))
            {
                DynamicBuffer<global::Game.Net.SubNet> buffer =
                    EntityManager.GetBuffer<global::Game.Net.SubNet>(entity, isReadOnly: true);
                for (int i = 0; i < buffer.Length; i++)
                    if (!ValidateLiveOrMemberReference(buffer[i].m_SubNet, members,
                            "SubNet", out reason)) return false;
            }
            if (EntityManager.HasBuffer<global::Game.Areas.SubArea>(entity))
            {
                DynamicBuffer<global::Game.Areas.SubArea> buffer =
                    EntityManager.GetBuffer<global::Game.Areas.SubArea>(entity, isReadOnly: true);
                for (int i = 0; i < buffer.Length; i++)
                    if (!ValidateLiveOrMemberReference(buffer[i].m_Area, members,
                            "SubArea", out reason)) return false;
            }
            return true;
        }

        private bool ValidateLiveOrMemberReference(Entity referenced, HashSet<Entity> members,
            string label, out string reason)
        {
            reason = null;
            if (referenced == Entity.Null) return true;
            if (!EntityManager.Exists(referenced) || EntityManager.HasComponent<Deleted>(referenced))
            {
                reason = label + " contains a stale entity reference";
                return false;
            }
            if (EntityManager.HasComponent<Temp>(referenced) &&
                (members == null || !members.Contains(referenced) ||
                 EntityManager.HasComponent<Disabled>(referenced)))
            {
                reason = label + " points outside the enabled transaction";
                return false;
            }
            return true;
        }

        /// <summary>
        /// Verify the complete generated net transaction immediately before scheduling its apply.
        /// Split targets and reuse nodes were resolved a frame earlier; a concurrent local edit may
        /// have invalidated an original, endpoint, owner, or connectivity buffer in the meantime.
        /// Partial work is discarded and rebuilt rather than passed to an unchecked apply path.
        /// </summary>
        private bool ValidateArmedNetTransaction(out string reason)
        {
            _relinkedOwners = 0;
            NativeArray<Entity> temps = _netOperationTemps.ToEntityArray(Allocator.Temp);
            try
            {
                if (temps.Length == 0)
                {
                    reason = "the generated net transaction was empty";
                    return false;
                }

                var members = new HashSet<Entity>();
                for (int i = 0; i < temps.Length; i++)
                {
                    Entity entity = temps[i];
                    if (!EntityManager.Exists(entity) || !EntityManager.HasComponent<Temp>(entity) ||
                        EntityManager.HasComponent<Deleted>(entity) ||
                        EntityManager.HasComponent<Disabled>(entity))
                    {
                        reason = "the generated net transaction became partial before commit";
                        return false;
                    }
                    members.Add(entity);
                }
                HashSet<Entity> enabledTransactionConnections =
                    CollectEnabledTransactionConnections(members);

                int structuralEntities = 0;
                int attachedObjectRoots = 0;
                int areaEntities = 0;
                for (int i = 0; i < temps.Length; i++)
                {
                    Entity entity = temps[i];
                    Temp temp = EntityManager.GetComponentData<Temp>(entity);
                    bool isObject = EntityManager.HasComponent<global::Game.Objects.Object>(entity);
                    bool isNode = EntityManager.HasComponent<Node>(entity);
                    bool isEdge = EntityManager.HasComponent<Edge>(entity);
                    bool isLane = EntityManager.HasComponent<Lane>(entity);
                    bool isAggregate = EntityManager.HasComponent<Aggregate>(entity);
                    bool isArea = EntityManager.HasComponent<global::Game.Areas.Area>(entity);
                    if (!isObject && !isNode && !isEdge && !isLane && !isAggregate && !isArea)
                    {
                        reason = "the generated net transaction contains an unknown entity shape";
                        return false;
                    }
                    if (isNode || isEdge) structuralEntities++;
                    if (isArea) areaEntities++;

                    if (!ValidateTransactionOwner(entity, members, out reason)) return false;
                    if (!ValidateOwnedBuffers(entity, members, out reason)) return false;

                    if (isObject)
                    {
                        if ((temp.m_Flags & TempFlags.Delete) == 0 &&
                            !ValidateObjectPrefabReference(entity, out reason)) return false;
                        if (!ValidateObjectOriginal(temp, out reason)) return false;
                        if (!ValidateAttachment(entity, members, out reason)) return false;
                        if (!EntityManager.HasComponent<Owner>(entity))
                        {
                            if (!ValidateNetAttachedObjectRoot(entity, temp, members, out reason))
                                return false;
                            attachedObjectRoots++;
                        }
                    }

                    if (isNode || isEdge)
                    {
                        if (!ValidateTransactionOriginal(entity, temp, isNode, isEdge,
                                enabledTransactionConnections, out reason)) return false;
                    }
                    if (isNode && !ValidateTempNode(entity, temp, out reason)) return false;
                    if (isEdge && !ValidateTempEdge(entity, temp, members,
                            enabledTransactionConnections, out reason)) return false;
                    if (isLane && !ValidateLaneOriginal(temp, out reason)) return false;
                    if (isArea && !ValidateAreaEntity(entity, temp, out reason)) return false;

                    bool missingReplacementOriginal =
                        isEdge && (temp.m_Flags & (TempFlags.Replace | TempFlags.Combine)) != 0 ||
                        isLane && (temp.m_Flags & TempFlags.Replace) != 0;
                    if (missingReplacementOriginal && temp.m_Original == Entity.Null)
                    {
                        reason = "a generated replacement has no original entity";
                        return false;
                    }
                }

                if (structuralEntities == 0)
                {
                    reason = "the generated net transaction has no node/edge root";
                    return false;
                }

                reason = null;
                if (attachedObjectRoots > 0 || areaEntities > 0 || _relinkedOwners > 0)
                    Diagnostics.FlightRecorder.Note("net side-effect graph validated temps=" +
                        temps.Length + " attachedRoots=" + attachedObjectRoots +
                        " areas=" + areaEntities +
                        (_relinkedOwners > 0 ? " ownersRelinked=" + _relinkedOwners : string.Empty));
                return true;
            }
            finally
            {
                temps.Dispose();
            }
        }

        /// <summary>
        /// An owner-less object in a net transaction must be the native update copy of an existing
        /// object attached to a touched node/edge. This excludes an unrelated placement preview from
        /// the net apply pass while retaining the exact path that recentres roundabout islands.
        /// </summary>
        private bool ValidateNetAttachedObjectRoot(Entity entity, Temp temp,
            HashSet<Entity> members, out string reason)
        {
            reason = null;
            const TempFlags incompatible = TempFlags.Create | TempFlags.Dragging |
                TempFlags.Select | TempFlags.Modify | TempFlags.Replace | TempFlags.Upgrade |
                TempFlags.Combine | TempFlags.Cancel | TempFlags.Duplicate;
            if (temp.m_Original == Entity.Null ||
                (temp.m_Flags & TempFlags.Essential) == 0 ||
                (temp.m_Flags & incompatible) != 0)
            {
                reason = "the net transaction contains an unrelated top-level object Temp";
                return false;
            }

            Entity original = temp.m_Original;
            if (!EntityManager.HasComponent<global::Game.Objects.Attached>(entity) ||
                !EntityManager.HasComponent<global::Game.Objects.Attached>(original) ||
                !EntityManager.HasComponent<global::Game.Prefabs.PrefabRef>(entity) ||
                !EntityManager.HasComponent<global::Game.Prefabs.PrefabRef>(original))
            {
                reason = "a generated net-side object is not an attached-object update";
                return false;
            }

            global::Game.Prefabs.PrefabRef prefab =
                EntityManager.GetComponentData<global::Game.Prefabs.PrefabRef>(entity);
            global::Game.Prefabs.PrefabRef originalPrefab =
                EntityManager.GetComponentData<global::Game.Prefabs.PrefabRef>(original);
            if (prefab.m_Prefab != originalPrefab.m_Prefab)
            {
                reason = "a generated net-side object changed prefab unexpectedly";
                return false;
            }

            global::Game.Objects.Attached attached =
                EntityManager.GetComponentData<global::Game.Objects.Attached>(entity);
            global::Game.Objects.Attached originalAttached =
                EntityManager.GetComponentData<global::Game.Objects.Attached>(original);
            bool deletesWithoutParent = (temp.m_Flags & TempFlags.Delete) != 0 &&
                                         attached.m_Parent == Entity.Null;
            if ((!deletesWithoutParent &&
                 !ValidateNetAttachmentParent(attached.m_Parent, members,
                     "generated attachment parent", out reason)) ||
                !ValidateNetAttachmentParent(originalAttached.m_Parent, members,
                    "original attachment parent", out reason)) return false;

            return true;
        }

        private bool ValidateNetAttachmentParent(Entity parent, HashSet<Entity> members,
            string label, out string reason)
        {
            if (parent == Entity.Null)
            {
                reason = label + " is null";
                return false;
            }
            if (!ValidateLiveOrMemberReference(parent, members, label, out reason)) return false;
            if (!EntityManager.HasComponent<Node>(parent) && !EntityManager.HasComponent<Edge>(parent))
            {
                reason = label + " is not a network node or edge";
                return false;
            }
            return true;
        }

        private bool ValidateTransactionOwner(Entity entity, HashSet<Entity> members, out string reason)
        {
            reason = null;
            if (!EntityManager.HasComponent<Owner>(entity)) return true;

            Entity owner = EntityManager.GetComponentData<Owner>(entity).m_Owner;
            // An unset owner is a normal intermediate state, not corruption. Native generation
            // leaves it unset on a sub-element whose owner is described by prefab + transform, and
            // the resolution pass a phase later fills it in by an exact transform match. That match
            // is one-shot - the description is consumed whether or not it hit - so a single miss is
            // permanent. Re-link from the description this batch still holds rather than discarding
            // a graph whose ownership the batch itself can state.
            Entity relinked;
            if (owner == Entity.Null && TryRelinkGeneratedOwner(entity, members, out relinked))
            {
                // Owner is already present, so this writes a value without changing the archetype:
                // the enclosing member array and set stay valid.
                EntityManager.SetComponentData(entity, new Owner { m_Owner = relinked });
                // One line per orphan would be hundreds on a large placement; the pass reports a
                // total, and the first member is enough to identify which graph needed repair.
                if (_relinkedOwners++ == 0)
                    Diagnostics.FlightRecorder.Note("transaction owner re-linked " +
                        DescribeTransactionEntity(entity) + " owner=#" + relinked.Index);
                owner = relinked;
            }
            if (owner == Entity.Null || !EntityManager.Exists(owner) ||
                EntityManager.HasComponent<Deleted>(owner))
            {
                reason = "a generated net entity has a missing owner " +
                         DescribeOwnerFailure(entity, owner, members);
                return false;
            }
            if (EntityManager.HasComponent<Temp>(owner) &&
                (!members.Contains(owner) || EntityManager.HasComponent<Disabled>(owner)))
            {
                // Generated child entities may still point at an isolated preview copy of an
                // existing owner. The apply passes patch that reference to Temp.m_Original before
                // consuming the child. Accept exactly that resolvable form; a new/replacement Temp
                // owner outside this transaction would leave the child attached to discarded work.
                Temp ownerTemp = EntityManager.GetComponentData<Temp>(owner);
                Entity original = ownerTemp.m_Original;
                bool resolvesToLiveOriginal = original != Entity.Null &&
                    (ownerTemp.m_Flags & (TempFlags.Replace | TempFlags.Combine)) == 0 &&
                    EntityManager.Exists(original) &&
                    !EntityManager.HasComponent<Deleted>(original) &&
                    !EntityManager.HasComponent<Temp>(original);
                if (!resolvesToLiveOriginal)
                {
                    reason = "a generated net entity is separated from an unresolved Temp owner";
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Replace the per-frame record of which owner each described sub-element named. Written by
        /// <see cref="OwnerDefinitionSnapshotSystem"/> in the phase before the game consumes those
        /// descriptions; an empty pass leaves the previous record intact, because the batch being
        /// validated has already had its descriptions taken.
        /// </summary>
        public void BeginOwnerDescriptionSnapshot(int expected)
        {
            if (expected <= 0) return;
            _describedOwners.Clear();
        }

        public void RecordOwnerDescription(Entity entity, Entity ownerPrefab,
            Unity.Mathematics.float3 ownerPosition)
        {
            _describedOwners[entity] = new ArmedOwnerDefinition
            {
                Prefab = ownerPrefab,
                Position = ownerPosition,
            };
        }

        /// <summary>
        /// Recover the owner of a sub-element the native resolution pass left unset, in descending
        /// order of certainty: the entity's own surviving description, the description recorded for
        /// exactly this entity before the pass consumed it, and finally the batch's own description
        /// when it names a single owner. Ambiguity is never guessed away.
        /// </summary>
        private bool TryRelinkGeneratedOwner(Entity entity, HashSet<Entity> members, out Entity owner)
        {
            owner = Entity.Null;
            ArmedOwnerDefinition described;
            if (members == null || !TryResolveOwnerDescription(entity, out described)) return false;
            return TryFindDescribedOwner(entity, described.Prefab, described.Position, members,
                out owner);
        }

        private bool TryResolveOwnerDescription(Entity entity, out ArmedOwnerDefinition described)
        {
            if (EntityManager.HasComponent<OwnerDefinition>(entity))
            {
                OwnerDefinition live = EntityManager.GetComponentData<OwnerDefinition>(entity);
                described = new ArmedOwnerDefinition
                {
                    Prefab = live.m_Prefab,
                    Position = live.m_Position,
                };
                return described.Prefab != Entity.Null;
            }
            if (_describedOwners.TryGetValue(entity, out described)) return true;
            // Two different owners in one batch cannot be told apart with no record of this entity.
            // Re-parenting to the wrong building is worse than rejecting the batch.
            if (_pendingOwnerDefinitions.Count == 1)
            {
                described = _pendingOwnerDefinitions[0];
                return true;
            }
            described = default(ArmedOwnerDefinition);
            return false;
        }

        /// <summary>
        /// A candidate owner must be something the apply passes can already resolve. An entity whose
        /// own owner is still unset is another orphan: parenting one to the other would build a
        /// chain that no pass can follow, and an entity may never own itself.
        /// </summary>
        private bool IsResolvedOwnerCandidate(Entity candidate, Entity child)
        {
            if (candidate == child) return false;
            if (!EntityManager.HasComponent<Owner>(candidate)) return true;
            return EntityManager.GetComponentData<Owner>(candidate).m_Owner != Entity.Null;
        }

        /// <summary>
        /// Match an owner description against the armed transaction. The native pass compares the
        /// live transform bit-exactly, which a ground-conforming or attachment pass between
        /// generation and resolution can defeat; compare on the horizontal plane instead, where a
        /// placement does not move, and only accept a single candidate.
        /// </summary>
        private bool TryFindDescribedOwner(Entity child, Entity prefab,
            Unity.Mathematics.float3 position, HashSet<Entity> members, out Entity owner)
        {
            owner = Entity.Null;
            if (prefab == Entity.Null) return false;

            // Every sub-element of one placement names the same owner, so a single-entry memo turns
            // a per-orphan scan of the whole transaction into one scan for the batch.
            if (prefab == _lastDescribedOwnerPrefab && position.Equals(_lastDescribedOwnerPosition) &&
                _lastDescribedOwner != Entity.Null && _lastDescribedOwner != child &&
                members.Contains(_lastDescribedOwner))
            {
                owner = _lastDescribedOwner;
                return true;
            }

            const float maxHorizontalDistanceSq = 1f;
            float bestDistanceSq = float.MaxValue;
            int candidates = 0;
            foreach (Entity candidate in members)
            {
                if (!EntityManager.Exists(candidate) ||
                    !IsResolvedOwnerCandidate(candidate, child) ||
                    EntityManager.HasComponent<Deleted>(candidate) ||
                    !EntityManager.HasComponent<global::Game.Objects.Object>(candidate) ||
                    !EntityManager.HasComponent<global::Game.Objects.Transform>(candidate) ||
                    !EntityManager.HasComponent<global::Game.Prefabs.PrefabRef>(candidate)) continue;
                if (EntityManager.GetComponentData<global::Game.Prefabs.PrefabRef>(candidate)
                        .m_Prefab != prefab) continue;

                Unity.Mathematics.float3 candidatePosition =
                    EntityManager.GetComponentData<global::Game.Objects.Transform>(candidate)
                        .m_Position;
                float distanceSq = Unity.Mathematics.math.distancesq(
                    candidatePosition.xz, position.xz);
                if (distanceSq > maxHorizontalDistanceSq) continue;
                candidates++;
                if (distanceSq >= bestDistanceSq) continue;
                bestDistanceSq = distanceSq;
                owner = candidate;
            }
            // A connector re-cut beside a building that already stands names a live owner. Owner
            // resolution only matches a Temp to a Temp, so it can never bind that pair and the
            // transaction alone cannot supply it either. Ask what is standing at the described
            // point instead; attaching to a live owner is the ordinary form the apply passes read.
            if (candidates == 0) return TryFindLiveDescribedOwner(prefab, position, out owner);
            if (candidates != 1)
            {
                owner = Entity.Null;
                return false;
            }
            _lastDescribedOwnerPrefab = prefab;
            _lastDescribedOwnerPosition = position;
            _lastDescribedOwner = owner;
            return true;
        }

        /// <summary>
        /// How many live objects of the described prefab stand where the description says. Zero
        /// means the description names something this machine does not have; more than one means
        /// the point is ambiguous and re-linking deliberately refused.
        /// </summary>
        private int LiveOwnerCandidates(Entity prefab, Unity.Mathematics.float3 position)
        {
            Entity ignored;
            if (TryFindLiveDescribedOwner(prefab, position, out ignored)) return 1;
            return _lastLiveOwnerCandidates;
        }

        private int _lastLiveOwnerCandidates;

        private bool TryFindLiveDescribedOwner(Entity prefab, Unity.Mathematics.float3 position,
            out Entity owner)
        {
            owner = Entity.Null;
            _lastLiveOwnerCandidates = 0;
            if (_ownerSearch == null) return false;

            const float searchRadius = 2f;
            const float maxHorizontalDistanceSq = 1f;
            var candidates = new NativeList<Entity>(Allocator.Temp);
            try
            {
                _ownerSearch.CollectNear(position, searchRadius, candidates);
                float bestDistanceSq = float.MaxValue;
                int matches = 0;
                for (int i = 0; i < candidates.Length; i++)
                {
                    Entity candidate = candidates[i];
                    if (!EntityManager.Exists(candidate) ||
                        EntityManager.HasComponent<Deleted>(candidate) ||
                        EntityManager.HasComponent<Temp>(candidate) ||
                        !EntityManager.HasComponent<global::Game.Objects.Transform>(candidate) ||
                        !EntityManager.HasComponent<global::Game.Prefabs.PrefabRef>(candidate) ||
                        EntityManager.GetComponentData<global::Game.Prefabs.PrefabRef>(candidate)
                            .m_Prefab != prefab) continue;

                    float distanceSq = Unity.Mathematics.math.distancesq(
                        EntityManager.GetComponentData<global::Game.Objects.Transform>(candidate)
                            .m_Position.xz, position.xz);
                    if (distanceSq > maxHorizontalDistanceSq) continue;
                    matches++;
                    if (distanceSq >= bestDistanceSq) continue;
                    bestDistanceSq = distanceSq;
                    owner = candidate;
                }
                _lastLiveOwnerCandidates = matches;
                if (matches == 1) return true;
                owner = Entity.Null;
                return false;
            }
            finally
            {
                candidates.Dispose();
            }
        }

        /// <summary>
        /// Name the entity a validation rule rejected. The reason string alone cannot distinguish an
        /// owner that never resolved from one deleted mid-transaction, which left several recorded
        /// sessions undiagnosable.
        /// </summary>
        private string DescribeOwnerFailure(Entity entity, Entity owner, HashSet<Entity> members)
        {
            var detail = new System.Text.StringBuilder("(");
            detail.Append(DescribeTransactionEntity(entity));
            detail.Append(EntityManager.HasComponent<OwnerDefinition>(entity)
                ? " ownerDefinition=present"
                : " ownerDefinition=consumed");
            if (owner == Entity.Null) detail.Append(" owner=unset");
            else if (!EntityManager.Exists(owner))
                detail.Append(" owner=#").Append(owner.Index).Append("=gone");
            else detail.Append(" owner=#").Append(owner.Index).Append("=deleted");

            ArmedOwnerDefinition described;
            if (!TryResolveOwnerDescription(entity, out described))
            {
                detail.Append(" wantedOwner=unknown armedOwners=")
                      .Append(_pendingOwnerDefinitions.Count);
            }
            else
            {
                detail.Append(" wantedOwner=")
                      .Append(PrefabIndex.SafeName(_prefabSystem, described.Prefab));
                // Distinguish the two ways the search can come up empty: no such owner is in the
                // transaction at all, or one is but sits outside the accepted distance. Only the
                // second is a tolerance question.
                int samePrefab = 0;
                float nearestSq = float.MaxValue;
                if (members != null)
                {
                    foreach (Entity candidate in members)
                    {
                        if (!EntityManager.Exists(candidate) ||
                            !EntityManager.HasComponent<global::Game.Objects.Transform>(candidate) ||
                            !EntityManager.HasComponent<global::Game.Prefabs.PrefabRef>(candidate) ||
                            EntityManager.GetComponentData<global::Game.Prefabs.PrefabRef>(candidate)
                                .m_Prefab != described.Prefab) continue;
                        samePrefab++;
                        float distanceSq = Unity.Mathematics.math.distancesq(
                            EntityManager.GetComponentData<global::Game.Objects.Transform>(candidate)
                                .m_Position.xz, described.Position.xz);
                        if (distanceSq < nearestSq) nearestSq = distanceSq;
                    }
                }
                detail.Append(" memberCandidates=").Append(samePrefab);
                if (samePrefab > 0)
                    detail.Append(" nearestM=")
                          .Append(Unity.Mathematics.math.sqrt(nearestSq).ToString("0.##"));
                else
                    detail.Append(" liveCandidates=")
                          .Append(LiveOwnerCandidates(described.Prefab, described.Position));
            }
            // Owner resolution ignores Disabled entities, and the isolation this commit path applies
            // uses exactly that tag. Say so when an isolated candidate exists: it separates our own
            // interference from a description the batch genuinely cannot satisfy.
            int isolated = IsolatedOwnerCandidates(entity);
            if (isolated > 0) detail.Append(" isolatedCandidates=").Append(isolated);
            detail.Append(')');
            return detail.ToString();
        }

        private string DescribeTransactionEntity(Entity entity)
        {
            var detail = new System.Text.StringBuilder();
            if (EntityManager.HasComponent<Edge>(entity)) detail.Append("edge");
            else if (EntityManager.HasComponent<Node>(entity)) detail.Append("node");
            else if (EntityManager.HasComponent<Lane>(entity)) detail.Append("lane");
            else if (EntityManager.HasComponent<Aggregate>(entity)) detail.Append("aggr");
            else if (EntityManager.HasComponent<global::Game.Objects.Object>(entity)) detail.Append("obj");
            else if (EntityManager.HasComponent<global::Game.Areas.Area>(entity)) detail.Append("area");
            else detail.Append("other");
            detail.Append('#').Append(entity.Index);

            if (EntityManager.HasComponent<global::Game.Prefabs.PrefabRef>(entity))
            {
                Entity prefab =
                    EntityManager.GetComponentData<global::Game.Prefabs.PrefabRef>(entity).m_Prefab;
                detail.Append(" prefab=").Append(PrefabIndex.SafeName(_prefabSystem, prefab));
            }
            if (EntityManager.HasComponent<Temp>(entity))
                detail.Append(" flags=").Append(EntityManager.GetComponentData<Temp>(entity)
                    .m_Flags.ToString().Replace(", ", "|"));
            return detail.ToString();
        }

        /// <summary>
        /// Owners this commit path is currently hiding that could have satisfied the rejected
        /// entity's description. Owner resolution skips Disabled entities, so a non-zero count means
        /// our own isolation, not the world, is what the description could not reach.
        /// </summary>
        private int IsolatedOwnerCandidates(Entity entity)
        {
            ArmedOwnerDefinition described;
            if (!TryResolveOwnerDescription(entity, out described)) return 0;
            Entity prefab = described.Prefab;
            if (prefab == Entity.Null) return 0;

            int isolated = 0;
            for (int i = 0; i < _isolatedLocalTemps.Count; i++)
            {
                Entity candidate = _isolatedLocalTemps[i];
                if (!EntityManager.Exists(candidate) ||
                    !EntityManager.HasComponent<global::Game.Prefabs.PrefabRef>(candidate)) continue;
                if (EntityManager.GetComponentData<global::Game.Prefabs.PrefabRef>(candidate)
                        .m_Prefab == prefab) isolated++;
            }
            return isolated;
        }

        private bool ValidateTransactionOriginal(Entity entity, Temp temp, bool isNode, bool isEdge,
            HashSet<Entity> enabledTransactionConnections, out string reason)
        {
            reason = null;
            Entity original = temp.m_Original;
            if (original == Entity.Null) return true;
            if (!EntityManager.Exists(original) || EntityManager.HasComponent<Deleted>(original) ||
                EntityManager.HasComponent<Temp>(original))
            {
                reason = "a referenced original vanished between arm and commit";
                return false;
            }
            // A valid split/replacement can mark every old edge Deleted inside this transaction.
            // Treat that as safe only when the generated graph below supplies replacement
            // connectivity; an unrelated teardown has no such enabled transaction edge.
            if (isNode)
            {
                bool replacesEdge = (temp.m_Flags & TempFlags.Replace) != 0 &&
                                    EntityManager.HasComponent<Edge>(original);
                if (!EntityManager.HasComponent<Node>(original) && !replacesEdge)
                {
                    reason = "a generated node has an invalid original type";
                    return false;
                }
                if (!replacesEdge && (temp.m_Flags & TempFlags.Delete) == 0 &&
                    IsNodeBeingDeleted(original) &&
                    !enabledTransactionConnections.Contains(entity))
                {
                    // A node whose complete old connectivity is being removed is safe only when
                    // this same transaction supplies its replacement edge. Otherwise ApplyNetSystem
                    // can consume the lingering node after its last real edge has vanished.
                    reason = "a referenced original node is being torn down without replacement connectivity";
                    return false;
                }
                // GenerateNodesSystem deliberately gives a split node the original Edge and
                // TempFlags.Replace. ApplyNetSystem then uses that pair to split the edge.
            }
            if (isEdge && !EntityManager.HasComponent<Edge>(original))
            {
                reason = "a generated edge references a non-edge original";
                return false;
            }

            bool updatesOriginal = (temp.m_Flags & (TempFlags.Delete | TempFlags.Replace |
                                                    TempFlags.Combine)) == 0;
            if (updatesOriginal && (isNode || isEdge) &&
                !ValidateNetPrefabReference(entity, out reason)) return false;

            // The connectivity repair pass reads every edge referenced by an updated node without
            // checking whether the entity still carries Edge.
            if (isNode && updatesOriginal && EntityManager.HasBuffer<ConnectedEdge>(original))
            {
                DynamicBuffer<ConnectedEdge> edges =
                    EntityManager.GetBuffer<ConnectedEdge>(original, isReadOnly: true);
                for (int i = 0; i < edges.Length; i++)
                {
                    Entity edge = edges[i].m_Edge;
                    if (!EntityManager.Exists(edge) || !EntityManager.HasComponent<Edge>(edge))
                    {
                        reason = "an original node contains a stale connected-edge reference";
                        return false;
                    }
                }
            }

            if (isEdge && updatesOriginal &&
                !EntityManager.HasBuffer<ConnectedNode>(original))
            {
                reason = "an original edge has no connected-node buffer";
                return false;
            }
            return true;
        }

        private bool ValidateTempNode(Entity entity, Temp temp, out string reason)
        {
            reason = null;
            if ((temp.m_Flags & TempFlags.Delete) == 0 &&
                !ValidateNetPrefabReference(entity, out reason)) return false;
            return true;
        }

        private bool ValidateTempEdge(Entity entity, Temp temp, HashSet<Entity> members,
            HashSet<Entity> enabledTransactionConnections, out string reason)
        {
            reason = null;
            if ((temp.m_Flags & TempFlags.Delete) != 0) return true;
            if (!ValidateNetPrefabReference(entity, out reason)) return false;
            if (!EntityManager.HasBuffer<ConnectedNode>(entity))
            {
                reason = "a generated edge has no connected-node buffer";
                return false;
            }

            Edge edge = EntityManager.GetComponentData<Edge>(entity);
            if (!ValidateTempEndpoint(edge.m_Start, members,
                    enabledTransactionConnections, out reason) ||
                !ValidateTempEndpoint(edge.m_End, members,
                    enabledTransactionConnections, out reason)) return false;

            DynamicBuffer<ConnectedNode> nodes =
                EntityManager.GetBuffer<ConnectedNode>(entity, isReadOnly: true);
            for (int i = 0; i < nodes.Length; i++)
                if (!ValidateConnectedNodeForApply(nodes[i].m_Node,
                        enabledTransactionConnections, out reason)) return false;

            if (temp.m_Original != Entity.Null &&
                (temp.m_Flags & (TempFlags.Replace | TempFlags.Combine)) == 0)
            {
                DynamicBuffer<ConnectedNode> originalNodes =
                    EntityManager.GetBuffer<ConnectedNode>(temp.m_Original, isReadOnly: true);
                for (int i = 0; i < originalNodes.Length; i++)
                {
                    Entity node = originalNodes[i].m_Node;
                    if (!EntityManager.Exists(node) || !EntityManager.HasBuffer<ConnectedEdge>(node))
                    {
                        reason = "an original edge contains a stale connected-node reference";
                        return false;
                    }
                }
            }
            return true;
        }

        private bool ValidateTempEndpoint(Entity node, HashSet<Entity> members,
            HashSet<Entity> enabledTransactionConnections, out string reason)
        {
            if (node == Entity.Null || !members.Contains(node) || !EntityManager.Exists(node) ||
                !EntityManager.HasComponent<Temp>(node) || !EntityManager.HasComponent<Node>(node) ||
                EntityManager.HasComponent<Deleted>(node) || EntityManager.HasComponent<Disabled>(node) ||
                !EntityManager.HasBuffer<ConnectedEdge>(node))
            {
                reason = "a generated edge endpoint is outside the enabled Temp transaction";
                return false;
            }
            return ValidateConnectedNodeForApply(node, enabledTransactionConnections, out reason);
        }

        private bool ValidateConnectedNodeForApply(Entity node,
            HashSet<Entity> enabledTransactionConnections,
            out string reason)
        {
            reason = null;
            if (!EntityManager.Exists(node) || !EntityManager.HasComponent<Node>(node))
            {
                reason = "a generated edge contains a missing connected node";
                return false;
            }

            Entity effective = node;
            if (EntityManager.HasComponent<Temp>(node))
            {
                Temp nodeTemp = EntityManager.GetComponentData<Temp>(node);
                if (nodeTemp.m_Original != Entity.Null &&
                    (nodeTemp.m_Flags & (TempFlags.Delete | TempFlags.Replace)) == 0)
                    effective = nodeTemp.m_Original;
            }
            if (!EntityManager.Exists(effective) || EntityManager.HasComponent<Deleted>(effective) ||
                !EntityManager.HasBuffer<ConnectedEdge>(effective))
            {
                reason = "a generated edge resolves to a node without connectivity data";
                return false;
            }
            if (IsNodeBeingDeleted(effective) &&
                !enabledTransactionConnections.Contains(node) &&
                !enabledTransactionConnections.Contains(effective))
            {
                reason = "a generated edge resolves to a node being torn down";
                return false;
            }
            return true;
        }

        private HashSet<Entity> CollectEnabledTransactionConnections(HashSet<Entity> members)
        {
            var result = new HashSet<Entity>();
            if (members == null) return result;
            foreach (Entity candidate in members)
            {
                if (!EntityManager.Exists(candidate) ||
                    !EntityManager.HasComponent<Temp>(candidate) ||
                    !EntityManager.HasComponent<Edge>(candidate) ||
                    EntityManager.HasComponent<Deleted>(candidate) ||
                    EntityManager.HasComponent<Disabled>(candidate))
                    continue;

                Temp edgeTemp = EntityManager.GetComponentData<Temp>(candidate);
                if ((edgeTemp.m_Flags & TempFlags.Delete) != 0) continue;
                Edge edge = EntityManager.GetComponentData<Edge>(candidate);
                AddEffectiveTransactionConnection(result, edge.m_Start);
                AddEffectiveTransactionConnection(result, edge.m_End);
            }
            return result;
        }

        private void AddEffectiveTransactionConnection(HashSet<Entity> connections, Entity node)
        {
            if (node == Entity.Null) return;
            connections.Add(node);
            if (!EntityManager.Exists(node) || !EntityManager.HasComponent<Temp>(node)) return;

            Temp temp = EntityManager.GetComponentData<Temp>(node);
            if (temp.m_Original != Entity.Null &&
                (temp.m_Flags & (TempFlags.Delete | TempFlags.Replace)) == 0 &&
                EntityManager.Exists(temp.m_Original) &&
                EntityManager.HasComponent<Node>(temp.m_Original))
                connections.Add(temp.m_Original);
        }

        private bool ValidateNetPrefabReference(Entity entity, out string reason)
        {
            reason = null;
            if (!EntityManager.HasComponent<global::Game.Prefabs.PrefabRef>(entity))
            {
                reason = "a generated net entity has no prefab reference";
                return false;
            }
            Entity prefab = EntityManager.GetComponentData<global::Game.Prefabs.PrefabRef>(entity).m_Prefab;
            if (prefab == Entity.Null || !EntityManager.Exists(prefab) ||
                !EntityManager.HasComponent<global::Game.Prefabs.PrefabData>(prefab))
            {
                reason = "a generated net entity references a missing prefab";
                return false;
            }
            return true;
        }

        private void DiscardStaleTransactionTemps(string why)
        {
            int cleared = ClearTempEntities(ActiveTransactionQuery());
            if (cleared <= 0) return;
            Mod.log.Warn("[MP] SyncApply: discarded " + cleared + " uncommitted Temp(s) - " + why + ".");
            Diagnostics.FlightRecorder.Note("transaction temps discarded=" + cleared + " (" + why + ")");
        }

        /// <summary>
        /// Arm the isolated net-domain commit for definitions a sibling system (delete/replace)
        /// created this frame. They become Temp net entities at the following Modification and
        /// <see cref="RealizePending"/> applies them natively. Only call when
        /// <see cref="CanBuildDefinitions"/> is true (and after
        /// <see cref="PrepareDefinitionFrame"/>). <paramref name="onCommitLost"/> is invoked if the
        /// armed batch never materialises (the apply window expiring) - it must re-queue the batch's
        /// source commands so the work is rebuilt, not lost.
        /// </summary>
        public void ArmNetCommit(System.Action onCommitLost, string source)
        {
            ArmNetCommit(onCommitLost, null, source);
        }

        /// <summary>
        /// Arm one correlated net mutation graph and retain its completion callback until the
        /// committed Temp graph has fully drained.
        /// </summary>
        public bool ArmNetCommit(System.Action onCommitLost,
            System.Action onCommitComplete, string source)
        {
            if (IsCommitBusy) return false;
            _pendingApply = true;
            _pendingTransactionKind = RemoteToolTransactionKind.Net;
            _pendingOwnerDefinitions.Clear();
            _describedOwners.Clear();
            _lastDescribedOwner = Entity.Null;
            _armTick = System.Environment.TickCount;
            _pendingNetConstructionCharge = 0;
            _pendingNetConstructionChargeCourses = 0;
            _onCommitLost = onCommitLost;
            _onCommitComplete = onCommitComplete;
            Diagnostics.FlightRecorder.Note("net " + source + " batch armed");
            return true;
        }

        /// <summary>
        /// Arm one route root and its complete waypoint/segment graph. Definitions materialize as
        /// Temps later in the frame; the next quiet ToolUpdate validates and applies only that
        /// isolated route domain.
        /// </summary>
        public bool ArmRouteCommit(System.Action onCommitLost,
            System.Action onCommitComplete, string source)
        {
            if (IsCommitBusy || _applyRoutesSystem == null)
                return false;
            _pendingApply = true;
            _pendingTransactionKind = RemoteToolTransactionKind.Route;
            _pendingOwnerDefinitions.Clear();
            _describedOwners.Clear();
            _lastDescribedOwner = Entity.Null;
            _armTick = System.Environment.TickCount;
            _pendingNetConstructionCharge = 0;
            _pendingNetConstructionChargeCourses = 0;
            _onCommitLost = onCommitLost;
            _onCommitComplete = onCommitComplete;
            Diagnostics.FlightRecorder.Note("route " + source + " operation armed");
            return true;
        }

        /// <summary>
        /// Arm one object graph. Its Object, owned Node/Edge/Lane/Aggregate, and Area Temps are
        /// validated and consumed together. The source callback is retained until drain completes.
        /// </summary>
        public bool ArmObjectCommit(System.Action onCommitLost, System.Action onCommitComplete,
            string source, bool rootlessAssetStamp = false,
            List<ArmedOwnerDefinition> ownerDefinitions = null)
        {
            if (IsCommitBusy) return false;
            _pendingApply = true;
            _pendingTransactionKind = rootlessAssetStamp
                ? RemoteToolTransactionKind.AssetStampGraph
                : RemoteToolTransactionKind.ObjectGraph;
            _pendingOwnerDefinitions.Clear();
            _describedOwners.Clear();
            _lastDescribedOwner = Entity.Null;
            if (ownerDefinitions != null) _pendingOwnerDefinitions.AddRange(ownerDefinitions);
            _armTick = System.Environment.TickCount;
            _pendingNetConstructionCharge = 0;
            _pendingNetConstructionChargeCourses = 0;
            _onCommitLost = onCommitLost;
            _onCommitComplete = onCommitComplete;
            Diagnostics.FlightRecorder.Note((rootlessAssetStamp ? "asset stamp " : "object ") +
                                               source + " operation armed");
            return true;
        }

        private void ChargeCommittedNetConstruction()
        {
            long amount = _committingNetConstructionCharge;
            int courses = _committingNetConstructionChargeCourses;
            _committingNetConstructionCharge = 0;
            _committingNetConstructionChargeCourses = 0;
            if (amount <= 0) return;

            try
            {
                ConstructionCharger.ChargeAmount(EntityManager, amount,
                    "remote net operation (" + courses + " course(s))");
            }
            catch (System.Exception ex)
            {
                // Charging is accounting, not geometry. Never destabilize a successfully committed
                // network transaction merely because the money singleton changed unexpectedly.
                Mod.log.Warn("[MP] NetSync: remote net charge failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Record a span this machine just realized from a remote command, so capture-side heuristics
        /// (NetReplaceSync's extension detection) can recognise follow-on local edits of that geometry
        /// - e.g. the game's node reduction merging it into a neighbour - as remote work, not
        /// something to broadcast back.
        /// </summary>
        public void RecordRealizedSpan(Bezier4x3 curve)
        {
            long now = Mod.Service != null ? Mod.Service.NowMs : 0;
            _recentRealizedSpans.Add((curve, now + 10000));
        }

        /// <summary>True when <paramref name="piece"/> is a 3D sub-curve of a recently realized span.</summary>
        public bool WasRecentlyRealized(Bezier4x3 piece)
        {
            for (int i = 0; i < _recentRealizedSpans.Count; i++)
                if (SplitMatch.IsSubCurve3D(piece, _recentRealizedSpans[i].curve)) return true;
            return false;
        }

        private void PruneRecentRealizedSpans()
        {
            if (_recentRealizedSpans.Count == 0 || Mod.Service == null) return;
            long now = Mod.Service.NowMs;
            for (int i = _recentRealizedSpans.Count - 1; i >= 0; i--)
                if (_recentRealizedSpans[i].expiresMs < now) _recentRealizedSpans.RemoveAt(i);
        }

        private static System.Reflection.FieldInfo _forceUpdateField;
        private static bool _forceUpdateFieldResolved;

        /// <summary>
        /// Set the tool's protected <c>m_ForceUpdate</c> flag so it regenerates its preview
        /// definitions on its next update even with a motionless cursor - the definition gate removed
        /// the preview, and without this a parked cursor would show none until moved. Runtime access
        /// to the loaded game assembly's own member; a rename in a future patch degrades gracefully
        /// (the preview simply returns on the next cursor move).
        /// </summary>
        private void TryForceToolUpdate(global::Game.Tools.ToolBaseSystem tool)
        {
            if (!_forceUpdateFieldResolved)
            {
                _forceUpdateFieldResolved = true;
                _forceUpdateField = typeof(global::Game.Tools.ToolBaseSystem).GetField(
                    "m_ForceUpdate",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            }
            if (_forceUpdateField != null) _forceUpdateField.SetValue(tool, true);
        }

        /// <summary>
        /// <see cref="DefinitionGateSystem"/>'s hook: after it destroys the tool's buffered
        /// definitions on an armed frame, the tool must regenerate its gesture next update.
        /// </summary>
        public void ForceActiveToolUpdate()
        {
            global::Game.Tools.ToolBaseSystem tool = _toolSystem != null ? _toolSystem.activeTool : null;
            if (tool != null && !(tool is global::Game.Tools.DefaultToolSystem)) TryForceToolUpdate(tool);
        }

        /// <summary>
        /// Apply remote terrain samples through the brush domain only. Local brush previews were
        /// Disabled by <see cref="PrepareAuxiliaryTemps"/> and are restored after ToolOutputBarrier.
        /// </summary>
        public bool CommitAuxiliaryTempsNow()
        {
            if (_applyBrushesSystem == null) return false;
            _applyBrushesSystem.Update();
            return true;
        }
    }
}
