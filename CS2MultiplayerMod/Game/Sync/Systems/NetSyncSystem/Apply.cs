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
        /// <summary>How long an armed batch may wait for its commit before it is discarded and replayed.</summary>
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
        }

        /// <summary>
        /// True on frames with an armed, not-yet-flipped commit - the frames where
        /// <see cref="DefinitionGateSystem"/> must destroy the tool's freshly buffered definitions
        /// (they would otherwise materialise as preview Temps inside the commit window and the flip
        /// would place the player's un-applied gesture). The flip branch clears the flag mid-phase,
        /// before the gate runs, so the commit frame itself is naturally excluded and the preview
        /// returns the very frame the batch commits.
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

                if (count > 0 && ArmedBatchReferencesVanishedOriginal())
                {
                    // The world changed under the armed batch: the game's own aftermath work (node
                    // reduction merging freshly committed spans) can delete an edge this batch
                    // resolved as a split target or reuse node one frame ago. Committing would make
                    // ApplyNetSystem dereference the corpse — the native CTD. Discard the Temps and
                    // rebuild from the source commands against the changed world instead.
                    _pendingApply = false;
                    _awaitingDrain = false;
                    DiscardStaleNetTemps("a referenced original vanished between arm and commit");
                    System.Action rebuild = _onCommitLost;
                    _onCommitLost = null;
                    if (rebuild != null && _expiryReplays < 3)
                    {
                        _expiryReplays++;
                        Diagnostics.FlightRecorder.Note("net batch invalidated pre-commit; replay "
                            + _expiryReplays + "/3");
                        rebuild();
                    }
                    else
                    {
                        Mod.log.Warn("[MP] NetApply: armed batch referenced a vanished original and " +
                                     "had no replay budget left - batch dropped.");
                        Diagnostics.FlightRecorder.Note("net batch invalidated pre-commit; dropped");
                    }
                }
                else if (count > 0 && !toolIdle && toolMode == global::Game.Tools.ApplyMode.Apply)
                {
                    MultiplayerService currentService = Mod.Service;
                    RecordPlacementOriginals(currentService != null ? currentService.NowMs : 0);
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
                    Diagnostics.FlightRecorder.Note("net commit ride-along (temps=" + count + ")");
                    // The click itself applies NOTHING of the player's: the gate destroyed the very
                    // definitions that were to become its committing Temps one frame ago. Rebuild the
                    // gesture from the gate's stash as ordinary sync commands (see .Replay).
                    ReplaySwallowedClick();
                }
                else if (count > 0 && TrySetApplyModeApply())
                {
                    MultiplayerService currentService = Mod.Service;
                    RecordPlacementOriginals(currentService != null ? currentService.NowMs : 0);
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
                    Diagnostics.FlightRecorder.Note("net commit flip (temps=" + count + ")");
                }
                else if (System.Environment.TickCount - _armTick > ApplyWindowMs)
                {
                    // The window ran out. Either the definitions never materialised (count == 0,
                    // rejected?) or they did but the commit could never be driven (no active tool,
                    // applyMode setter gone). Both strand the batch: anything still standing is an
                    // uncommitted course that must not join a later pass (see DiscardStaleNetTemps).
                    // Replay a few times, then stop - a batch the game always rejects must not
                    // rebuild forever.
                    _pendingApply = false;
                    _awaitingDrain = false;
                    if (count > 0) DiscardStaleNetTemps("apply window expired with the batch uncommitted");

                    System.Action replay = _onCommitLost;
                    _onCommitLost = null;
                    if (replay != null && _expiryReplays < 3)
                    {
                        _expiryReplays++;
                        Mod.log.Warn("[MP] NetApply: apply window expired (temps=" + count +
                                     ") - re-queueing batch (attempt " + _expiryReplays + "/3).");
                        Diagnostics.FlightRecorder.Note("net apply window expired temps=" + count +
                            "; replay " + _expiryReplays + "/3");
                        replay();
                    }
                    else
                    {
                        Mod.log.Warn("[MP] NetApply: apply window expired (temps=" + count + ") - batch dropped" +
                                     (replay != null ? " after " + _expiryReplays + " replays." : "."));
                        Diagnostics.FlightRecorder.Note("net apply window expired temps=" + count + "; batch dropped");
                    }
                }
                else
                {
                    // Still waiting: Temps not materialised yet (or a click frame with none of ours
                    // present). While a build tool is out, keep hijacking each waiting frame so the
                    // tool's fresh definitions can't materialise into the pending window and pollute
                    // the eventual commit - EXCEPT on the frame the player's own click applies:
                    // wiping then swallows the click a second way (the preview it was to commit is
                    // destroyed under it). Replay the stashed gesture instead and leave the frame be.
                    if (count == 0 && !toolIdle)
                    {
                        if (toolMode == global::Game.Tools.ApplyMode.Apply) ReplaySwallowedClick();
                        else PrepareDefinitionFrame();
                    }
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
                    // The remaining Temps are the tool's regenerated preview, not our batch - and the
                    // next batch's PrepareDefinitionFrame wipes them anyway (a tool is out).
                    _awaitingDrain = false;
                }
                else if (System.Environment.TickCount - _drainArmTick > DrainWindowMs)
                {
                    // Committed Temps that never drained, with the idle tool active: nothing else
                    // will ever clear them (the hijack wipe no-ops while idle), so they would ride
                    // into the next batch's ApplyTool pass and can crash the game natively.
                    DiscardStaleNetTemps("commit never drained");
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
        /// True until queued placement courses and their commit/drain have become queryable network
        /// geometry. Systems that attach to or edit roads must not overtake this boundary.
        /// </summary>
        public bool HasPlacementBacklog => !_incoming.IsEmpty || _remoteDeferred.Count > 0 ||
                                           _localReplays.Count > 0 || IsCommitBusy;

        /// <summary>
        /// True when a feeder may create Temp-backed work. Remote commits are deliberately queued
        /// while any interactive tool is active: ApplyTool and ClearTool are global Temp phases, so
        /// no amount of curve reconstruction can make a concurrent preview transaction-safe. The
        /// default tool provides an unambiguous window without destroying or replaying local input.
        /// </summary>
        public bool CanBuildDefinitions
        {
            get
            {
                if (_pendingApply || _awaitingDrain) return false;
                global::Game.Tools.ToolBaseSystem tool = _toolSystem != null ? _toolSystem.activeTool : null;
                return tool == null || tool is global::Game.Tools.DefaultToolSystem;
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

            int temps = ClearTempEntities(_allTempEntities);

            TryForceToolUpdate(tool);
            if (defs > 0 || temps > 0)
                Diagnostics.FlightRecorder.Note("hijack wipe defs=" + defs + " temps=" + temps +
                    " tool=" + tool.GetType().Name);
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
                    cleared++;
                }
            }
            finally
            {
                tempEntities.Dispose();
            }
            return cleared;
        }

        /// <summary>
        /// Tear down net Temps left standing by a batch that armed but never committed.
        ///
        /// An uncommitted Temp course must never survive into a LATER ApplyTool pass. Two courses
        /// that each touch an existing edge, committed in one pass, make the game's net apply step
        /// dereference an edge the first split already replaced - a hard process crash. A stale
        /// course is worse still: it points at a split target that may have been bulldozed since.
        /// Neither the game's clear pass nor <see cref="PrepareDefinitionFrame"/> removes them while
        /// the idle default tool is active, so the timeout paths clear them explicitly.
        /// </summary>
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
        /// Arm the ApplyTool commit for net definitions a sibling system (delete/replace) created this
        /// frame. They become Temp net entities at the following Modification; the commit flow (part A
        /// of <see cref="RealizePending"/>) flips applyMode next frame and ApplyNetSystem commits
        /// them natively. Only call when <see cref="CanBuildDefinitions"/> is true (and after
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
        /// Commit auxiliary Temps a sibling created THIS frame (terrain brush samples, which are
        /// real <c>Temp + Brush</c> entities the moment they are created, not definitions awaiting a
        /// Modification pass) by flipping the active tool's applyMode now - so this frame's
        /// <c>ToolOutputSystem</c> runs the ApplyTool pass and <c>ApplyBrushesSystem</c> applies
        /// them. Only valid right after the caller created its Temps following
        /// <see cref="PrepareDefinitionFrame"/>, and only when <see cref="CanBuildDefinitions"/> is
        /// true (no net batch armed/draining, not the player's own apply frame). Returns whether the
        /// flip was driven.
        /// </summary>
        public bool CommitAuxiliaryTempsNow() => TrySetApplyModeApply();
    }
}
