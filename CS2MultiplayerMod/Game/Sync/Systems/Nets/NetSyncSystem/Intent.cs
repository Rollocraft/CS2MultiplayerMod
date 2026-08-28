using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Game.Sync.Commands;

namespace CS2MultiplayerMod.Game.Sync.Systems.Net
{
    // Capturing what this player's own net tool did, as intent the other side can rebuild from.
    // The game emits definitions, not a description of the gesture, so the gesture is read back
    // out of them - including the hidden sub-networks it generated, which must not travel.
    //
    // Turning one definition into a command, and an endpoint into something a peer can resolve,
    // is in IntentCommand.cs.
    public partial class NetSyncSystem
    {
        private const long CommittedSideEffectWindowMs = 5000;

        /// <summary>
        /// Refresh the active net tool's cached course definitions. An empty steady-state frame keeps
        /// the prior cache because a motionless preview does not necessarily regenerate definitions.
        /// The net tool also uses Clear while replacing a moving cursor preview, so Clear by itself
        /// is not cancellation and must not erase the last complete native operation. Switching away
        /// from the net tool still invalidates it.
        /// </summary>
        public void ObserveLocalNetDefinitions(NativeArray<Entity> definitions)
        {
            global::Game.Tools.ToolBaseSystem active = _toolSystem != null ? _toolSystem.activeTool : null;
            if (!(active is global::Game.Tools.NetToolSystem))
            {
                _cachedLocalCourses.Clear();
                _cachedLocalMixedOperation.Clear();
                _cachedFallbackOriginalEdges.Clear();
                _cachedNeedsFinalEdgeFallback = false;
                return;
            }

            // CaptureLocalNetApply can run before DefinitionGate observes this frame's buffered
            // definitions. Once that Apply has been published, those definitions are the same
            // gesture and must not seed a stale cache for the next click.
            if (_nativeApplyCapturedFrame == _realizeFrame) return;

            // A network prefab may create a top-level object as the owner of its course graph.
            // Owner-linked courses cannot be replayed as independent network placements; the
            // complete heterogeneous batch is captured atomically by BuildSyncSystem.
            if (global::CS2MultiplayerMod.Game.Sync.Systems.NativeObjectGraph
                .HasNewTopLevelObjectRoot(EntityManager, definitions))
            {
                _cachedLocalCourses.Clear();
                _cachedLocalMixedOperation.Clear();
                _cachedFallbackOriginalEdges.Clear();
                _cachedNeedsFinalEdgeFallback = false;
                return;
            }

            var netTool = (global::Game.Tools.NetToolSystem)active;
            bool pointOperation = netTool.actualMode == global::Game.Tools.NetToolSystem.Mode.Point;

            var next = new List<NetPlacementCommand>();
            var mixed = new List<LocalNetToolOperationItem>();
            // A course of this operation that cannot be expressed on the wire voids the whole native
            // envelope. Publishing the rest would ship a self-consistent but INCOMPLETE operation
            // (CourseCount counts only what survived) and would then suppress final-edge capture too,
            // so the missing courses would never reach the other machines at all.
            string rejection = null;
            int rejected = 0;
            int mutations = 0;
            int hiddenSubNets = 0;
            var rejectedOriginalEdges = new List<Entity>();
            for (int i = 0; i < definitions.Length; i++)
            {
                Entity entity = definitions[i];
                if (!EntityManager.Exists(entity) || !EntityManager.HasComponent<NetCourse>(entity) ||
                    !EntityManager.HasComponent<CreationDefinition>(entity)) continue;

                CreationDefinition definition = EntityManager.GetComponentData<CreationDefinition>(entity);
                if (EntityManager.HasComponent<OwnerDefinition>(entity))
                {
                    // Editing a road re-cuts the hidden connector of every building beside it, and
                    // those re-cuts join this operation as owned courses. They are a consequence of
                    // the edit, not part of it: the receiver rebuilds them from the visible net it
                    // is about to commit, exactly as it does for an unowned hidden sub-net. Only a
                    // VISIBLE owned course is real content, and BuildSyncSystem captures that batch.
                    if (IsGeneratedHiddenSubNet(definition))
                    {
                        hiddenSubNets++;
                        continue;
                    }
                    rejected++;
                    rejection = rejection ?? "carry an owner definition";
                    continue;
                }

                NetCourse course = EntityManager.GetComponentData<NetCourse>(entity);
                // Point-mode network prefabs intentionally commit a zero-length course (for example
                // a circular junction). Other modes' zero-length definitions are only cursor markers.
                // The threshold is the realize side's degenerate limit, not a round metre: a drawn
                // net can legitimately contain a sub-metre course (two crossings close together in
                // one drag, a short remainder in a grid), and dropping one silently shortens the
                // operation to a chain with a gap the receiver cannot bridge.
                if (course.m_Length < NetPlacementCommand.MinCourseLength && !pointOperation) continue;

                if (!IsPlainLocalNetDefinition(definition))
                {
                    string unrepresentable;
                    LocalNetToolOperationItem mutation =
                        CaptureMixedMutationCommand(definition, course, out unrepresentable);
                    if (mutation != null)
                    {
                        mixed.Add(mutation);
                        mutations++;
                    }
                    else if (unrepresentable != null)
                    {
                        rejected++;
                        rejection = rejection ?? unrepresentable;
                    }

                    Entity original = definition.m_Original;
                    // Retain every usable original while classifying. If any OTHER member later
                    // voids the envelope, all original-backed geometry must participate in the
                    // legacy fallback, including members which were individually representable.
                    if (original != Entity.Null && EntityManager.Exists(original) &&
                        EntityManager.HasComponent<Edge>(original) &&
                        EntityManager.HasComponent<Curve>(original) &&
                        EntityManager.HasComponent<PrefabRef>(original) &&
                        !rejectedOriginalEdges.Contains(original))
                        rejectedOriginalEdges.Add(original);
                }
                else
                {
                    string unrepresentable;
                    NetPlacementCommand command = CaptureDefinitionCommand(definition, course,
                        out unrepresentable);
                    if (command == null)
                    {
                        if (unrepresentable == null) continue;
                        rejected++;
                        rejection = rejection ?? unrepresentable;
                        continue;
                    }
                    next.Add(command);
                    mixed.Add(new LocalNetToolOperationItem
                    {
                        CommandId = NetPlacementCommand.Id,
                        Placement = command,
                    });
                }

                if (next.Count > NetPlacementCommand.MaxCoursesPerOperation ||
                    mixed.Count > NetToolOperationCommand.MaxItems)
                {
                    rejected++;
                    rejection = "exceed the atomic net-operation item cap";
                    break;
                }
            }

            if (next.Count == 0)
            {
                // A genuinely empty steady frame keeps the last preview, but a visible graph made
                // entirely of mutations is a new operation owned by the standalone delete/replace
                // systems. Clear any older placement preview so its later Apply cannot publish a
                // stale course envelope in place of this mutation-only gesture.
                if (mutations > 0 || rejected > 0)
                {
                    _cachedLocalCourses.Clear();
                    _cachedLocalMixedOperation.Clear();
                    _cachedFallbackOriginalEdges.Clear();
                    _cachedNeedsFinalEdgeFallback = false;
                }
                return;
            }

            _cachedLocalCourses.Clear();
            _cachedLocalMixedOperation.Clear();
            if (rejection == null && mutations == 0)
            {
                _cachedFallbackOriginalEdges.Clear();
                _cachedNeedsFinalEdgeFallback = false;
                _cachedLocalCourses.AddRange(next);
                return;
            }

            if (rejection == null)
            {
                // This is one native Apply, not a delete followed by replacements followed by
                // placements. Retain the exact definition order and send it through one envelope.
                _cachedFallbackOriginalEdges.Clear();
                _cachedNeedsFinalEdgeFallback = false;
                _cachedLocalMixedOperation.AddRange(mixed);
                Diagnostics.FlightRecorder.Note("net atomic mixed capture cached items=" +
                    mixed.Count + " placements=" + next.Count + " mutations=" + mutations +
                    (hiddenSubNets > 0 ? " hiddenSubNets=" + hiddenSubNets : string.Empty));
                return;
            }

            // Never resurrect the fragmented delete/replace/final-edge path. It is exactly the path
            // that can remove an original before its sibling replacement resolves on another peer.
            // Suppress all legacy echoes for this Apply and repair from an authoritative snapshot.
            _cachedFallbackOriginalEdges.Clear();
            _cachedFallbackOriginalEdges.AddRange(rejectedOriginalEdges);
            _cachedNeedsFinalEdgeFallback = true;

            Mod.log.Warn("[MP] NetSync: local mixed net operation cannot be encoded atomically (" +
                         rejected + " of " + (rejected + next.Count) + " courses " + rejection +
                         "; " + hiddenSubNets + " generated connector(s) skipped); the legacy " +
                         "fragmented fallback is disabled and world recovery will be requested " +
                         "after Apply.");
            Diagnostics.FlightRecorder.Note("net native capture voided rejected=" + rejected + "/" +
                                              (rejected + next.Count) +
                                              " hiddenSubNets=" + hiddenSubNets);
        }

