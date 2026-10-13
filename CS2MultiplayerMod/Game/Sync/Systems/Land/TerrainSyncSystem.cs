using System.Collections.Generic;
using Game.Common;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Infrastructure;
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.Systems.Net;
namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>Private tag on brush samples we realized, so capture never echoes them back.</summary>
    internal struct RemoteTerrainBrush : IComponentData { }

    /// <summary>
    /// Replicates terraforming as the applied brush samples the game produces. Capture reads applied
    /// <c>Brush</c> entities at ModificationEnd; realize recreates each sample as a
    /// <see cref="RemoteTerrainBrush"/> Temp brush and applies it through <c>ApplyBrushesSystem</c>.
    /// </summary>
    public partial class TerrainSyncSystem : CommandSyncSystem, IRealizeStage
    {
        /// <summary>
        /// Samples applied per frame. Bounds a spike, not normal play: a backlog holds every other realize
        /// (see <see cref="HasBacklog"/>), and the game applies all brushes in one pass anyway.
        /// </summary>
        private const int MaxApplyPerFrame = 512;

        /// <summary>
        /// Frames the apply may stay unavailable before queued samples are abandoned; otherwise
        /// <see cref="HasBacklog"/> would gate every other realize forever.
        /// </summary>
        private const int MaxCommitFailureFrames = 300;

        // Inbox commands decoded per frame.
        private const int MaxDecodePerFrame = 64;

        private const int MaxPendingSamples = 4096;

        // Resolved samples waiting for a safe ApplyTool frame, kept in order across frames.
        private readonly List<(Entity tool, Entity brush, TerrainBrushCommand.Sample sample)> _pending =
            new List<(Entity, Entity, TerrainBrushCommand.Sample)>();

        private PrefabSystem _prefabSystem;
        private PrefabIndex _prefabIndex;
        private NetSyncSystem _netSync;
        private global::Game.Simulation.TerrainSystem _terrainSystem;
        private EntityQuery _appliedBrushes;
        private bool _awaitingHeightReadback;
        private bool _commitApplyFailureLogged;

        private long _diagStartMs = -1;
        private int _diagCaptured, _diagRealized;

        // Terrain that was not sent or not applied; reported ungated (see ReportDroppedTerrain).
        private int _dropSendNoToolName, _dropSendNoBrushName, _dropSendOpacity, _dropSendBadFrame;
        private int _dropApplyUnknownPrefab, _dropApplyUnusablePrefab, _dropApplyCreateFailed;
        private int _dropApplyMalformed, _dropApplyUnavailable;
        private int _commitFailureFrames;

        protected override void OnCreate()
        {
            base.OnCreate();

            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _prefabIndex = new PrefabIndex(_prefabSystem, GetEntityQuery(ComponentType.ReadOnly<PrefabData>()));
            _netSync = World.GetOrCreateSystemManaged<NetSyncSystem>();
            _terrainSystem = World.GetOrCreateSystemManaged<global::Game.Simulation.TerrainSystem>();

            // Local applied samples; RemoteTerrainBrush excludes the ones we realized.
            _appliedBrushes = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<Brush, PrefabRef, Temp, Applied>(),
                None = SyncQuery.ReadOnly<RemoteTerrainBrush>(),
            });

            ListenFor(new[] { TerrainBrushCommand.Id }, TerrainBrushCommand.MaxEncodedBytes);
        }

        /// <summary>World reload purges queued and half-applied strokes (see <see cref="SyncInbox"/>).</summary>
        protected override void DrainQueue()
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
        /// True while remote terrain is still queued, including own-origin echoes that may hide a
        /// remote edit behind them.
        /// </summary>
        public bool HasBacklog()
        {
            if (_awaitingHeightReadback) return true;
            if (_pending.Count > 0) return true;
            return !_incoming.IsEmpty;
        }

        /// <summary>
        /// Completes the previous pass's height readback: an empty queue does not mean the CPU height
        /// array has the edited surface yet.
        /// </summary>
        public void CompletePendingHeightReadback()
        {
            if (!_awaitingHeightReadback || _terrainSystem == null) return;
            try
            {
                // Later samples mark the first GPU request out of date; the second call waits the follow-up.
                _terrainSystem.GetHeightData(waitForPending: true);
                _terrainSystem.GetHeightData(waitForPending: true);
                _awaitingHeightReadback = false;
                SyncLog.Trace(LogTopic.Land, "terrain height readback complete");
            }
            catch (System.Exception ex)
            {
                // Never wedge construction on a readback failure; a world sync repairs it.
                _awaitingHeightReadback = false;
                SyncLog.Warn(LogTopic.Land, "TerrainSync: height readback barrier failed: " +
                    ex.Message);
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
            // Leave room for a whole command so none is truncated.
            while (_pending.Count <= MaxPendingSamples - TerrainBrushCommand.MaxSamples &&
                   scanned < MaxDecodePerFrame
                   && _incoming.TryDequeue(out SimulationCommandMessage message))
            {
                scanned++;
                if (message.OriginPlayerId == session.LocalPlayerId) continue;

                if (!CommandDecode.TryDecode(message, TerrainBrushCommand.Decode, LogTopic.Land,
                        "TerrainSync", out TerrainBrushCommand command))
                {
                    _dropApplyMalformed++;
                    continue;
                }

                if (!_prefabIndex.TryResolve(command.ToolPrefabName, out Entity tool) ||
                    !_prefabIndex.TryResolve(command.BrushPrefabName, out Entity brush))
                {
                    _dropApplyUnknownPrefab += command.Samples.Length;
                    continue;
                }
                // ApplyBrushesSystem reads TerraformingData/BrushData/BrushCell unchecked: a wrong prefab is a
                // native crash.
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

            // A local Apply frame would consume our brushes twice; None/Clear are safe.
            if (_netSync == null || !_netSync.CanApplyAuxiliaryTemps) return;

            _netSync.PrepareAuxiliaryTemps();

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
                    created.Add(CreateRemoteBrush(item.tool, item.brush, item.sample,
                        out bool sampleChangesHeight));
                    changesHeight |= sampleChangesHeight;
                }
                catch (System.Exception ex)
                {
                    // Skip only the failing sample; keeping it queued would hold the backlog forever.
                    _dropApplyCreateFailed++;
                    SyncLog.Warn(LogTopic.Land,
                        "TerrainSync: dropping a brush sample that could not be " + "created: " +
                        ex.Message);
                }
            }

            if (created.Count == 0)
            {
                // Spent either way; isolation is released at the end of the frame.
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
                    SyncLog.Warn(LogTopic.Land,
                        "TerrainSync: brush apply unavailable; remote samples remain queued" +
                        (string.IsNullOrEmpty(commitError) ? "." : ": " + commitError));
                }
                if (++_commitFailureFrames >= MaxCommitFailureFrames) GiveUpOnQueuedTerrain(commitError);
                return;
            }

            // The apply pass consumes every Temp brush; Applied only becomes visible at ModificationEnd.
            _commitApplyFailureLogged = false;
            _commitFailureFrames = 0;
            _pending.RemoveRange(0, consumed);
            if (changesHeight) _awaitingHeightReadback = true;
            _diagRealized += created.Count;
            SyncLog.Trace(LogTopic.Land, "terrain realize n=" + created.Count +
                (_pending.Count > 0 ? " held=" + _pending.Count : ""));
        }

        /// <summary>
        /// Drops terrain this machine cannot apply so the backlog clears, and reports the divergence.
        /// </summary>
        private void GiveUpOnQueuedTerrain(string commitError)
        {
            int abandoned = _pending.Count;
            _pending.Clear();
            _commitFailureFrames = 0;
            _commitApplyFailureLogged = false;
            _dropApplyUnavailable += abandoned;

            Diagnostics.SyncLog.Warn(LogTopic.Land, "Terrain sync: gave up on " + abandoned +
                " queued terraforming sample(s) after " + MaxCommitFailureFrames +
                " frames without a usable apply pass" +
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
                adjustedStrength = TerrainBrushCommand.ReceiverStrength(s.Strength, s.DeltaTime,
                    UnityEngine.Time.unscaledDeltaTime, toolData.m_Type == TerraformingType.Soften);
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
                // The recipe GenerateBrushesSystem stamps; ApplyBrushesSystem adds Applied+Deleted.
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
                var batches = new Dictionary<(string tool, string brush), List<TerrainBrushCommand.Sample>>();
                // A hitch or first frame gives a meaningless delta; use a normal frame rather than drop the stroke.
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

                    // Object and vegetation brushes share the Brush component but do not terraform.
                    if (brush.m_Tool == Entity.Null || !EntityManager.Exists(brush.m_Tool) ||
                        !EntityManager.HasComponent<TerraformingData>(brush.m_Tool)) continue;

                    string toolName = _prefabSystem.GetPrefabName(brush.m_Tool);
                    if (string.IsNullOrEmpty(toolName)) { _dropSendNoToolName++; continue; }
                    string brushName = _prefabSystem.GetPrefabName(
                        EntityManager.GetComponentData<PrefabRef>(entities[i]).m_Prefab);
                    if (string.IsNullOrEmpty(brushName)) { _dropSendNoBrushName++; continue; }

                    // Out-of-range opacity would fail the wire guard. Counted: an applied brush did change the ground.
                    if (brush.m_Opacity <= 0f || brush.m_Opacity > 1f) { _dropSendOpacity++; continue; }

                    var key = (toolName, brushName);
                    if (!batches.TryGetValue(key, out List<TerrainBrushCommand.Sample> list))
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
                SyncLog.Detail(LogTopic.Land, "TerrainSync/5s: captured " + _diagCaptured +
                    " sample(s), realized " + _diagRealized + ".");
            ReportDroppedTerrain();
            _diagCaptured = _diagRealized = 0;
            _diagStartMs = now;
        }

        /// <summary>Reports terrain that was not sent or not applied: ground now differs between peers.</summary>
        private void ReportDroppedTerrain()
        {
            int notSent = _dropSendNoToolName + _dropSendNoBrushName + _dropSendOpacity;
            int notApplied = _dropApplyUnknownPrefab + _dropApplyUnusablePrefab +
                             _dropApplyCreateFailed + _dropApplyMalformed;

            if (notSent > 0)
                Diagnostics.SyncLog.Warn(LogTopic.Land, "Terrain sync: " + notSent +
                    " terraforming sample(s) changed the ground here " + "but could not be sent (" +
                    _dropSendNoToolName + " with no tool name, " + _dropSendNoBrushName +
                    " with no brush name, " + _dropSendOpacity +
                    " outside the encodable range). The other player's ground is now different here.");

            if (notApplied > 0)
                Diagnostics.SyncLog.Warn(LogTopic.Land, "Terrain sync: " + notApplied +
                    " terraforming sample(s) arrived but could not " + "be applied (" +
                    _dropApplyUnknownPrefab + " naming a tool or brush this game " +
                    "does not have, " + _dropApplyUnusablePrefab + " naming one that cannot " +
                    "terraform, " + _dropApplyCreateFailed + " that failed to build, " +
                    _dropApplyMalformed + " malformed). The ground here is now different from the " +
                    "other player's.");

            if (_dropSendBadFrame > 0)
                Diagnostics.SyncLog.Warn(LogTopic.Land, "Terrain sync: " + _dropSendBadFrame +
                    " terraforming sample(s) were sent with " +
                    "a substitute frame time because this machine reported an implausible one. " +
                    "Their height change may be slightly off on the other player's map.");

            if (notSent > 0 || notApplied > 0 || _dropSendBadFrame > 0 || _dropApplyUnavailable > 0)
                SyncLog.Trace(LogTopic.Land, "terrain dropped sendNoTool=" + _dropSendNoToolName +
                    " sendNoBrush=" + _dropSendNoBrushName + " sendOpacity=" + _dropSendOpacity +
                    " sendBadFrame=" + _dropSendBadFrame + " applyUnknownPrefab=" +
                    _dropApplyUnknownPrefab + " applyUnusablePrefab=" + _dropApplyUnusablePrefab +
                    " applyCreateFailed=" + _dropApplyCreateFailed + " applyMalformed=" +
                    _dropApplyMalformed + " applyUnavailable=" + _dropApplyUnavailable);

            _dropSendNoToolName = _dropSendNoBrushName = _dropSendOpacity = _dropSendBadFrame = 0;
            _dropApplyUnknownPrefab = _dropApplyUnusablePrefab = _dropApplyCreateFailed = 0;
            _dropApplyMalformed = _dropApplyUnavailable = 0;
        }
    }
}
