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
    // Capturing local recolouring. An edit is held until it settles rather than sent per frame -
    // dragging a colour picker would otherwise be one command per mouse move - and the batch
    // recolour of a whole prefab is captured as its own change.
    public partial class VisualCustomizationSyncSystem
    {
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
    }
}