        /// <summary>
        /// Publish the exact cached courses when the local net tool actually applies its standing
        /// preview. Called before ToolOutputSystem, while the preview Temps still expose every
        /// original edge the operation will replace as a split side effect.
        /// </summary>
        public void CaptureLocalNetApply()
        {
            CaptureLocalNetApply(refreshStandingDefinitions: true, barrierRecovery: false);
        }

        /// <summary>
        /// Last-chance Apply capture after <see cref="global::Game.Tools.ToolOutputBarrier"/> has made
        /// this frame's buffered definitions entity-visible. <see cref="DefinitionGateSystem"/> has
        /// already passed those exact definitions to <see cref="ObserveLocalNetDefinitions"/>, so do
        /// not replace them with the older untagged standing graph here.
        /// </summary>
        public void CaptureBufferedLocalNetApply()
        {
            CaptureLocalNetApply(refreshStandingDefinitions: false, barrierRecovery: true);
        }

        private void CaptureLocalNetApply(bool refreshStandingDefinitions, bool barrierRecovery)
        {
            MultiplayerService service = Mod.Service;
            if (service == null || !service.GameplaySyncReady ||
                _nativeApplyCapturedFrame == _realizeFrame)
                return;

            global::Game.Tools.ToolBaseSystem active = _toolSystem != null ? _toolSystem.activeTool : null;
            if (!(active is global::Game.Tools.NetToolSystem) ||
                active.applyMode != global::Game.Tools.ApplyMode.Apply) return;

            // Re-read the graph that is actually standing behind this Apply. The after-barrier cache
            // is intentionally retained across empty preview frames, but a grid can regenerate all of
            // its courses on the click frame. Publishing a stale or partial cache makes the final-edge
            // fallback replay every generated edge as a separate operation. A net-owned object graph
            // (for example a network prefab with its own root object) clears the course cache here and
            // is captured atomically by BuildSyncSystem instead.
            if (refreshStandingDefinitions && !_standingLocalDefinitions.IsEmptyIgnoreFilter)
            {
                NativeArray<Entity> definitions =
                    _standingLocalDefinitions.ToEntityArray(Allocator.Temp);
                try
                {
                    ObserveLocalNetDefinitions(definitions);
                }
                finally
                {
                    definitions.Dispose();
                }
            }

            if (_cachedLocalMixedOperation.Count > 0)
            {
                CaptureAtomicMixedNetApply(service, barrierRecovery);
                return;
            }
            if (_cachedLocalCourses.Count == 0)
            {
                if (!_cachedNeedsFinalEdgeFallback) return;
                RecordPlacementOriginals(service.NowMs);
                _atomicMixedOriginals.Clear();
                _atomicMixedOriginalsFrame = _realizeFrame;
                for (int i = 0; i < _cachedFallbackOriginalEdges.Count; i++)
                    _atomicMixedOriginals.Add(_cachedFallbackOriginalEdges[i]);
                service.RequestAutomaticWorldRecovery(
                    "mixed road operation could not be encoded atomically");
                Diagnostics.FlightRecorder.Note("net mixed capture rejected; recovery requested" +
                    (barrierRecovery ? " source=barrier" : string.Empty));
                _cachedFallbackOriginalEdges.Clear();
                _cachedNeedsFinalEdgeFallback = false;
                _nativeApplyCapturedFrame = _realizeFrame;
                _atomicMixedApplyCapturedFrame = _realizeFrame;
                return;
            }

            // When a remote batch is armed, BeginRealizeFrame has already Disabled its Temps and
            // restored the local preview for this Apply frame. The local operation therefore commits
            // normally and must still be published; the remote batch waits intact for a quiet frame.

            long now = service.NowMs;
            RecordPlacementOriginals(now);

            // A span over water divides into the pieces that reproduce the deck committing here;
            // everything else passes through unchanged. Measured once, at publish.
            ExpandWaterProfilePins(_cachedLocalCourses);

            long operationId = _nextLocalNetOperationId++;
            if (_nextLocalNetOperationId <= 0) _nextLocalNetOperationId = 1;
            int count = _cachedLocalCourses.Count;
            var encoded = new List<byte[]>(count);
            try
            {
                // Encode the complete operation before sending its first course. A locally unusual
                // definition then falls back as a whole to final-edge capture instead of publishing
                // a partial native operation.
                for (int i = 0; i < count; i++)
                {
                    NetPlacementCommand command = _cachedLocalCourses[i];
                    command.OperationId = operationId;
                    command.CourseIndex = (short)i;
                    command.CourseCount = (short)count;
                    encoded.Add(command.Encode());
                }
            }
            catch (System.Exception ex)
            {
                _cachedLocalCourses.Clear();
                Mod.log.Warn("[MP] NetSync intent capture could not encode operation; " +
                             "using final-edge capture: " + ex.Message);
                return;
            }

            int sent = 0;
            for (int i = 0; i < count; i++)
            {
                NetPlacementCommand command = _cachedLocalCourses[i];
                try
                {
                    service.Session.SendCommand(0, NetPlacementCommand.Id, encoded[i]);
                    RecordDiagnostic(command.PrefabName);
                    sent++;
                }
                catch (System.Exception ex)
                {
                    Mod.log.Warn("[MP] NetSync intent capture dropped course " + i + "/" + count +
                                 ": " + ex.Message);
                }
            }

            _cachedLocalCourses.Clear();
            // A partial native envelope is deliberately not considered captured. The receiver will
            // expire those fragments as one incomplete operation, while final-edge capture remains
            // enabled to provide a complete geometry fallback for this local apply.
            if (sent == count) _nativeApplyCapturedFrame = _realizeFrame;
            if (sent > 0)
                Diagnostics.FlightRecorder.Note("net intent apply op=" + operationId + " courses=" +
                                                  sent + "/" + count +
                                                  (barrierRecovery ? " source=barrier" : string.Empty));
        }

