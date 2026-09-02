using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Colossal.Entities;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Diagnostics;
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
    // Applying a peer's recolouring. The entity it names may not exist locally yet, so an
    // unmatched command is retried within its window before being given up on.
    public partial class VisualCustomizationSyncSystem
    {
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
                    SyncLog.Warn(LogTopic.Buildings,
                        "VisualCustomizationSync: dropping malformed command: " + ex.Message);
                    SyncInbox.RequestResync(CS2MultiplayerMod.Game.Diagnostics.ResyncReport
                        .Create("malformed visual-customization command", "appearance",
                            CS2MultiplayerMod.Game.Diagnostics.ResyncEvidence.StreamLoss)
                        .About("malformed customization command")
                        .Tried("nothing - the command could not be decoded"));
                }
            }
        }

        // Re-resolving every pending command on every frame is what turns a burst of targets
        // into a stall; the retry window is measured in seconds, so this granularity is ample.
        private readonly HeldTime _targetHold = new HeldTime();

        private void ApplyRetries(long now)
        {
            // A customization waits for its building, and a zone-grown building is exactly what the
            // realize pipeline holds back while terrain or roads catch up. Counting the window down
            // through that hold expires it against a target that could not have arrived, and the
            // expiry below asks for a world reload.
            long heldMs = _targetHold.Observe(now, RealizeGate.WorldBuildingHeld);
            if (heldMs > 0)
                for (int h = 0; h < _retry.Count; h++)
                {
                    PendingVisual shifted = _retry[h];
                    shifted.DeadlineMs += heldMs;
                    _retry[h] = shifted;
                }

            if (_retry.Count == 0) return;
            if (now - _lastRetryMs < RetryIntervalMs) return;
            _lastRetryMs = now;

            PendingVisual[] pending = _retry.ToArray();
            _retry.Clear();
            _retryTargetCount = 0;
            int expiredTargets = 0;
            string firstExpiredPrefab = null;
            for (int i = 0; i < pending.Length; i++)
            {
                if (pending[i].DeadlineMs < now)
                {
                    if (firstExpiredPrefab == null)
                        firstExpiredPrefab = pending[i].Command.PrefabName;
                    expiredTargets += pending[i].Command.Targets.Length;
                    continue;
                }
                ApplyVisual(pending[i].Command, now, pending[i].DeadlineMs, allowRetry: true);
            }
            if (expiredTargets > 0)
            {
                SyncLog.Warn(LogTopic.Buildings, "VisualCustomizationSync: " + expiredTargets +
                    " target(s) did not appear before the retry deadline.");
                SyncInbox.RequestResync(CS2MultiplayerMod.Game.Diagnostics.ResyncReport
                    .Create("visual-customization target did not resolve", "appearance",
                        CS2MultiplayerMod.Game.Diagnostics.ResyncEvidence.MissingTarget)
                    .About("customization of '" + firstExpiredPrefab + "'")
                    .Tried("retried every 250 ms for 10 s of attempts, not counting time the buildings were held back")
                    .Fact("targets that did not appear", expiredTargets));
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
                SyncInbox.RequestResync(CS2MultiplayerMod.Game.Diagnostics.ResyncReport
                    .Create("visual-customization retry budget exhausted", "appearance",
                        CS2MultiplayerMod.Game.Diagnostics.ResyncEvidence.StreamLoss)
                    .About("customization retry budget")
                    .Tried("nothing - the oldest queued customizations were shed to stay within the budget"));
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
    }
}
