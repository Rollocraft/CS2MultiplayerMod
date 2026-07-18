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
    // Commit orchestration for NetSyncSystem. Remote net Temps are applied through the net domain
    // alone; local net and brush previews are temporarily Disabled so an unrelated tool can remain
    // selected without either transaction consuming the other one's entities.
    public partial class NetSyncSystem
    {
        /// <summary>How long an armed batch may wait for its commit before it is discarded and re-queued.</summary>
        private const int ApplyWindowMs = 3000;

        /// <summary>How long a committed batch's Temps may linger before they are force-cleared.</summary>
        private const int DrainWindowMs = 3000;

        /// <summary>
        /// Called by <see cref="SyncRealizeSystem"/> once per frame BEFORE any net-pipeline feeder
        /// (delete/replace/build) runs, so per-frame state is reset exactly once regardless of which
        /// feeder acts first.
        /// </summary>
        public void BeginRealizeFrame()
        {
            _prepDoneThisFrame = false;
            _realizeFrame++;
            // Last frame's commit-frame capture skip has served its purpose (the one-frame
            // Created tags it targeted are gone); a commit this frame re-sets it below.
            _suppressCaptureThisFrame = false;
            ProtectRemoteBatchForLocalToolOutput();
        }

        /// <summary>
        /// True on frames with an armed, not-yet-applied commit - the frames where
        /// <see cref="DefinitionGateSystem"/> must destroy the tool's freshly buffered definitions
        /// before they can materialise beside the isolated remote batch. The flag clears before the
        /// gate on the actual commit frame, so the player's preview resumes immediately afterward.
        /// </summary>
        public bool HasArmedNetCommit => _pendingApply;

        /// <summary>
        /// Called by <see cref="SyncRealizeSystem"/> during the ToolUpdate phase, where the
        /// NetCourse definition is consumed by <c>GenerateNodesSystem</c>/<c>GenerateEdgesSystem</c>
        /// in the same frame's Modification1/2 - created any later it would be silently
        /// discarded (see <see cref="SyncRealizeSystem"/>).
        /// </summary>
        public void RealizePending()
        {
            // Definitions created on the prior ToolUpdate have now become remote Temp net entities.
            // A quiet local-tool frame applies only that enabled net set. On a local Apply/Clear frame,
            // BeginRealizeFrame protected the remote set instead and this transaction waits intact.
            if (_pendingApply && !_localToolOutputProtectedThisFrame)
            {
                int isolatedCount = _tempNetEntities.CalculateEntityCount();
                if (isolatedCount > 0 && ArmedBatchReferencesVanishedOriginal())
                {
                    InvalidateArmedBatch("a referenced original vanished between arm and commit", isolatedCount);
                }
                else if (isolatedCount > 0)
                {
                    CommitRemoteNetTemps(isolatedCount);
                }
                else if (System.Environment.TickCount - _armTick > ApplyWindowMs)
                {
                    InvalidateArmedBatch("apply window expired before the batch materialised", isolatedCount);
                }
            }
            else if (_awaitingDrain)
            {
                _drainFrames++;
                if (!CommittedRemoteTempsRemain())
                {
                    _committingRemoteNetTemps.Clear();
                    _awaitingDrain = false;
                }
                else if (System.Environment.TickCount - _drainArmTick > DrainWindowMs)
                {
                    ClearTrackedTemps(_committingRemoteNetTemps, clearPreview: true);
                    _committingRemoteNetTemps.Clear();
                    _awaitingDrain = false;
                    Mod.log.Warn("[MP] NetApply: isolated remote commit did not drain; stale Temps cleared.");
                    Diagnostics.FlightRecorder.Note("net isolated commit did not drain; stale temps cleared");
                }
            }

            PruneRecentRealizedSpans();

            MultiplayerService service = Mod.Service;
            if (service == null) return;

            MultiplayerSession session = service.Session;
            if (!service.GameplaySyncReady) return;
            RealizeIncoming(session, service.NowMs);
        }

        /// <summary>
        /// True while a net-Temp commit is armed or draining. Only one batch (build OR delete OR
        /// replace) enters any one net-domain pass - a split course and a delete of the same edge in
        /// the same commit can make ApplyNetSystem dereference a stale edge and native-crash.
        /// </summary>
        public bool IsCommitBusy => _pendingApply || _awaitingDrain;

        /// <summary>
        /// True until queued placement courses and their commit/drain have become queryable network
        /// geometry. Systems that attach to or edit roads must not overtake this boundary.
        /// </summary>
        public bool HasPlacementBacklog => !_incoming.IsEmpty || _remoteDeferred.Count > 0 || IsCommitBusy;

        /// <summary>
        /// True when a feeder may create Temp-backed work. An interactive tool may stay selected;
        /// only its actual Apply/Clear frame gets priority. Quiet preview frames are isolated below.
        /// </summary>
        public bool CanBuildDefinitions
        {
            get
            {
                if (_pendingApply || _awaitingDrain) return false;
                global::Game.Tools.ToolBaseSystem tool = _toolSystem != null ? _toolSystem.activeTool : null;
                return tool == null || tool is global::Game.Tools.DefaultToolSystem ||
                       tool.applyMode == global::Game.Tools.ApplyMode.None;
            }
        }

        /// <summary>
        /// Isolate the local net portion of the active preview before remote definitions materialise.
        /// Disabled preview entities are excluded from generation and from the isolated remote apply;
        /// other preview domains remain visible and untouched.
        /// </summary>
        public void PrepareDefinitionFrame()
        {
            if (_prepDoneThisFrame) return;
            _prepDoneThisFrame = true;

            if (_isolatedLocalNetTemps.Count > 0) ReleaseTrackedTemps(_isolatedLocalNetTemps);
            DisableQueryEntities(_tempNetEntities, _isolatedLocalNetTemps);
            if (_isolatedLocalNetTemps.Count > 0)
                Diagnostics.FlightRecorder.Note("net preview isolated=" + _isolatedLocalNetTemps.Count);
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
            if (temp.m_Original != Entity.Null && EntityManager.Exists(temp.m_Original)
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
            EntityManager.AddComponent<Deleted>(e);
            return true;
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
            if (tool == null || (tool.applyMode != global::Game.Tools.ApplyMode.Apply &&
                                 tool.applyMode != global::Game.Tools.ApplyMode.Clear)) return;

            _protectedRemoteNetTemps.Clear();
            DisableQueryEntities(_tempNetEntities, _protectedRemoteNetTemps);
            ReleaseLocalNetTempsForTool(tool);
            _localToolOutputProtectedThisFrame = true;
            Diagnostics.FlightRecorder.Note("net remote batch protected for local " + tool.applyMode +
                " (remote=" + _protectedRemoteNetTemps.Count + ")");
        }

        private void ReleaseLocalNetTempsForTool(global::Game.Tools.ToolBaseSystem tool)
        {
            if (tool is global::Game.Tools.NetToolSystem)
            {
                ReleaseTrackedTemps(_isolatedLocalNetTemps);
                return;
            }
            if (!(tool is global::Game.Tools.BulldozeToolSystem)) return;

            // A bulldozer Apply/Clear only owns delete previews. A disabled create/replace preview
            // can be left behind by a prior road tool and must not ride along with this click.
            for (int i = _isolatedLocalNetTemps.Count - 1; i >= 0; i--)
            {
                Entity entity = _isolatedLocalNetTemps[i];
                if (!EntityManager.Exists(entity) || !EntityManager.HasComponent<Temp>(entity))
                {
                    _isolatedLocalNetTemps.RemoveAt(i);
                    continue;
                }
                Temp temp = EntityManager.GetComponentData<Temp>(entity);
                if ((temp.m_Flags & TempFlags.Delete) == 0) continue;
                if (EntityManager.HasComponent<Disabled>(entity))
                    EntityManager.RemoveComponent<Disabled>(entity);
                _isolatedLocalNetTemps.RemoveAt(i);
            }
        }

        private void CommitRemoteNetTemps(int count)
        {
            MultiplayerService currentService = Mod.Service;
            RecordPlacementOriginals(currentService != null ? currentService.NowMs : 0);

            _committingRemoteNetTemps.Clear();
            NativeArray<Entity> remoteTemps = _tempNetEntities.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < remoteTemps.Length; i++)
                    _committingRemoteNetTemps.Add(remoteTemps[i]);
            }
            finally
            {
                remoteTemps.Dispose();
            }

            try
            {
                _applyNetSystem.Update();
            }
            catch (System.Exception ex)
            {
                _committingRemoteNetTemps.Clear();
                Diagnostics.FlightRecorder.Note("net isolated apply failed: " + ex.GetType().Name);
                InvalidateArmedBatch("isolated apply failed (" + ex.GetType().Name + ")", count);
                return;
            }

            _pendingApply = false;
            _onCommitLost = null;
            _expiryReplays = 0;
            _awaitingDrain = true;
            _drainArmTick = System.Environment.TickCount;
            _drainFrames = 0;
            _suppressCaptureThisFrame = true;
            _clearLocalNetIsolationAfterBarrier = true;
            Diagnostics.FlightRecorder.Note("net commit isolated (temps=" + count + ")");
        }

        private bool CommittedRemoteTempsRemain()
        {
            for (int i = 0; i < _committingRemoteNetTemps.Count; i++)
            {
                Entity entity = _committingRemoteNetTemps[i];
                if (EntityManager.Exists(entity) && EntityManager.HasComponent<Temp>(entity) &&
                    !EntityManager.HasComponent<Deleted>(entity)) return true;
            }
            return false;
        }

        private void InvalidateArmedBatch(string reason, int count)
        {
            _pendingApply = false;
            _awaitingDrain = false;
            if (count > 0) DiscardStaleNetTemps(reason);
            ReleaseTrackedTemps(_isolatedLocalNetTemps);

            System.Action replay = _onCommitLost;
            _onCommitLost = null;
            if (replay != null && _expiryReplays < 3)
            {
                _expiryReplays++;
                Mod.log.Warn("[MP] NetApply: " + reason + "; re-queueing batch (attempt " +
                             _expiryReplays + "/3).");
                Diagnostics.FlightRecorder.Note("net batch invalidated; replay " + _expiryReplays + "/3");
                replay();
            }
            else
            {
                Mod.log.Warn("[MP] NetApply: " + reason + "; batch dropped" +
                             (replay != null ? " after " + _expiryReplays + " replays." : "."));
                Diagnostics.FlightRecorder.Note("net batch invalidated; dropped");
            }
        }

        /// <summary>Finish structural isolation after ToolOutputBarrier has consumed this frame.</summary>
        public void FinishIsolationAfterToolOutput()
        {
            if (_protectedRemoteNetTemps.Count > 0) ReleaseTrackedTemps(_protectedRemoteNetTemps);
            _localToolOutputProtectedThisFrame = false;

            if (_clearLocalNetIsolationAfterBarrier)
            {
                int cleared = ClearTrackedTemps(_isolatedLocalNetTemps, clearPreview: true);
                _isolatedLocalNetTemps.Clear();
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
            ReleaseTrackedTemps(_isolatedLocalNetTemps);
            ReleaseTrackedTemps(_isolatedLocalBrushTemps);
            _localToolOutputProtectedThisFrame = false;
            _clearLocalNetIsolationAfterBarrier = false;
        }

        public bool CanApplyAuxiliaryTemps
        {
            get
            {
                if (_pendingApply || _awaitingDrain) return false;
                global::Game.Tools.ToolBaseSystem tool = _toolSystem != null ? _toolSystem.activeTool : null;
                // The direct brush pass runs before ToolOutputSystem. A later Clear pass only
                // discards Temps (local brush previews are isolated below), but a later Apply pass
                // would run ApplyBrushesSystem again and apply the remote samples twice.
                return tool == null || tool.applyMode != global::Game.Tools.ApplyMode.Apply;
            }
        }

        public void PrepareAuxiliaryTemps()
        {
            if (_isolatedLocalBrushTemps.Count > 0) ReleaseTrackedTemps(_isolatedLocalBrushTemps);
            DisableQueryEntities(_localBrushTemps, _isolatedLocalBrushTemps);
        }

        /// <summary>
        /// True when any live net Temp references an original entity that no longer exists or is
        /// being torn down. Split targets and reuse nodes were resolved when the batch was built —
        /// a frame before the commit — and ApplyNetSystem dereferences originals unchecked, so a
        /// batch this has gone stale under must be discarded, never committed. Runs only on frames
        /// with an armed commit; cost is a component read per standing Temp.
        /// </summary>
        private bool ArmedBatchReferencesVanishedOriginal()
        {
            NativeArray<Entity> temps = _tempNetEntities.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < temps.Length; i++)
                {
                    if (!EntityManager.HasComponent<Temp>(temps[i])) continue;
                    Entity original = EntityManager.GetComponentData<Temp>(temps[i]).m_Original;
                    if (original == Entity.Null) continue;
                    if (!EntityManager.Exists(original) || EntityManager.HasComponent<Deleted>(original))
                        return true;
                    // Net cleanup can leave a node entity alive for a frame after every connected
                    // edge has entered deletion. It is just as stale to ApplyNetSystem as a node
                    // already carrying Deleted, even though Exists still returns true.
                    if (EntityManager.HasComponent<Node>(original) && IsNodeBeingDeleted(original))
                        return true;
                }
            }
            finally
            {
                temps.Dispose();
            }
            return false;
        }

        private void DiscardStaleNetTemps(string why)
        {
            int cleared = ClearTempEntities(_tempNetEntities);
            if (cleared <= 0) return;
            Mod.log.Warn("[MP] NetApply: discarded " + cleared + " uncommitted net Temp(s) - " + why + ".");
            Diagnostics.FlightRecorder.Note("net temps discarded=" + cleared + " (" + why + ")");
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
            if (_pendingApply || _awaitingDrain) return;
            _pendingApply = true;
            _armTick = System.Environment.TickCount;
            _onCommitLost = onCommitLost;
            Diagnostics.FlightRecorder.Note("net " + source + " batch armed");
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
        /// Disabled by <see cref="PrepareAuxiliaryTemps"/> only for this direct pass. Restore them
        /// before ToolOutputSystem so a pending local Clear still disposes its previous preview.
        /// </summary>
        public bool CommitAuxiliaryTempsNow()
        {
            try
            {
                if (_applyBrushesSystem == null) return false;
                _applyBrushesSystem.Update();
                return true;
            }
            finally
            {
                ReleaseTrackedTemps(_isolatedLocalBrushTemps);
            }
        }
    }
}