        /// <summary>
        /// Publish every native definition from one heterogeneous net-tool Apply as one command.
        /// The receiver can then resolve deletes/replacements against its original topology and arm
        /// all generated Temps together; no independent capture stream is allowed to echo members.
        /// </summary>
        private void CaptureAtomicMixedNetApply(MultiplayerService service, bool barrierRecovery)
        {
            long now = service.NowMs;
            RecordPlacementOriginals(now);
            long sideEffectExpiry = now + CommittedSideEffectWindowMs;
            _atomicMixedOriginals.Clear();
            _atomicMixedOriginalsFrame = _realizeFrame;
            for (int i = 0; i < _cachedLocalMixedOperation.Count; i++)
            {
                LocalNetToolOperationItem local = _cachedLocalMixedOperation[i];
                Entity original = local.Original;
                if (original != Entity.Null && EntityManager.Exists(original) &&
                    EntityManager.HasComponent<Edge>(original))
                {
                    _atomicMixedOriginals.Add(original);
                    if (local.CommandId == NetDeleteCommand.Id)
                        _committedNetSideEffects[original] = sideEffectExpiry;
                }
            }

            ExpandWaterProfilePins(_cachedLocalMixedOperation);

            long operationId = _nextLocalNetOperationId++;
            if (_nextLocalNetOperationId <= 0) _nextLocalNetOperationId = 1;

            int itemCount = _cachedLocalMixedOperation.Count;
            int placementCount = 0;
            for (int i = 0; i < itemCount; i++)
                if (_cachedLocalMixedOperation[i].CommandId == NetPlacementCommand.Id)
                    placementCount++;

            bool sent = false;
            try
            {
                var items = new NetToolOperationItem[itemCount];
                int placementIndex = 0;
                for (int i = 0; i < itemCount; i++)
                {
                    LocalNetToolOperationItem local = _cachedLocalMixedOperation[i];
                    byte[] body;
                    switch (local.CommandId)
                    {
                        case NetPlacementCommand.Id:
                            local.Placement.OperationId = operationId;
                            local.Placement.CourseIndex = (short)placementIndex++;
                            local.Placement.CourseCount = (short)placementCount;
                            body = local.Placement.Encode();
                            break;
                        case NetDeleteCommand.Id:
                            body = local.Delete.Encode();
                            break;
                        case NetReplaceCommand.Id:
                            body = local.Replace.Encode();
                            break;
                        default:
                            throw new System.InvalidOperationException(
                                "Unsupported cached atomic net command " + local.CommandId + ".");
                    }
                    items[i] = new NetToolOperationItem
                    {
                        CommandId = local.CommandId,
                        Body = body,
                    };
                }

                var operation = new NetToolOperationCommand
                {
                    OperationId = operationId,
                    Items = items,
                };
                service.Session.SendCommand(0, NetToolOperationCommand.Id, operation.Encode());
                sent = true;

                for (int i = 0; i < itemCount; i++)
                {
                    NetPlacementCommand placement = _cachedLocalMixedOperation[i].Placement;
                    if (placement != null) RecordDiagnostic(placement.PrefabName);
                }
            }
            catch (System.Exception ex)
            {
                // A fragmented fallback is exactly the failure this envelope prevents. Suppress
                // every legacy echo even when encoding/sending fails, and repair the peer from a
                // world snapshot instead of racing delete/replace/place streams again.
                Mod.log.Warn("[MP] NetSync atomic mixed apply could not be sent: " + ex.Message);
                service.RequestAutomaticWorldRecovery("atomic mixed road operation could not be sent");
            }
            finally
            {
                _cachedLocalMixedOperation.Clear();
                _cachedLocalCourses.Clear();
                _cachedFallbackOriginalEdges.Clear();
                _cachedNeedsFinalEdgeFallback = false;
                _nativeApplyCapturedFrame = _realizeFrame;
                _atomicMixedApplyCapturedFrame = _realizeFrame;
            }

            Diagnostics.FlightRecorder.Note("net atomic mixed apply op=" + operationId +
                                              " items=" + itemCount +
                                              " status=" + (sent ? "sent" : "recovery") +
                                              (barrierRecovery ? " source=barrier" : string.Empty));
        }

