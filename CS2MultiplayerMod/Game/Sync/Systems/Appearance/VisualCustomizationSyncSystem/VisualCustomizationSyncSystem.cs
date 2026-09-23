using System.Collections.Generic;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.Infrastructure;
using Game;
using Game.Buildings;
using Game.Common;
using Game.Objects;
using Game.Prefabs;
using Game.Rendering;
using Game.Tools;
using Game.UI.InGame;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Replicates visual customization: per-entity mesh colors, the historical flag and the global
    /// color palette. These write components directly, so placement detectors cannot see them.
    /// </summary>
    public partial class VisualCustomizationSyncSystem : CommandSyncSystem
    {
        private const long RetryWindowMs = 10000;
        private const long RetryIntervalMs = 250;
        private const long SuppressWindowMs = 5000;
        private const long PruneIntervalMs = 30000;
        private const int MaxRetryTargets = 4096;
        private const float MatchToleranceSq = 4f;

        // The picker writes every UI frame while dragged; send the settled value (or once per max hold).
        private const long ColorSettleMs = 1000;
        private const long ColorMaxHoldMs = 3000;
        private const int MaxPendingColorTargets = 8192;

        private struct VisualState
        {
            public bool SupportsColor;
            public bool HasCustomColor;
            public VisualColorSet Color;
            public bool SupportsHistorical;
            public bool IsHistorical;
        }

        private struct PendingVisual
        {
            public VisualCustomizationCommand Command;
            public long DeadlineMs;
        }

        private struct PendingColorEdit
        {
            public VisualState State;
            public long FirstChangeMs;
            public long LastChangeMs;
        }

        /// <summary>Per-frame match candidates bucketed by prefab, so retries do not walk the city.</summary>
        private sealed class CandidateCache
        {
            public sealed class Bucket
            {
                public Entity[] Entities;
                public float3[] Positions;
                public int[] Seeds;
                public int Count;
            }

            private readonly Dictionary<Entity, Bucket> _byPrefab = new Dictionary<Entity, Bucket>();
            private NativeArray<Entity> _entities;
            private NativeArray<PrefabRef> _prefabs;
            private NativeArray<Transform> _transforms;
            private bool _loaded;

            public Bucket For(Entity prefab, EntityQuery query, EntityManager entityManager)
            {
                if (_byPrefab.TryGetValue(prefab, out Bucket bucket)) return bucket;

                if (!_loaded)
                {
                    _entities = query.ToEntityArray(Allocator.Temp);
                    _prefabs = query.ToComponentDataArray<PrefabRef>(Allocator.Temp);
                    _transforms = query.ToComponentDataArray<Transform>(Allocator.Temp);
                    _loaded = true;
                }

                int count = 0;
                for (int i = 0; i < _prefabs.Length; i++)
                    if (_prefabs[i].m_Prefab == prefab) count++;

                bucket = new Bucket
                {
                    Entities = new Entity[count],
                    Positions = new float3[count],
                    Seeds = new int[count],
                    Count = count,
                };

                int next = 0;
                for (int i = 0; i < _prefabs.Length && next < count; i++)
                {
                    if (_prefabs[i].m_Prefab != prefab) continue;
                    Entity entity = _entities[i];
                    bucket.Entities[next] = entity;
                    bucket.Positions[next] = _transforms[i].m_Position;
                    bucket.Seeds[next] = entityManager.HasComponent<PseudoRandomSeed>(entity)
                        ? entityManager.GetComponentData<PseudoRandomSeed>(entity).m_Seed
                        : -1;
                    next++;
                }

                _byPrefab[prefab] = bucket;
                return bucket;
            }

            public void Release()
            {
                if (_loaded)
                {
                    _entities.Dispose();
                    _prefabs.Dispose();
                    _transforms.Dispose();
                    _loaded = false;
                }
                _byPrefab.Clear();
            }
        }

        private sealed class CommandBuilder
        {
            public string PrefabName;
            public VisualCustomizationFields Fields;
            public bool HasCustomColor;
            public VisualColorSet Color;
            public bool IsHistorical;
            public readonly List<VisualCustomizationTarget> Targets =
                new List<VisualCustomizationTarget>();

            public bool SamePayload(string prefabName, VisualCustomizationFields fields,
                in VisualState state)
            {
                if (PrefabName != prefabName || Fields != fields) return false;
                if ((fields & VisualCustomizationFields.MeshColor) != 0 &&
                    (HasCustomColor != state.HasCustomColor ||
                     (HasCustomColor && !Color.Equals(state.Color))))
                    return false;
                return (fields & VisualCustomizationFields.Historical) == 0 ||
                       IsHistorical == state.IsHistorical;
            }

            public VisualCustomizationCommand Build() => new VisualCustomizationCommand
            {
                PrefabName = PrefabName,
                Fields = Fields,
                HasCustomColor = HasCustomColor,
                Color = Color,
                IsHistorical = IsHistorical,
                Targets = Targets.ToArray(),
            };
        }

        private readonly Dictionary<Entity, VisualState> _known =
            new Dictionary<Entity, VisualState>();
        private readonly Dictionary<Entity, long> _suppressColorBatch =
            new Dictionary<Entity, long>();
        private readonly List<PendingVisual> _retry = new List<PendingVisual>();
        private readonly Dictionary<Entity, PendingColorEdit> _pendingColor =
            new Dictionary<Entity, PendingColorEdit>();
        private readonly List<CommandBuilder> _outgoing = new List<CommandBuilder>();
        private readonly CandidateCache _candidates = new CandidateCache();

        private PrefabSystem _prefabSystem;
        private PrefabIndex _prefabIndex;
        private SelectedInfoUISystem _selectedInfo;
        private MeshColorPaletteSystem _paletteSystem;
        private EndFrameBarrier _endFrameBarrier;
        private EntityQuery _batchColorQuery;
        private EntityQuery _targetQuery;

        private Entity _lastSelected;
        private VisualState _lastSelectedState;
        private bool _lastSelectedValid;
        private VisualColorSet[] _knownPalette;
        private bool _paletteKnown;
        private bool _initialized;
        private int _retryTargetCount;
        private long _lastPruneMs;
        private long _lastRetryMs;
        private Entity _pendingAnchor;
        private bool _pendingAnchorValid;
        private ColorPaletteCommand _pendingPalette;
        private long _pendingPaletteFirstMs;
        private long _pendingPaletteLastMs;
        private EntityCommandBuffer _frameCommands;
        private bool _frameCommandsValid;
        private bool _selectedInfoDirty;

        protected override void OnCreate()
        {
            base.OnCreate();

            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _prefabIndex = new PrefabIndex(_prefabSystem,
                GetEntityQuery(ComponentType.ReadOnly<PrefabData>()));
            _selectedInfo = World.GetOrCreateSystemManaged<SelectedInfoUISystem>();
            _paletteSystem = World.GetOrCreateSystemManaged<MeshColorPaletteSystem>();
            _endFrameBarrier = World.GetOrCreateSystemManaged<EndFrameBarrier>();

            _batchColorQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<BatchesUpdated, CustomMeshColor, MeshColor, PrefabRef,
                    Transform>(),
                None = SyncQuery.ReadOnly<Temp, Deleted>(),
                Options = EntityQueryOptions.IgnoreComponentEnabledState,
            });

            _targetQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<PrefabRef, Transform>(),
                Any = SyncQuery.ReadOnly<CustomMeshColor, Building>(),
                None = SyncQuery.ReadOnly<Temp, Deleted>(),
                Options = EntityQueryOptions.IgnoreComponentEnabledState,
            });

            ListenFor(new[] { VisualCustomizationCommand.Id, ColorPaletteCommand.Id },
                VisualCustomizationCommand.MaxEncodedBytes);
        }

        protected override void DrainQueue()
        {
            SyncInbox.Clear(_incoming);
            ResetTracking();
        }

        protected override void OnUpdate()
        {
            using (Diagnostics.SyncProfiler.Measure("VisualCustomization"))
            {
                MultiplayerService service = Mod.Service;
                if (service == null) return;

                MultiplayerSession session = service.Session;
                if (!service.GameplaySyncReady)
                {
                    ResetTracking();
                    if (session.Status != SessionStatus.Connected)
                        SyncInbox.Clear(_incoming);
                    return;
                }

                long now = service.NowMs;
                _frameCommandsValid = false;
                _selectedInfoDirty = false;
                Prune(now);

                bool seededNow = false;
                if (!_initialized)
                {
                    SeedCurrentState();
                    _initialized = true;
                    seededNow = true;
                }

                if (!seededNow)
                {
                    CaptureLocalVisualChanges(now);
                    CaptureLocalPaletteChange(now);
                }

                try
                {
                    ApplyRetries(now);
                    ApplyIncoming(session, now);

                    List<VisualCustomizationCommand> localVisual = TakeSettledVisualChanges(now);
                    ColorPaletteCommand localPalette = TakeSettledPaletteChange(now);

                    // The host relays the incoming command before the local one; keep that order here.
                    if (localVisual != null)
                    {
                        for (int i = 0; i < localVisual.Count; i++)
                            EnsureLocalState(localVisual[i], now);
                    }
                    if (localPalette != null) EnsureLocalPalette(localPalette);

                    if (localVisual != null)
                    {
                        for (int i = 0; i < localVisual.Count; i++)
                            session.SendCommand(0, VisualCustomizationCommand.Id, localVisual[i].Encode());
                    }
                    if (localPalette != null)
                        session.SendCommand(0, ColorPaletteCommand.Id, localPalette.Encode());
                }
                finally
                {
                    _candidates.Release();
                }

                if (_selectedInfoDirty) _selectedInfo.RequestUpdate();
            }
        }

        private EntityCommandBuffer FrameCommands()
        {
            if (!_frameCommandsValid)
            {
                _frameCommands = _endFrameBarrier.CreateCommandBuffer();
                _frameCommandsValid = true;
            }
            return _frameCommands;
        }

        private void ResetTracking()
        {
            if (!_initialized && _known.Count == 0 && _retry.Count == 0 &&
                _pendingColor.Count == 0 && _outgoing.Count == 0 && _pendingPalette == null)
                return;
            _initialized = false;
            _known.Clear();
            _suppressColorBatch.Clear();
            _retry.Clear();
            _retryTargetCount = 0;
            _pendingColor.Clear();
            _outgoing.Clear();
            _pendingAnchor = Entity.Null;
            _pendingAnchorValid = false;
            _pendingPalette = null;
            _lastSelected = Entity.Null;
            _lastSelectedValid = false;
            _knownPalette = null;
            _paletteKnown = false;
        }

        private void SeedCurrentState()
        {
            _lastSelected = _selectedInfo.selectedEntity;
            _lastSelectedValid = TryReadState(_lastSelected, out _lastSelectedState);
            if (_lastSelectedValid) _known[_lastSelected] = _lastSelectedState;
            _knownPalette = ReadPalette();
            _paletteKnown = true;
        }
    }
}
