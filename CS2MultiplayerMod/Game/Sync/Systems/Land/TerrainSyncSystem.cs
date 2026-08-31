using System.Collections.Concurrent;
using System.Collections.Generic;
using Game;
using Game.Common;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;

using CS2MultiplayerMod.Game.Sync.Infrastructure;
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.Systems.Net;
namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>Private tag on brush samples we realized, so capture never echoes them back.</summary>
    internal struct RemoteTerrainBrush : IComponentData { }

    /// <summary>
    /// Replicates terraforming as the stream of applied brush samples the game itself produces.
    ///
    /// Capture (ModificationEnd): read every <c>Brush + PrefabRef + Temp + Applied</c> entity that is
    /// not one of ours, and broadcast the terraforming tool prefab, the brush prefab and each
    /// sample's complete applied <see cref="Brush"/> state. Preview and cancelled brushes carry no
    /// <see cref="Applied"/> tag and are ignored. Consecutive samples sharing a tool+brush batch into
    /// one <see cref="TerrainBrushCommand"/>.
    ///
    /// Realize (ToolUpdate, via <see cref="SyncRealizeSystem"/>): recreate each sample as a real
    /// <c>Temp + Brush</c> entity (tagged <see cref="RemoteTerrainBrush"/>) and apply the isolated
    /// brush domain through <c>ApplyBrushesSystem</c>. This runs the height/material/resource change
    /// on the normal path and tags each sample
    /// <c>Applied + Deleted</c>. Independent bounds cap samples-per-frame, decode scan and inbox size;
    /// residual GPU/float drift is trued by the periodic world resync.
    /// </summary>
    public partial class TerrainSyncSystem : GameSystemBase
    {
        /// <summary>
        /// Receiver apply budget: brush samples materialised per frame.
        ///
        /// The budget exists to bound one frame's spike, NOT to pace normal play. A player's own
        /// terraforming produces at most about sixty samples a second, so at sixty frames a second
        /// this only ever bites on a backlog - and a backlog is exactly when spreading the work out
        /// is the wrong thing to do: <see cref="HasBacklog"/> holds back every road, building, zone,
        /// growable and route realize until terrain is level with the sender's, so every extra frame
        /// spent draining is a frame in which nothing else in the session can be applied either.
        ///
        /// The game applies whatever brushes exist in a single <c>ApplyBrushesSystem</c> pass, so a
        /// larger batch is one bigger frame, not more frames. Sixty-four was arbitrary and made a
        /// single terraforming stroke take several frames to land; five hundred and twelve covers
        /// roughly eight seconds of continuous terraforming, which is longer than a stroke anyone
        /// draws in one go, and leaves the ceiling only for the pathological case.
        ///
        /// It also makes the replay slightly more faithful: a height brush is a rate, rescaled per
        /// sample by this machine's frame time, so applying a stroke's samples together under one
        /// frame time reproduces it more exactly than spreading them over frames of varying length.
        /// </summary>
        private const int MaxApplyPerFrame = 512;

        /// <summary>
        /// How long the apply may stay unavailable before the queued samples are given up on.
        ///
        /// Without a bound this was a session-ending wedge rather than a lost stroke: samples that
        /// can never be applied keep <see cref="HasBacklog"/> true forever, and that flag gates
        /// every other realize in the mod. Reaching this means terrain here no longer matches the
        /// sender's, which is a divergence worth repairing rather than one to play on.
        /// </summary>
        private const int MaxCommitFailureFrames = 300;

        // Decoder scan budget: commands pulled off the inbox per frame, so a malformed/unknown-prefab
        // flood cannot create an unbounded main-thread loop.
        private const int MaxDecodePerFrame = 64;

        // Ceiling on decoded-but-not-yet-applied samples, so a burst that outruns the apply budget
        // stays bounded in memory.
        private const int MaxPendingSamples = 4096;

        private readonly ConcurrentQueue<SimulationCommandMessage> _incoming =
            new ConcurrentQueue<SimulationCommandMessage>();

        // Samples resolved and waiting for a safe ApplyTool frame. A partially-applied batch keeps
        // its remaining samples here and continues in order next frame.
        private readonly List<(Entity tool, Entity brush, TerrainBrushCommand.Sample sample)> _pending =
            new List<(Entity, Entity, TerrainBrushCommand.Sample)>();

        private PrefabSystem _prefabSystem;
        private PrefabIndex _prefabIndex;
        private NetSyncSystem _netSync;
        private global::Game.Simulation.TerrainSystem _terrainSystem;
        private EntityQuery _appliedBrushes;
        private CommandObserver _observer;
        private bool _awaitingHeightReadback;
        private bool _commitApplyFailureLogged;

        private long _diagStartMs = -1;
        private int _diagCaptured, _diagRealized;

        // Terrain that never left this machine, or never arrived on it. Every one of these paths
        // used to be a silent `continue`, and a terraform that goes missing is not a small error:
        // one stroke is metres of height, and the roads placed near it afterwards resolve their
        // endpoints against a surface the other player does not have. Counted here and reported at
        // the production level, because this is the thing a session that "keeps de-syncing" needs
        // its log to say out loud.
        private int _dropSendNoToolName, _dropSendNoBrushName, _dropSendOpacity, _dropSendBadFrame;
        private int _dropApplyUnknownPrefab, _dropApplyUnusablePrefab, _dropApplyCreateFailed;
        private int _dropApplyMalformed, _dropApplyUnavailable;
        private int _commitFailureFrames;

        protected override void OnCreate()
        {
            base.OnCreate();

            Mod.log.Info(nameof(TerrainSyncSystem) + " ready.");
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _prefabIndex = new PrefabIndex(_prefabSystem, GetEntityQuery(ComponentType.ReadOnly<PrefabData>()));
            _netSync = World.GetOrCreateSystemManaged<NetSyncSystem>();
            _terrainSystem = World.GetOrCreateSystemManaged<global::Game.Simulation.TerrainSystem>();

            // Applied brush samples the local player just laid down: the ApplyTool pass tags each
            // consumed sample Applied (+Deleted). RemoteTerrainBrush excludes the ones we realized.
            _appliedBrushes = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<Brush, PrefabRef, Temp, Applied>(),
                None = SyncQuery.ReadOnly<RemoteTerrainBrush>(),
            });

            _observer = SyncObserverBinding.Bind(
                () => new CommandObserver(_incoming, TerrainBrushCommand.Id)
                    {
                        MaxBodyBytes = TerrainBrushCommand.MaxEncodedBytes,
                    },
                DrainQueue);
        }

        protected override void OnDestroy()
        {
            SyncObserverBinding.Unbind(_observer, DrainQueue);
            base.OnDestroy();
        }

        /// <summary>World reload purges queued and half-applied strokes (see <see cref="SyncInbox"/>).</summary>
        private void DrainQueue()
        {
            SyncInbox.Clear(_incoming);
            _pending.Clear();
            _awaitingHeightReadback = false;
            _commitApplyFailureLogged = false;
            _commitFailureFrames = 0;
        }

        protected override void OnUpdate()
        {
            using (Diagnostics.SyncProfiler.Measure("TerrainSync"))
            {
                MultiplayerService service = Mod.Service;
                if (service == null) return;

                MultiplayerSession session = service.Session;
                if (!service.GameplaySyncReady) return;

                CaptureBrushes(session);
                FlushDiagnostics(service.NowMs);
            }
        }

        /// <summary>
        /// True while remote terrain work is still queued. Read once per frame by
        /// <see cref="SyncRealizeSystem"/> to hold new net/object realizes until the surface matches
        /// the sender's. Own-origin echoes may defer one frame until they are discarded; treating any
        /// queued command as backlog prevents a remote edit hidden behind that echo from being missed.
        /// </summary>
        public bool HasBacklog()
        {
            if (_awaitingHeightReadback) return true;
            if (_pending.Count > 0) return true;
            return !_incoming.IsEmpty;
        }

        /// <summary>
        /// Complete the asynchronous heightmap readback from the previous remote brush pass before
        /// dependent roads and objects are allowed to sample terrain. Queue drain alone is not a
        /// terrain-consistency barrier: the CPU height array can still contain the pre-edit surface.
        /// </summary>
        public void CompletePendingHeightReadback()
        {
            if (!_awaitingHeightReadback || _terrainSystem == null) return;
            try
            {
                // A batch contains many ApplyBrush calls. If the first call already started a GPU
                // request, later samples mark that request out-of-date; completing it immediately
                // schedules one consolidated follow-up request. The second call waits that follow-up
                // (or is a cheap no-op when there was only one request).
                _terrainSystem.GetHeightData(waitForPending: true);
                _terrainSystem.GetHeightData(waitForPending: true);
                _awaitingHeightReadback = false;
                Diagnostics.FlightRecorder.Note("terrain height readback complete");
            }
            catch (System.Exception ex)
            {
                // Do not wedge all subsequent construction forever if a future game build changes
                // the readback contract. The next authoritative world sync remains the repair path.
                _awaitingHeightReadback = false;
                Mod.log.Warn("[MP] TerrainSync: height readback barrier failed: " + ex.Message);
            }
        }

        /// <summary>Called by <see cref="SyncRealizeSystem"/> during ToolUpdate (see there for why).</summary>
        public void RealizePending()
        {
            MultiplayerService service = Mod.Service;
            if (service == null) return;

            MultiplayerSession session = service.Session;
            if (!service.GameplaySyncReady) return;

            // Refill the pending list from the inbox (bounded scan), then apply from it.
            int scanned = 0;
            SimulationCommandMessage message;
            // Leave room for a maximum-size command before dequeueing it. This preserves every
            // sample in that command instead of accepting a prefix and silently dropping its tail.
            while (_pending.Count <= MaxPendingSamples - TerrainBrushCommand.MaxSamples &&
                   scanned < MaxDecodePerFrame
                   && _incoming.TryDequeue(out message))
            {
                scanned++;
                if (message.OriginPlayerId == session.LocalPlayerId) continue;

                TerrainBrushCommand command;
                try { command = TerrainBrushCommand.Decode(message.Body); }
                catch (System.Exception ex)
                {
                    _dropApplyMalformed++;
                    Mod.log.Warn("[MP] TerrainSync: dropping malformed command: " + ex.Message);
                    continue;
                }

                Entity tool, brush;
                if (!_prefabIndex.TryResolve(command.ToolPrefabName, out tool) ||
                    !_prefabIndex.TryResolve(command.BrushPrefabName, out brush))
                {
                    _dropApplyUnknownPrefab += command.Samples.Length;
                    continue;
                }
                // ApplyBrushesSystem dereferences TerraformingData[tool] and BrushData/BrushCell[brush]
                // with no existence check — a wrong prefab type there is a native crash, not an
                // exception. Only queue a sample whose prefabs carry them.
                if (!EntityManager.HasComponent<TerraformingData>(tool) ||
                    !EntityManager.HasComponent<BrushData>(brush) ||
                    !EntityManager.HasBuffer<BrushCell>(brush))
                {
                    _dropApplyUnusablePrefab += command.Samples.Length;
                    continue;
                }

                for (int i = 0; i < command.Samples.Length; i++)
                    _pending.Add((tool, brush, command.Samples[i]));
            }

            if (_pending.Count == 0) return;

            // A local Apply frame gets priority because its later ApplyTool pass would consume our
            // brushes twice. None/Clear are safe: local brush previews are isolated below and a
            // ClearTool pass only performs Temp cleanup.
            if (_netSync == null || !_netSync.CanApplyAuxiliaryTemps) return;

            _netSync.PrepareAuxiliaryTemps();

            // Take the whole arrived stroke in one pass. ApplyBrushesSystem consumes every Temp
            // brush that exists in a single Update, so a bigger batch is one bigger frame rather
            // than more frames - and every extra frame here is a frame in which no road, building,
            // zone or route may be applied either (see HasBacklog).
            int candidateCount = System.Math.Min(MaxApplyPerFrame, _pending.Count);
            var created = new List<Entity>(candidateCount);
            int consumed = 0;
            bool changesHeight = false;
            for (int i = 0; i < candidateCount; i++)
            {
                var item = _pending[i];
                consumed++;
                try
                {
                    bool sampleChangesHeight;
                    created.Add(CreateRemoteBrush(item.tool, item.brush, item.sample,
                        out sampleChangesHeight));
                    changesHeight |= sampleChangesHeight;
                }
                catch (System.Exception ex)
                {
                    // Skip the one sample that threw rather than abandoning the batch. Rejecting
                    // the whole batch and leaving it queued meant a single bad sample kept
                    // HasBacklog true forever, and that flag holds back every other realize in the
                    // mod - a lost stroke turned into a session that could apply nothing at all.
                    _dropApplyCreateFailed++;
                    Mod.log.Warn("[MP] TerrainSync: dropping a brush sample that could not be " +
                                 "created: " + ex.Message);
                }
            }

            if (created.Count == 0)
            {
                // Nothing to commit, but these samples are spent - queueing them again would retry
                // the same failure forever. The brush isolation taken above is released by
                // FinishIsolationAfterToolOutput at the end of this frame either way.
                if (consumed > 0) _pending.RemoveRange(0, consumed);
                return;
            }

            bool committed = false;
            string commitError = null;
            try { committed = _netSync.CommitAuxiliaryTempsNow(); }
            catch (System.Exception ex)
            {
                commitError = ex.Message;
            }

            if (!committed)
            {
                DestroyUncommittedBrushes(created);
                if (!_commitApplyFailureLogged)
                {
                    _commitApplyFailureLogged = true;
                    Mod.log.Warn("[MP] TerrainSync: brush apply unavailable; remote samples remain queued" +
                                 (string.IsNullOrEmpty(commitError) ? "." : ": " + commitError));
                }
                // Bounded. An apply that never becomes available is not something to wait out: the
                // queue it blocks is the one every other sync system waits behind.
                if (++_commitFailureFrames >= MaxCommitFailureFrames) GiveUpOnQueuedTerrain(commitError);
                return;
            }

            // The apply pass consumes every Temp brush that exists when it runs; the samples are
            // spent whether or not their Applied tag is visible yet (it is stamped through the tool
            // barrier and only becomes observable at ModificationEnd, which is why the capture
            // query reads it there). This is the same contract the smaller batch relied on.
            _commitApplyFailureLogged = false;
            _commitFailureFrames = 0;
            _pending.RemoveRange(0, consumed);
            if (changesHeight) _awaitingHeightReadback = true;
            _diagRealized += created.Count;
            Diagnostics.FlightRecorder.Note("terrain realize n=" + created.Count +
                (_pending.Count > 0 ? " held=" + _pending.Count : ""));
        }

        /// <summary>
        /// Abandon terrain this machine cannot apply. The samples are dropped so the backlog flag
        /// clears and the rest of the mod can run again, and the divergence they leave behind is
        /// put to the resync arbiter as what it is: ground that is now a different shape here.
        /// </summary>
        private void GiveUpOnQueuedTerrain(string commitError)
        {
            int abandoned = _pending.Count;
            _pending.Clear();
            _commitFailureFrames = 0;
            _commitApplyFailureLogged = false;
            _dropApplyUnavailable += abandoned;

            Diagnostics.SyncLog.ProdWarn(
                "Terrain sync: gave up on " + abandoned + " queued terraforming sample(s) after " +
                MaxCommitFailureFrames + " frames without a usable apply pass" +
                (string.IsNullOrEmpty(commitError) ? "." : " (" + commitError + ").") +
                " The ground here no longer matches the other player's.");
            SyncInbox.RequestResync(Diagnostics.ResyncReport
                .Create("queued terraforming could not be applied", "terrain",
                    Diagnostics.ResyncEvidence.Contradiction)
                .About("terrain apply pass")
                .Tried("held the samples for " + MaxCommitFailureFrames +
                       " frames waiting for a frame the game would apply brushes on")
                .Fact("samples abandoned", abandoned)
                .Fact("why the apply was refused", commitError ?? "the brush apply system was unavailable"));
        }

        private Entity CreateRemoteBrush(Entity tool, Entity brushPrefab, TerrainBrushCommand.Sample s,
            out bool changesHeight)
        {
            TerraformingData toolData = EntityManager.GetComponentData<TerraformingData>(tool);
            changesHeight = toolData.m_Target == TerraformingTarget.Height;
            float adjustedStrength = s.Strength;
            if (changesHeight)
            {
                float receiverDelta = UnityEngine.Time.unscaledDeltaTime;
                if (receiverDelta <= 0f || float.IsNaN(receiverDelta) || float.IsInfinity(receiverDelta))
                    receiverDelta = 0.0001f;
                adjustedStrength = s.Strength * s.DeltaTime / receiverDelta;
            }
            Entity brush = EntityManager.CreateEntity();
            try
            {
                EntityManager.AddComponentData(brush, new Brush
                {
                    m_Tool = tool,
                    m_Position = new float3(s.PosX, s.PosY, s.PosZ),
                    m_Target = new float3(s.TargetX, s.TargetY, s.TargetZ),
                    m_Start = new float3(s.StartX, s.StartY, s.StartZ),
                    m_Size = s.Size,
                    m_Angle = s.Angle,
                    m_Strength = adjustedStrength,
                    m_Opacity = s.Opacity,
                });
                EntityManager.AddComponentData(brush, new PrefabRef { m_Prefab = brushPrefab });
                // A real applied brush is Temp + Brush; Essential|Create is the non-delete recipe
                // GenerateBrushesSystem stamps. ApplyBrushesSystem consumes it and adds Applied+Deleted.
                EntityManager.AddComponentData(brush, new Temp
                {
                    m_Original = Entity.Null,
                    m_Flags = TempFlags.Essential | TempFlags.Create,
                });
                EntityManager.AddComponent<RemoteTerrainBrush>(brush);
                return brush;
            }
            catch
            {
                if (EntityManager.Exists(brush)) EntityManager.DestroyEntity(brush);
                throw;
            }
        }

        private void DestroyUncommittedBrushes(List<Entity> brushes)
        {
            for (int i = 0; i < brushes.Count; i++)
                if (EntityManager.Exists(brushes[i])) EntityManager.DestroyEntity(brushes[i]);
        }

        private void CaptureBrushes(MultiplayerSession session)
        {
            if (_appliedBrushes.IsEmptyIgnoreFilter) return;

            NativeArray<Entity> entities = _appliedBrushes.ToEntityArray(Allocator.Temp);
            try
            {
                // Batch consecutive samples that share a tool+brush into one command (a fast small
                // brush drag applies many samples per frame).
                var batches = new Dictionary<(string tool, string brush), List<TerrainBrushCommand.Sample>>();
                // A hitch, a resumed frame or a first frame gives a delta that says nothing about
                // how long this stroke was applied for. Returning here dropped every sample in the
                // frame - the ground moved locally and the other player was never told, which is a
                // permanent divergence bought to avoid one mis-scaled sample. Fall back to a normal
                // frame instead: the height rate is then slightly off and the periodic sync trues
                // it, where a dropped stroke is never trued at all.
                const float FallbackFrameSeconds = 1f / 60f;
                float sourceDelta = UnityEngine.Time.unscaledDeltaTime;
                if (sourceDelta <= 0f || sourceDelta > 10f ||
                    float.IsNaN(sourceDelta) || float.IsInfinity(sourceDelta))
                {
                    _dropSendBadFrame += entities.Length;
                    sourceDelta = FallbackFrameSeconds;
                }
                for (int i = 0; i < entities.Length; i++)
                {
                    Brush brush = EntityManager.GetComponentData<Brush>(entities[i]);

                    string toolName = _prefabSystem.GetPrefabName(brush.m_Tool);
                    if (string.IsNullOrEmpty(toolName)) { _dropSendNoToolName++; continue; }
                    string brushName = _prefabSystem.GetPrefabName(
                        EntityManager.GetComponentData<PrefabRef>(entities[i]).m_Prefab);
                    if (string.IsNullOrEmpty(brushName)) { _dropSendNoBrushName++; continue; }

                    // A cancelled/preview brush carries opacity outside (0,1] or no real edit — the
                    // wire guard would reject it anyway; skip so a batch never fails to encode.
                    // Counted rather than skipped silently: this query only sees APPLIED brushes, so
                    // anything refused here did change the ground and did not travel.
                    if (brush.m_Opacity <= 0f || brush.m_Opacity > 1f) { _dropSendOpacity++; continue; }

                    var key = (toolName, brushName);
                    List<TerrainBrushCommand.Sample> list;
                    if (!batches.TryGetValue(key, out list))
                    {
                        list = new List<TerrainBrushCommand.Sample>();
                        batches[key] = list;
                    }
                    list.Add(new TerrainBrushCommand.Sample
                    {
                        PosX = brush.m_Position.x, PosY = brush.m_Position.y, PosZ = brush.m_Position.z,
                        TargetX = brush.m_Target.x, TargetY = brush.m_Target.y, TargetZ = brush.m_Target.z,
                        StartX = brush.m_Start.x, StartY = brush.m_Start.y, StartZ = brush.m_Start.z,
                        Size = brush.m_Size,
                        Angle = brush.m_Angle,
                        Strength = brush.m_Strength,
                        Opacity = brush.m_Opacity,
                        DeltaTime = sourceDelta,
                    });
                }

                foreach (var batch in batches)
                    SendBatch(session, batch.Key.tool, batch.Key.brush, batch.Value);
            }
            finally
            {
                entities.Dispose();
            }
        }

        private void SendBatch(MultiplayerSession session, string tool, string brush,
            List<TerrainBrushCommand.Sample> samples)
        {
            // Split a batch bigger than the per-command sample cap across several commands.
            for (int offset = 0; offset < samples.Count; offset += TerrainBrushCommand.MaxSamples)
            {
                int count = System.Math.Min(TerrainBrushCommand.MaxSamples, samples.Count - offset);
                var chunk = new TerrainBrushCommand.Sample[count];
                samples.CopyTo(offset, chunk, 0, count);
                var command = new TerrainBrushCommand
                {
                    ToolPrefabName = tool,
                    BrushPrefabName = brush,
                    Samples = chunk,
                };
                session.SendCommand(0, TerrainBrushCommand.Id, command.Encode());
                _diagCaptured += count;
            }
        }

        private void FlushDiagnostics(long now)
        {
            if (_diagStartMs < 0) { _diagStartMs = now; return; }
            if (now - _diagStartMs < 5000) return;
            if (_diagCaptured > 0 || _diagRealized > 0)
                Mod.Verbose("[MP] TerrainSync/5s: captured " + _diagCaptured + " sample(s), realized " + _diagRealized + ".");
            ReportDroppedTerrain();
            _diagCaptured = _diagRealized = 0;
            _diagStartMs = now;
        }

        /// <summary>
        /// Say out loud what terrain went missing, and on which side.
        ///
        /// Each of these was a bare <c>continue</c>. A terraforming stroke is metres of height, so
        /// one dropped sample is not a rounding error - it is ground that is a different shape on
        /// the two machines, and the roads drawn near it afterwards resolve their endpoints against
        /// a surface the sender does not have. The realize pass already reports elevation
        /// corrections of over three metres; this is where such a figure comes from.
        /// </summary>
        private void ReportDroppedTerrain()
        {
            int notSent = _dropSendNoToolName + _dropSendNoBrushName + _dropSendOpacity;
            int notApplied = _dropApplyUnknownPrefab + _dropApplyUnusablePrefab +
                             _dropApplyCreateFailed + _dropApplyMalformed;

            if (notSent > 0)
                Diagnostics.SyncLog.ProdWarn(
                    "Terrain sync: " + notSent + " terraforming sample(s) changed the ground here " +
                    "but could not be sent (" + _dropSendNoToolName + " with no tool name, " +
                    _dropSendNoBrushName + " with no brush name, " + _dropSendOpacity +
                    " outside the encodable range). The other player's ground is now different here.");

            if (notApplied > 0)
                Diagnostics.SyncLog.ProdWarn(
                    "Terrain sync: " + notApplied + " terraforming sample(s) arrived but could not " +
                    "be applied (" + _dropApplyUnknownPrefab + " naming a tool or brush this game " +
                    "does not have, " + _dropApplyUnusablePrefab + " naming one that cannot " +
                    "terraform, " + _dropApplyCreateFailed + " that failed to build, " +
                    _dropApplyMalformed + " malformed). The ground here is now different from the " +
                    "other player's.");

            if (_dropSendBadFrame > 0)
                Diagnostics.SyncLog.ProdWarn(
                    "Terrain sync: " + _dropSendBadFrame + " terraforming sample(s) were sent with " +
                    "a substitute frame time because this machine reported an implausible one. " +
                    "Their height change may be slightly off on the other player's map.");

            if (notSent > 0 || notApplied > 0 || _dropSendBadFrame > 0 || _dropApplyUnavailable > 0)
                Diagnostics.FlightRecorder.Note(
                    "terrain dropped sendNoTool=" + _dropSendNoToolName +
                    " sendNoBrush=" + _dropSendNoBrushName +
                    " sendOpacity=" + _dropSendOpacity +
                    " sendBadFrame=" + _dropSendBadFrame +
                    " applyUnknownPrefab=" + _dropApplyUnknownPrefab +
                    " applyUnusablePrefab=" + _dropApplyUnusablePrefab +
                    " applyCreateFailed=" + _dropApplyCreateFailed +
                    " applyMalformed=" + _dropApplyMalformed +
                    " applyUnavailable=" + _dropApplyUnavailable);

            _dropSendNoToolName = _dropSendNoBrushName = _dropSendOpacity = _dropSendBadFrame = 0;
            _dropApplyUnknownPrefab = _dropApplyUnusablePrefab = _dropApplyCreateFailed = 0;
            _dropApplyMalformed = _dropApplyUnavailable = 0;
        }
    }
}
