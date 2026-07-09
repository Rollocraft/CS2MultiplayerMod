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
    // Commit orchestration for NetSyncSystem: the once-per-frame ToolUpdate entry point that commits a
    // queued realize batch (by flipping the active tool's applyMode), waits for it to drain, then
    // drains the next batch - plus the preview hijack that makes commits possible while the local
    // player has a build tool out, and the reflection that drives the ApplyTool phase.
    public partial class NetSyncSystem
    {
        /// <summary>
        /// Called by <see cref="SyncRealizeSystem"/> once per frame BEFORE any net-pipeline feeder
        /// (delete/replace/build) runs, so per-frame state is reset exactly once regardless of which
        /// feeder acts first.
        /// </summary>
        public void BeginRealizeFrame()
        {
            _prepDoneThisFrame = false;
            // Last frame's commit-frame capture skip has served its purpose (the one-frame
            // Created tags it targeted are gone); a commit this frame re-sets it below.
            _suppressCaptureThisFrame = false;
        }

        /// <summary>
        /// Called by <see cref="SyncRealizeSystem"/> during the ToolUpdate phase, where the
        /// NetCourse definition is consumed by <c>GenerateNodesSystem</c>/<c>GenerateEdgesSystem</c>
        /// in the same frame's Modification1/2 - created any later it would be silently
        /// discarded (see <see cref="SyncRealizeSystem"/>).
        /// </summary>
        public void RealizePending()
        {
            // Runs at ToolUpdate - AFTER the tools (which set their applyMode each frame) and BEFORE
            // ToolOutputSystem (UpdateAfter in ToolUpdate). That ordering is the whole trick: it's the
            // one window where flipping applyMode sticks long enough for ToolOutputSystem to read it
            // and run the ApplyTool phase (in its own valid context).

            // (A) Commit a previously-queued batch. Its definitions (created in an earlier ToolUpdate)
            // were consumed at the following Modification into Temp edges that persist uncommitted.
            // Flip applyMode=Apply now so this frame's ToolOutputSystem commits them - splits,
            // connections and zoning handled natively. We must NOT drive ApplyTool ourselves: from a
            // Modification-nested system its barriers crash ("EntityCommandBuffer not allowed").
            //
            // This works with ANY active tool, not just the idle default: the def-frame's preview
            // hijack (PrepareDefinitionFrame) wiped the tool's own preview Temps and its pending
            // definitions, so the world's Temps here are OURS ALONE - overriding the tool's Clear/None
            // with Apply commits exactly our batch and nothing the player is previewing. The tool's
            // fresh definitions (made this frame, before us) only materialise at this frame's
            // Modification, AFTER the ApplyTool pass - its preview returns untouched right behind our
            // commit.
            if (_pendingApply)
            {
                int count = _tempNetEntities.CalculateEntityCount();
                global::Game.Tools.ToolBaseSystem tool = _toolSystem != null ? _toolSystem.activeTool : null;
                bool toolIdle = tool == null || tool is global::Game.Tools.DefaultToolSystem;
                global::Game.Tools.ApplyMode toolMode =
                    tool != null ? tool.applyMode : global::Game.Tools.ApplyMode.None;

                if (count > 0 && !toolIdle && toolMode == global::Game.Tools.ApplyMode.Apply)
                {
                    // The player's click drives the ApplyTool pass this frame and our armed batch
                    // rides along. Track it as a normal commit. NO capture-suppress window here: the
                    // player's own work is Created this same frame and a blanket skip would swallow
                    // its broadcast - the per-edge ReplicationGuard marks set at realize still catch
                    // our batch's echoes individually.
                    _pendingApply = false;
                    _onCommitLost = null;
                    _expiryReplays = 0;
                    _awaitingDrain = true;
                    _drainArmTick = System.Environment.TickCount;
                    _drainFrames = 0;
                }
                else if (count > 0 && TrySetApplyModeApply())
                {
                    _pendingApply = false;
                    _onCommitLost = null;
                    _expiryReplays = 0;
                    // Don't build the next batch until these Temps actually commit and clear; until then
                    // the new nodes/edges aren't query-able and the next batch couldn't connect to them.
                    _awaitingDrain = true;
                    _drainArmTick = System.Environment.TickCount;
                    _drainFrames = 0;
                    // Skip capture at THIS frame's ModificationEnd only: the pass commits our
                    // batch and nothing of the player's (their gesture isn't applying on a
                    // self-flip frame). Echoes surfacing on later frames (node-reduction merges)
                    // are caught per-edge by the realize guard marks, like the ride-along path.
                    _suppressCaptureThisFrame = true;
                }
                else if (count == 0 && System.Environment.TickCount - _armTick > 3000)
                {
                    // Temps never materialised (definition rejected?). Replay a few times, then stop -
                    // a batch the game always rejects must not rebuild forever.
                    _pendingApply = false;
                    System.Action replay = _onCommitLost;
                    _onCommitLost = null;
                    if (replay != null && _expiryReplays < 3)
                    {
                        _expiryReplays++;
                        Mod.log.Warn("[MP] NetApply: apply window expired with no Temp entities - re-queueing batch (attempt " +
                                     _expiryReplays + "/3).");
                        replay();
                    }
                    else
                    {
                        Mod.log.Warn("[MP] NetApply: apply window expired with no Temp entities - batch dropped" +
                                     (replay != null ? " after " + _expiryReplays + " replays." : "."));
                    }
                }
                else
                {
                    // Still waiting: Temps not materialised yet (or a click frame with none of ours
                    // present). While a build tool is out, keep hijacking each waiting frame so the
                    // tool's fresh definitions can't materialise into the pending window and pollute
                    // the eventual commit.
                    if (count == 0 && !toolIdle) PrepareDefinitionFrame();
                }
            }
            // (A2) Wait for a committed batch's Temp entities to drain (become real) before building the
            // next one. Idle: the count hits 0 within a frame. With a build tool out its preview Temps
            // regenerate immediately, so the count never reaches 0 - but the committed geometry is
            // query-able one frame after the ApplyTool pass, which the frame counter covers. The
            // timeout is a safety valve so a stuck commit can't wedge the realize pipeline forever.
            else if (_awaitingDrain)
            {
                _drainFrames++;
                int temps = _tempNetEntities.CalculateEntityCount();
                bool toolIdle = _toolSystem == null || _toolSystem.activeTool == null
                    || _toolSystem.activeTool is global::Game.Tools.DefaultToolSystem;
                if (temps == 0)
                {
                    _awaitingDrain = false;
                }
                else if (!toolIdle && _drainFrames >= 2)
                {
                    // The remaining Temps are the tool's regenerated preview, not our batch.
                    _awaitingDrain = false;
                }
                else if (System.Environment.TickCount - _drainArmTick > 3000)
                {
                    _awaitingDrain = false;
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
        /// replace) enters any one ApplyTool pass - a split course and a delete of the same edge in
        /// the same commit can make ApplyNetSystem dereference a stale edge and native-crash.
        /// </summary>
        public bool IsCommitBusy => _pendingApply || _awaitingDrain;

        /// <summary>
        /// True when a feeder (build/delete/replace) may create net definitions this frame. False
        /// while a commit is in flight, and on the frame the player's own gesture applies - their
        /// preview must survive to be committed by their click, so we never hijack that frame.
        /// With any other tool state the pipeline runs LIVE: the def-frame wipes the tool's preview
        /// (<see cref="PrepareDefinitionFrame"/>) and the commit overrides its applyMode next frame.
        /// </summary>
        public bool CanBuildDefinitions
        {
            get
            {
                if (_pendingApply || _awaitingDrain) return false;
                global::Game.Tools.ToolBaseSystem tool = _toolSystem != null ? _toolSystem.activeTool : null;
                if (tool == null || tool is global::Game.Tools.DefaultToolSystem) return true;
                return tool.applyMode != global::Game.Tools.ApplyMode.Apply;
            }
        }

        /// <summary>
        /// Make this frame safe for creating our net definitions while the local player has a build
        /// tool out. Observed runtime behaviour this mirrors: the game's clear pass (the only ClearTool
        /// system) deletes EVERY Temp entity in the world and restores originals it was hiding, and
        /// each tool destroys its own definitions before regenerating them. We do both here - destroy
        /// the tool's fresh definitions (created before us this ToolUpdate, or they'd materialise as
        /// preview Temps inside our commit), clear all live preview Temps the same way the clear pass
        /// does, then set the tool's force-update flag so it rebuilds its preview from its own retained
        /// gesture (control points survive; the preview blinks for one frame). Idempotent per frame;
        /// no-op while the idle default tool is active (nothing to hijack).
        ///
        /// Callers: every feeder, immediately before creating its first definition of the frame.
        /// Feeders gate on <see cref="CanBuildDefinitions"/>, so no armed batch of ours exists here -
        /// the Temps cleared are only ever the local player's preview.
        /// </summary>
        public void PrepareDefinitionFrame()
        {
            if (_prepDoneThisFrame) return;
            _prepDoneThisFrame = true;

            global::Game.Tools.ToolBaseSystem tool = _toolSystem != null ? _toolSystem.activeTool : null;
            if (tool == null || tool is global::Game.Tools.DefaultToolSystem) return;

            int defs = 0;
            if (!_freshDefinitions.IsEmptyIgnoreFilter)
            {
                // Only NON-Permanent definitions can pollute the commit window (they materialise as
                // Temps; Permanent ones build real entities directly and never enter ApplyTool).
                // Permanent definitions here are a sibling realize from THIS frame - a remote
                // building/upgrade/move/area/route created before this wipe - or the game's own
                // simulation spawns; destroying them silently killed those placements whenever a
                // net batch realized in the same frame with a build tool out.
                NativeArray<Entity> defEntities = _freshDefinitions.ToEntityArray(Allocator.Temp);
                try
                {
                    for (int i = 0; i < defEntities.Length; i++)
                    {
                        CreationDefinition def =
                            EntityManager.GetComponentData<CreationDefinition>(defEntities[i]);
                        if ((def.m_Flags & CreationFlags.Permanent) != 0) continue;
                        EntityManager.DestroyEntity(defEntities[i]);
                        defs++;
                    }
                }
                finally
                {
                    defEntities.Dispose();
                }
            }

            int temps = 0;
            if (!_allTempEntities.IsEmptyIgnoreFilter)
            {
                NativeArray<Entity> tempEntities = _allTempEntities.ToEntityArray(Allocator.Temp);
                try
                {
                    for (int i = 0; i < tempEntities.Length; i++)
                    {
                        Entity e = tempEntities[i];
                        if (!EntityManager.Exists(e) || EntityManager.HasComponent<Deleted>(e)) continue;

                        Temp temp = EntityManager.GetComponentData<Temp>(e);
                        // A preview that was hiding its original (modify/move ghosts) must restore it,
                        // exactly like the game's clear pass - or the road/building stays invisible.
                        if (temp.m_Original != Entity.Null && EntityManager.Exists(temp.m_Original)
                            && EntityManager.HasComponent<Hidden>(temp.m_Original))
                        {
                            EntityManager.RemoveComponent<Hidden>(temp.m_Original);
                            EntityManager.AddComponent<BatchesUpdated>(temp.m_Original);
                        }
                        // Highlighted street-name aggregates get their highlight dropped with the Temp.
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
                        temps++;
                    }
                }
                finally
                {
                    tempEntities.Dispose();
                }
            }

            TryForceToolUpdate(tool);
        }

        /// <summary>
        /// Arm the ApplyTool commit for net definitions a sibling system (delete/replace) created this
        /// frame. They become Temp net entities at the following Modification; the commit flow (part A
        /// of <see cref="RealizePending"/>) flips applyMode next frame and ApplyNetSystem commits
        /// them natively. Only call when <see cref="CanBuildDefinitions"/> is true (and after
        /// <see cref="PrepareDefinitionFrame"/>). <paramref name="onCommitLost"/> is invoked if the
        /// armed batch never materialises (the apply window expiring) - it must re-queue the batch's
        /// source commands so the work is rebuilt, not lost.
        /// </summary>
        public void ArmNetCommit(System.Action onCommitLost)
        {
            if (_pendingApply || _awaitingDrain) return;
            _pendingApply = true;
            _armTick = System.Environment.TickCount;
            _onCommitLost = onCommitLost;
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

        private static System.Reflection.MethodInfo _applyModeSetter;
        private static bool _applyModeSetterResolved;
        private static System.Reflection.FieldInfo _forceUpdateField;
        private static bool _forceUpdateFieldResolved;

        /// <summary>
        /// Flip the active tool's <c>applyMode</c> to <c>Apply</c> for this frame via its protected
        /// setter, so the game's <c>ToolOutputSystem</c> runs the <c>ApplyTool</c> phase and commits
        /// our pending net Temp entities. The tool re-sets its own applyMode next frame, so the flip
        /// is naturally one-shot.
        /// </summary>
        private bool TrySetApplyModeApply()
        {
            global::Game.Tools.ToolBaseSystem active = _toolSystem != null ? _toolSystem.activeTool : null;
            if (active == null) return false;
            if (!_applyModeSetterResolved)
            {
                _applyModeSetterResolved = true;
                System.Reflection.PropertyInfo prop = typeof(global::Game.Tools.ToolBaseSystem).GetProperty(
                    "applyMode", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                _applyModeSetter = prop != null ? prop.GetSetMethod(nonPublic: true) : null;
            }
            if (_applyModeSetter == null) return false;
            _applyModeSetter.Invoke(active, new object[] { global::Game.Tools.ApplyMode.Apply });
            return true;
        }

        /// <summary>
        /// Set the tool's protected <c>m_ForceUpdate</c> flag so it regenerates its preview
        /// definitions on its next update even with a motionless cursor - the def-frame hijack wiped
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
    }
}
