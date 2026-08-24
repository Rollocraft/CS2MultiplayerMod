using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Colossal.Entities;
using CS2MultiplayerMod.Core.Protocol.Messages;
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
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Replicates the persistent state edited by the visual-customization section:
    /// per-entity custom mesh colors, the historical-building flag, and the savegame's
    /// global color-preset palette. These edits mutate existing components directly,
    /// so placement/update detectors cannot observe them.
    /// </summary>
    public partial class VisualCustomizationSyncSystem : GameSystemBase
    {
        private const long RetryWindowMs = 10000;
        private const long RetryIntervalMs = 250;
        private const long SuppressWindowMs = 5000;
        private const long PruneIntervalMs = 30000;
        private const int MaxRetryTargets = 4096;
        private const float MatchToleranceSq = 4f;

        // The color picker rewrites CustomMeshColor on every UI frame it is dragged, so the
        // resulting-state detector sees one change per frame. Only the value a drag settles on
        // is worth replicating; a drag that never pauses still reports once per max hold.
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

        /// <summary>
        /// Per-frame snapshot of the match candidates, bucketed by prefab. Without it the
        /// spatial fallback walks - and re-reads components from - every colorable object in
        /// the city once per target, for every command and every queued retry.
        /// </summary>
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
                Bucket bucket;
                if (_byPrefab.TryGetValue(prefab, out bucket)) return bucket;

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

        private readonly ConcurrentQueue<SimulationCommandMessage> _incoming =
            new ConcurrentQueue<SimulationCommandMessage>();
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
        private CommandObserver _observer;

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
            Mod.log.Info(nameof(VisualCustomizationSyncSystem) + " ready.");

            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _prefabIndex = new PrefabIndex(_prefabSystem,
                GetEntityQuery(ComponentType.ReadOnly<PrefabData>()));
            _selectedInfo = World.GetOrCreateSystemManaged<SelectedInfoUISystem>();
            _paletteSystem = World.GetOrCreateSystemManaged<MeshColorPaletteSystem>();
            _endFrameBarrier = World.GetOrCreateSystemManaged<EndFrameBarrier>();

            _batchColorQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<BatchesUpdated>(),
                    ComponentType.ReadOnly<CustomMeshColor>(),
                    ComponentType.ReadOnly<MeshColor>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Transform>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                },
                Options = EntityQueryOptions.IgnoreComponentEnabledState,
            });

            _targetQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Transform>(),
                },
                Any = new[]
                {
                    ComponentType.ReadOnly<CustomMeshColor>(),
                    ComponentType.ReadOnly<Building>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                },
                Options = EntityQueryOptions.IgnoreComponentEnabledState,
            });

            if (Mod.Service != null)
            {
                _observer = new CommandObserver(
                    _incoming, VisualCustomizationCommand.Id, ColorPaletteCommand.Id);
                _observer.MaxBodyBytes = VisualCustomizationCommand.MaxEncodedBytes;
                Mod.Service.Session.AddObserver(_observer);
            }
            SyncInbox.RegisterDrain(DrainQueue);
        }

        protected override void OnDestroy()
        {
            SyncInbox.UnregisterDrain(DrainQueue);
            if (_observer != null && Mod.Service != null)
                Mod.Service.Session.RemoveObserver(_observer);
            base.OnDestroy();
        }

        private void DrainQueue()
        {
            SyncInbox.Clear(_incoming);
            ResetTracking();
        }

        protected override void OnUpdate()
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

                // A UI edit and an incoming edit can land in the same UI frame. The host
                // relays the incoming command first and the local command second, so preserve
                // that same final order locally.
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

        // ---- local capture ---------------------------------------------------

        private void CaptureLocalVisualChanges(long now)
        {
            CaptureSelectedChange(now);
            CaptureBatchColorChanges(now);
        }

        /// <summary>
        /// Emits the field changes that are ready to leave: the Historical flag immediately
        /// (a single click), plus every color edit whose value has settled.
        /// </summary>
        private List<VisualCustomizationCommand> TakeSettledVisualChanges(long now)
        {
            CollectSettledColorEdits(now);
            if (_outgoing.Count == 0) return null;

            var commands = new List<VisualCustomizationCommand>(_outgoing.Count);
            for (int i = 0; i < _outgoing.Count; i++) commands.Add(_outgoing[i].Build());
            _outgoing.Clear();
            return commands;
        }

        private void CollectSettledColorEdits(long now)
        {
            if (_pendingColor.Count == 0) return;

            // Selecting something else ends the gesture, so there is nothing left to wait for.
            bool flushAll = _pendingColor.Count >= MaxPendingColorTargets ||
                            (_pendingAnchorValid && _selectedInfo.selectedEntity != _pendingAnchor);

            List<Entity> settled = null;
            foreach (KeyValuePair<Entity, PendingColorEdit> pair in _pendingColor)
            {
                if (!flushAll &&
                    now - pair.Value.LastChangeMs < ColorSettleMs &&
                    now - pair.Value.FirstChangeMs < ColorMaxHoldMs)
                    continue;
                (settled ?? (settled = new List<Entity>())).Add(pair.Key);
            }
            if (settled == null) return;

            for (int i = 0; i < settled.Count; i++)
            {
                PendingColorEdit entry = _pendingColor[settled[i]];
                _pendingColor.Remove(settled[i]);
                AddTarget(_outgoing, settled[i], VisualCustomizationFields.MeshColor, in entry.State);
            }
            if (_pendingColor.Count == 0) _pendingAnchorValid = false;
        }

        private void CaptureSelectedChange(long now)
        {
            Entity selected = _selectedInfo.selectedEntity;
            VisualState current;
            if (!TryReadState(selected, out current))
            {
                _lastSelected = selected;
                _lastSelectedValid = false;
                return;
            }

            if (selected != _lastSelected || !_lastSelectedValid)
            {
                _lastSelected = selected;
                _lastSelectedState = current;
                _lastSelectedValid = true;
                _known[selected] = current;
                return;
            }

            VisualCustomizationFields fields = VisualCustomizationFields.None;
            if (current.SupportsColor && _lastSelectedState.SupportsColor &&
                !SameColorState(in current, in _lastSelectedState))
                fields |= VisualCustomizationFields.MeshColor;
            if (current.SupportsHistorical && _lastSelectedState.SupportsHistorical &&
                current.IsHistorical != _lastSelectedState.IsHistorical)
                fields |= VisualCustomizationFields.Historical;

            _lastSelectedState = current;
            _known[selected] = current;
            if (fields != VisualCustomizationFields.None)
                AddChange(selected, fields, in current, now);
        }

        private void CaptureBatchColorChanges(long now)
        {
            if (_batchColorQuery.IsEmptyIgnoreFilter) return;

            Entity selected = _selectedInfo.selectedEntity;
            Entity selectedPrefab = Entity.Null;
            VisualColorSet selectedEffective = default(VisualColorSet);
            bool canBeSetToAll = TryGetEffectiveColor(selected, out selectedEffective) &&
                                 TryGetPrefab(selected, out selectedPrefab);

            NativeArray<Entity> entities = _batchColorQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity entity = entities[i];
                    VisualState current;
                    if (!TryReadState(entity, out current) || !current.SupportsColor) continue;

                    VisualState known;
                    bool hadKnown = _known.TryGetValue(entity, out known);
                    long suppressUntil;
                    if (_suppressColorBatch.TryGetValue(entity, out suppressUntil))
                    {
                        if (suppressUntil >= now && hadKnown && SameColorState(in current, in known))
                            continue;
                        _suppressColorBatch.Remove(entity);
                    }

                    if (entity == selected)
                    {
                        _known[entity] = current;
                        continue;
                    }

                    Entity prefab;
                    bool setToAllTarget =
                        canBeSetToAll &&
                        current.HasCustomColor &&
                        TryGetPrefab(entity, out prefab) &&
                        prefab == selectedPrefab &&
                        current.Color.Equals(selectedEffective);

                    bool changed = hadKnown && !SameColorState(in current, in known);
                    _known[entity] = current;

                    // "Set to all" writes every sibling and tags it BatchesUpdated even
                    // when its value was already equal, so the selected entity's effective
                    // color is the reliable signature for those otherwise-invisible writes.
                    if (setToAllTarget || changed)
                        AddChange(entity, VisualCustomizationFields.MeshColor, in current, now);
                }
            }
            finally
            {
                entities.Dispose();
            }
        }

        // A palette swatch has the same per-frame color picker behind it as an entity color.
        private void CaptureLocalPaletteChange(long now)
        {
            VisualColorSet[] current = ReadPalette();
            if (!_paletteKnown)
            {
                _knownPalette = current;
                _paletteKnown = true;
                return;
            }
            if (SamePalette(current, _knownPalette)) return;

            _knownPalette = ClonePalette(current);
            if (_pendingPalette == null) _pendingPaletteFirstMs = now;
            _pendingPalette = new ColorPaletteCommand { Colors = current };
            _pendingPaletteLastMs = now;
        }

        private ColorPaletteCommand TakeSettledPaletteChange(long now)
        {
            if (_pendingPalette == null) return null;
            if (now - _pendingPaletteLastMs < ColorSettleMs &&
                now - _pendingPaletteFirstMs < ColorMaxHoldMs)
                return null;

            ColorPaletteCommand command = _pendingPalette;
            _pendingPalette = null;
            return command;
        }

        private void AddChange(Entity entity, VisualCustomizationFields fields,
            in VisualState state, long now)
        {
            if ((fields & VisualCustomizationFields.MeshColor) != 0)
                RecordColorEdit(entity, in state, now);
            if ((fields & VisualCustomizationFields.Historical) != 0)
                AddTarget(_outgoing, entity, VisualCustomizationFields.Historical, in state);
        }

        private void RecordColorEdit(Entity entity, in VisualState state, long now)
        {
            if (_pendingColor.Count == 0)
            {
                _pendingAnchor = _selectedInfo.selectedEntity;
                _pendingAnchorValid = true;
            }

            PendingColorEdit entry;
            if (!_pendingColor.TryGetValue(entity, out entry)) entry.FirstChangeMs = now;
            entry.State = state;
            entry.LastChangeMs = now;
            _pendingColor[entity] = entry;
        }

        private void AddTarget(List<CommandBuilder> builders, Entity entity,
            VisualCustomizationFields fields, in VisualState state)
        {
            Entity prefab;
            if (!TryGetPrefab(entity, out prefab)) return;
            string prefabName = _prefabSystem.GetPrefabName(prefab);
            if (string.IsNullOrEmpty(prefabName)) return;

            VisualCustomizationTarget target;
            if (!TryBuildTarget(entity, out target)) return;

            for (int i = 0; i < builders.Count; i++)
            {
                CommandBuilder existing = builders[i];
                if (existing.Targets.Count < VisualCustomizationCommand.MaxTargets &&
                    existing.SamePayload(prefabName, fields, in state))
                {
                    existing.Targets.Add(target);
                    return;
                }
            }

            var builder = new CommandBuilder
            {
                PrefabName = prefabName,
                Fields = fields,
                HasCustomColor = state.HasCustomColor,
                Color = state.Color,
                IsHistorical = state.IsHistorical,
            };
            builder.Targets.Add(target);
            builders.Add(builder);
        }

        private bool TryBuildTarget(Entity entity, out VisualCustomizationTarget target)
        {
            target = default(VisualCustomizationTarget);
            if (!EntityManager.Exists(entity) || !EntityManager.HasComponent<Transform>(entity))
                return false;

            float3 position = EntityManager.GetComponentData<Transform>(entity).m_Position;
            int seed = EntityManager.HasComponent<PseudoRandomSeed>(entity)
                ? EntityManager.GetComponentData<PseudoRandomSeed>(entity).m_Seed
                : -1;
            target = new VisualCustomizationTarget
            {
                EntityIndex = entity.Index,
                EntityVersion = entity.Version,
                RandomSeed = seed,
                X = position.x,
                Y = position.y,
                Z = position.z,
            };
            return true;
        }

        // ---- incoming / retry ------------------------------------------------

        private void ApplyIncoming(MultiplayerSession session, long now)
        {
            SimulationCommandMessage message;
            while (_incoming.TryDequeue(out message))
            {
                if (message.OriginPlayerId == session.LocalPlayerId) continue;
                try
                {
                    if (message.CommandId == VisualCustomizationCommand.Id)
                        ApplyVisual(VisualCustomizationCommand.Decode(message.Body), now,
                            now + RetryWindowMs, allowRetry: true);
                    else if (message.CommandId == ColorPaletteCommand.Id)
                        ApplyPalette(ColorPaletteCommand.Decode(message.Body));
                }
                catch (Exception ex)
                {
                    Mod.log.Warn("[MP] VisualCustomizationSync: dropping malformed command: " +
                                 ex.Message);
                    SyncInbox.RequestResync("malformed visual-customization command");
                }
            }
        }

        // Re-resolving every pending command on every frame is what turns a burst of targets
        // into a stall; the retry window is measured in seconds, so this granularity is ample.
        private void ApplyRetries(long now)
        {
            if (_retry.Count == 0) return;
            if (now - _lastRetryMs < RetryIntervalMs) return;
            _lastRetryMs = now;

            PendingVisual[] pending = _retry.ToArray();
            _retry.Clear();
            _retryTargetCount = 0;
            int expiredTargets = 0;
            for (int i = 0; i < pending.Length; i++)
            {
                if (pending[i].DeadlineMs < now)
                {
                    expiredTargets += pending[i].Command.Targets.Length;
                    continue;
                }
                ApplyVisual(pending[i].Command, now, pending[i].DeadlineMs, allowRetry: true);
            }
            if (expiredTargets > 0)
            {
                Mod.log.Warn("[MP] VisualCustomizationSync: " + expiredTargets +
                             " target(s) did not appear before the retry deadline.");
                SyncInbox.RequestResync("visual-customization target did not resolve");
            }
        }

        private void QueueRetry(VisualCustomizationCommand source,
            List<VisualCustomizationTarget> unresolved, long deadline)
        {
            if (unresolved == null || unresolved.Count == 0) return;

            var command = new VisualCustomizationCommand
            {
                PrefabName = source.PrefabName,
                Fields = source.Fields,
                HasCustomColor = source.HasCustomColor,
                Color = source.Color,
                IsHistorical = source.IsHistorical,
                Targets = unresolved.ToArray(),
            };
            _retry.Add(new PendingVisual { Command = command, DeadlineMs = deadline });
            _retryTargetCount += command.Targets.Length;

            while (_retryTargetCount > MaxRetryTargets && _retry.Count > 0)
            {
                _retryTargetCount -= _retry[0].Command.Targets.Length;
                _retry.RemoveAt(0);
                SyncInbox.RequestResync("visual-customization retry budget exhausted");
            }
        }

        // ---- apply -----------------------------------------------------------

        private void ApplyVisual(VisualCustomizationCommand command, long now,
            long retryDeadline, bool allowRetry)
        {
            Entity prefab;
            if (!_prefabIndex.TryResolve(command.PrefabName, out prefab))
            {
                if (allowRetry)
                    QueueRetry(command, new List<VisualCustomizationTarget>(command.Targets),
                        retryDeadline);
                return;
            }

            var used = new HashSet<Entity>();
            var unresolved = allowRetry ? new List<VisualCustomizationTarget>() : null;
            for (int i = 0; i < command.Targets.Length; i++)
            {
                VisualCustomizationTarget target = command.Targets[i];
                Entity entity = ResolveTarget(prefab, in target, used);
                if (entity == Entity.Null)
                {
                    if (unresolved != null) unresolved.Add(target);
                    continue;
                }

                used.Add(entity);
                if (ApplyTarget(entity, command, now)) _selectedInfoDirty = true;
            }

            if (unresolved != null && unresolved.Count > 0)
                QueueRetry(command, unresolved, retryDeadline);
        }

        private bool ApplyTarget(Entity entity, VisualCustomizationCommand command, long now)
        {
            VisualState state;
            if (!TryReadState(entity, out state))
                return false;

            // An enable/disable recorded in EndFrameBarrier is not visible through
            // IsComponentEnabled until the next frame. Keep the already-applied desired
            // color state for sequential commands in this frame, but always refresh
            // component support and the directly-written historical flag.
            VisualState pending;
            long suppressUntil;
            if (_suppressColorBatch.TryGetValue(entity, out suppressUntil) &&
                suppressUntil >= now &&
                _known.TryGetValue(entity, out pending) &&
                pending.SupportsColor)
            {
                state.HasCustomColor = pending.HasCustomColor;
                state.Color = pending.Color;
            }

            bool changed = false;
            if ((command.Fields & VisualCustomizationFields.MeshColor) != 0 &&
                state.SupportsColor &&
                (state.HasCustomColor != command.HasCustomColor ||
                 (command.HasCustomColor && !state.Color.Equals(command.Color))))
            {
                DynamicBuffer<CustomMeshColor> buffer =
                    EntityManager.GetBuffer<CustomMeshColor>(entity);
                if (command.HasCustomColor)
                {
                    CustomMeshColor value = new CustomMeshColor
                    {
                        m_ColorSet = ToGameColor(command.Color),
                    };
                    if (buffer.Length == 0) buffer.Add(value);
                    else buffer[0] = value;
                    FrameCommands().SetComponentEnabled<CustomMeshColor>(entity, true);
                }
                else
                {
                    EntityManager.SetComponentEnabled<CustomMeshColor>(entity, false);
                    buffer.Clear();
                    // Override a same-frame queued enable from an earlier command.
                    FrameCommands().SetComponentEnabled<CustomMeshColor>(entity, false);
                }
                FrameCommands().AddComponent<BatchesUpdated>(entity);
                _suppressColorBatch[entity] = now + SuppressWindowMs;
                state.HasCustomColor = command.HasCustomColor;
                state.Color = command.Color;
                changed = true;
            }

            if ((command.Fields & VisualCustomizationFields.Historical) != 0 &&
                state.SupportsHistorical &&
                state.IsHistorical != command.IsHistorical)
            {
                Building building = EntityManager.GetComponentData<Building>(entity);
                if (command.IsHistorical)
                    building.m_Flags |= global::Game.Buildings.BuildingFlags.Historical;
                else
                    building.m_Flags &= ~global::Game.Buildings.BuildingFlags.Historical;
                EntityManager.SetComponentData(entity, building);
                state.IsHistorical = command.IsHistorical;
                changed = true;
            }

            _known[entity] = state;
            if (entity == _lastSelected)
            {
                _lastSelectedState = state;
                _lastSelectedValid = true;
            }
            return changed;
        }

        private void EnsureLocalState(VisualCustomizationCommand command, long now)
        {
            Entity prefab;
            if (!_prefabIndex.TryResolve(command.PrefabName, out prefab)) return;

            var used = new HashSet<Entity>();
            bool needsApply = false;
            for (int i = 0; i < command.Targets.Length; i++)
            {
                Entity entity = ResolveTarget(prefab, in command.Targets[i], used);
                if (entity == Entity.Null) continue;
                used.Add(entity);

                VisualState state;
                if (!_known.TryGetValue(entity, out state) && !TryReadState(entity, out state))
                    continue;
                if (!MatchesCommand(in state, command))
                {
                    needsApply = true;
                    break;
                }
            }

            if (needsApply) ApplyVisual(command, now, 0, allowRetry: false);
        }

        private void ApplyPalette(ColorPaletteCommand command)
        {
            if (_paletteKnown && SamePalette(_knownPalette, command.Colors)) return;
            WritePalette(command.Colors);
            _knownPalette = ClonePalette(command.Colors);
            _paletteKnown = true;
            _selectedInfoDirty = true;
        }

        private void EnsureLocalPalette(ColorPaletteCommand command)
        {
            if (_paletteKnown && SamePalette(_knownPalette, command.Colors)) return;
            ApplyPalette(command);
        }

        // ---- target resolution ----------------------------------------------

        private Entity ResolveTarget(Entity prefab, in VisualCustomizationTarget target,
            HashSet<Entity> used)
        {
            Entity hinted = new Entity
            {
                Index = target.EntityIndex,
                Version = target.EntityVersion,
            };
            if (!used.Contains(hinted) && IsBaseCandidate(hinted, prefab))
            {
                bool seedMatches = target.RandomSeed < 0 ||
                    (EntityManager.HasComponent<PseudoRandomSeed>(hinted) &&
                     EntityManager.GetComponentData<PseudoRandomSeed>(hinted).m_Seed ==
                     target.RandomSeed);
                float3 hintedPosition =
                    EntityManager.GetComponentData<Transform>(hinted).m_Position;
                float distanceSq = math.distancesq(
                    hintedPosition, new float3(target.X, target.Y, target.Z));
                if (seedMatches || distanceSq <= MatchToleranceSq)
                    return hinted;
            }

            // The bucket already satisfies every IsBaseCandidate condition except an in-frame
            // delete, so only the winner needs re-validating against live state.
            CandidateCache.Bucket bucket = _candidates.For(prefab, _targetQuery, EntityManager);
            int bestSeed = -1;
            int bestNear = -1;
            float bestSeedDistance = float.MaxValue;
            float bestNearDistance = float.MaxValue;
            int seedMatchesCount = 0;
            float3 position = new float3(target.X, target.Y, target.Z);
            bool skipUsed = used.Count != 0;

            for (int i = 0; i < bucket.Count; i++)
            {
                if (skipUsed && used.Contains(bucket.Entities[i])) continue;

                float distanceSq = math.distancesq(bucket.Positions[i], position);
                if (distanceSq < bestNearDistance)
                {
                    bestNearDistance = distanceSq;
                    bestNear = i;
                }

                if (target.RandomSeed >= 0 && bucket.Seeds[i] == target.RandomSeed)
                {
                    seedMatchesCount++;
                    if (distanceSq < bestSeedDistance)
                    {
                        bestSeedDistance = distanceSq;
                        bestSeed = i;
                    }
                }
            }

            if (bestSeed >= 0 &&
                (seedMatchesCount == 1 || bestSeedDistance <= MatchToleranceSq ||
                 EntityManager.HasComponent<Vehicle>(bucket.Entities[bestSeed])) &&
                IsBaseCandidate(bucket.Entities[bestSeed], prefab))
                return bucket.Entities[bestSeed];
            return bestNear >= 0 && bestNearDistance <= MatchToleranceSq &&
                   IsBaseCandidate(bucket.Entities[bestNear], prefab)
                ? bucket.Entities[bestNear]
                : Entity.Null;
        }

        private bool IsBaseCandidate(Entity entity, Entity prefab)
        {
            if (!EntityManager.Exists(entity) ||
                EntityManager.HasComponent<Deleted>(entity) ||
                EntityManager.HasComponent<Temp>(entity) ||
                !EntityManager.HasComponent<PrefabRef>(entity) ||
                !EntityManager.HasComponent<Transform>(entity))
                return false;
            if (EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab != prefab)
                return false;
            return EntityManager.HasBuffer<CustomMeshColor>(entity) ||
                   EntityManager.HasComponent<Building>(entity);
        }

        // ---- state helpers ---------------------------------------------------

        private bool TryReadState(Entity entity, out VisualState state)
        {
            state = default(VisualState);
            Entity prefab;
            if (!TryGetPrefab(entity, out prefab)) return false;

            state.SupportsColor =
                !EntityManager.HasComponent<Plant>(entity) &&
                EntityManager.HasBuffer<MeshColor>(entity) &&
                EntityManager.HasBuffer<CustomMeshColor>(entity);
            if (state.SupportsColor)
            {
                DynamicBuffer<CustomMeshColor> custom =
                    EntityManager.GetBuffer<CustomMeshColor>(entity, true);
                state.HasCustomColor =
                    EntityManager.IsComponentEnabled<CustomMeshColor>(entity) &&
                    custom.Length > 0;
                if (state.HasCustomColor)
                    state.Color = FromGameColor(custom[0].m_ColorSet);
            }

            state.SupportsHistorical = CanBeHistorical(entity, prefab);
            if (state.SupportsHistorical)
            {
                Building building = EntityManager.GetComponentData<Building>(entity);
                state.IsHistorical =
                    (building.m_Flags & global::Game.Buildings.BuildingFlags.Historical) != 0;
            }
            return state.SupportsColor || state.SupportsHistorical;
        }

        private bool CanBeHistorical(Entity entity, Entity prefab) =>
            EntityManager.HasComponent<Building>(entity) &&
            !EntityManager.HasComponent<Abandoned>(entity) &&
            EntityManager.HasComponent<SpawnableBuildingData>(prefab) &&
            !EntityManager.HasComponent<SignatureBuildingData>(prefab);

        private bool TryGetEffectiveColor(Entity entity, out VisualColorSet color)
        {
            color = default(VisualColorSet);
            if (!EntityManager.Exists(entity) ||
                EntityManager.HasComponent<Plant>(entity) ||
                !EntityManager.HasBuffer<MeshColor>(entity) ||
                !EntityManager.HasBuffer<CustomMeshColor>(entity))
                return false;

            DynamicBuffer<CustomMeshColor> custom =
                EntityManager.GetBuffer<CustomMeshColor>(entity, true);
            if (custom.Length > 0)
            {
                color = FromGameColor(custom[0].m_ColorSet);
                return true;
            }

            DynamicBuffer<MeshColor> mesh = EntityManager.GetBuffer<MeshColor>(entity, true);
            if (mesh.Length == 0) return false;
            color = FromGameColor(mesh[0].m_ColorSet);
            return true;
        }

        private bool TryGetPrefab(Entity entity, out Entity prefab)
        {
            prefab = Entity.Null;
            if (entity == Entity.Null || !EntityManager.Exists(entity) ||
                EntityManager.HasComponent<Deleted>(entity) ||
                EntityManager.HasComponent<Temp>(entity) ||
                !EntityManager.HasComponent<PrefabRef>(entity))
                return false;
            prefab = EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab;
            return prefab != Entity.Null && EntityManager.Exists(prefab);
        }

        private static bool SameColorState(in VisualState left, in VisualState right) =>
            left.SupportsColor == right.SupportsColor &&
            left.HasCustomColor == right.HasCustomColor &&
            (!left.HasCustomColor || left.Color.Equals(right.Color));

        private static bool MatchesCommand(in VisualState state,
            VisualCustomizationCommand command)
        {
            if ((command.Fields & VisualCustomizationFields.MeshColor) != 0 &&
                state.SupportsColor &&
                (state.HasCustomColor != command.HasCustomColor ||
                 (command.HasCustomColor && !state.Color.Equals(command.Color))))
                return false;
            if ((command.Fields & VisualCustomizationFields.Historical) != 0 &&
                state.SupportsHistorical &&
                state.IsHistorical != command.IsHistorical)
                return false;
            return true;
        }

        private VisualColorSet[] ReadPalette()
        {
            if (!_paletteSystem.HasPaletteEntity()) return new VisualColorSet[0];
            DynamicBuffer<MeshColorPalette> buffer = _paletteSystem.GetPaletteBuffer();
            int count = Math.Min(buffer.Length, ColorPaletteCommand.MaxColorSets);
            var result = new VisualColorSet[count];
            for (int i = 0; i < count; i++)
                result[i] = FromGameColor(buffer[i].m_ColorSet);
            return result;
        }

        private void WritePalette(VisualColorSet[] colors)
        {
            DynamicBuffer<MeshColorPalette> buffer = _paletteSystem.GetPaletteBuffer();
            buffer.Clear();
            for (int i = 0; i < colors.Length; i++)
            {
                buffer.Add(new MeshColorPalette
                {
                    m_ColorSet = ToGameColor(colors[i]),
                });
            }
        }

        private static bool SamePalette(VisualColorSet[] left, VisualColorSet[] right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left == null || right == null || left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; i++)
                if (!left[i].Equals(right[i])) return false;
            return true;
        }

        private static VisualColorSet[] ClonePalette(VisualColorSet[] source)
        {
            if (source == null) return null;
            var clone = new VisualColorSet[source.Length];
            Array.Copy(source, clone, source.Length);
            return clone;
        }

        private static VisualColorSet FromGameColor(ColorSet color) => new VisualColorSet
        {
            R0 = color.m_Channel0.r, G0 = color.m_Channel0.g,
            B0 = color.m_Channel0.b, A0 = color.m_Channel0.a,
            R1 = color.m_Channel1.r, G1 = color.m_Channel1.g,
            B1 = color.m_Channel1.b, A1 = color.m_Channel1.a,
            R2 = color.m_Channel2.r, G2 = color.m_Channel2.g,
            B2 = color.m_Channel2.b, A2 = color.m_Channel2.a,
        };

        private static ColorSet ToGameColor(VisualColorSet color) => new ColorSet
        {
            m_Channel0 = new UnityEngine.Color(color.R0, color.G0, color.B0, color.A0),
            m_Channel1 = new UnityEngine.Color(color.R1, color.G1, color.B1, color.A1),
            m_Channel2 = new UnityEngine.Color(color.R2, color.G2, color.B2, color.A2),
        };

        private void Prune(long now)
        {
            if (now - _lastPruneMs < PruneIntervalMs) return;
            _lastPruneMs = now;

            List<Entity> dead = null;
            foreach (KeyValuePair<Entity, VisualState> pair in _known)
            {
                if (EntityManager.Exists(pair.Key) &&
                    !EntityManager.HasComponent<Deleted>(pair.Key))
                    continue;
                (dead ?? (dead = new List<Entity>())).Add(pair.Key);
            }
            if (dead != null)
                for (int i = 0; i < dead.Count; i++) _known.Remove(dead[i]);

            dead = null;
            foreach (KeyValuePair<Entity, long> pair in _suppressColorBatch)
            {
                if (pair.Value >= now && EntityManager.Exists(pair.Key)) continue;
                (dead ?? (dead = new List<Entity>())).Add(pair.Key);
            }
            if (dead != null)
                for (int i = 0; i < dead.Count; i++) _suppressColorBatch.Remove(dead[i]);
        }
    }
}