        /// <summary>
        /// Consume an exact original edge recorded from a committing Temp transaction. DeleteSync
        /// calls this before its geometry heuristics; a match has already been represented by the
        /// placement/delete/replace command that caused it and must not become a second command.
        /// </summary>
        public bool ConsumeCommittedNetSideEffect(Entity edge, long now)
        {
            if (_atomicMixedOriginalsFrame == _realizeFrame &&
                _atomicMixedOriginals.Remove(edge)) return true;

            long expires;
            if (!_committedNetSideEffects.TryGetValue(edge, out expires)) return false;
            _committedNetSideEffects.Remove(edge);
            return expires >= now;
        }

        /// <summary>
        /// A course the game generated as the hidden connector of some owner - a building driveway,
        /// a lot path - rather than something the player drew. Both the new prefab and the original
        /// it re-cuts are checked, because an edit beside a building produces one definition per
        /// affected connector in either form. The receiver regenerates these from the visible net,
        /// so they are skipped rather than transmitted or treated as unrepresentable.
        /// </summary>
        private bool IsGeneratedHiddenSubNet(CreationDefinition definition)
        {
            if (IsHiddenNetPrefab(definition.m_Prefab)) return true;

            Entity original = definition.m_Original;
            if (original == Entity.Null || !EntityManager.Exists(original) ||
                !EntityManager.HasComponent<PrefabRef>(original)) return false;
            return IsHiddenNetPrefab(EntityManager.GetComponentData<PrefabRef>(original).m_Prefab);
        }

        private bool IsHiddenNetPrefab(Entity prefab)
        {
            if (prefab == Entity.Null) return false;
            string name = PrefabNameOf(prefab);
            return !string.IsNullOrEmpty(name) && name.StartsWith("Invisible");
        }

        private static bool IsPlainLocalNetDefinition(CreationDefinition definition)
        {
            const CreationFlags incompatible = CreationFlags.Permanent | CreationFlags.Delete |
                CreationFlags.Upgrade | CreationFlags.Relocate | CreationFlags.Recreate |
                CreationFlags.Repair | CreationFlags.Duplicate;
            return definition.m_Original == Entity.Null && definition.m_Owner == Entity.Null &&
                   definition.m_Attached == Entity.Null && (definition.m_Flags & incompatible) == 0;
        }
    }
}
