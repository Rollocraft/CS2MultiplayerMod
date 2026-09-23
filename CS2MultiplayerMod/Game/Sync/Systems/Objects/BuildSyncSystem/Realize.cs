using System.Text;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Infrastructure;
using CS2MultiplayerMod.Game.Sync.Commands;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    public partial class BuildSyncSystem
    {
        /// <summary>Attach-node position match tolerance, squared metres (2 m XZ).</summary>
        private const float AttachNodeTolSq = 4f;

        /// <summary>Never attach to a node stacked on another level (bridge over junction).</summary>
        private const float AttachNodeMaxDy = 4f;

        /// <summary>How far (metres, 3D) an anchor may sit off an edge's centreline to match it.</summary>
        private const float AttachEdgeTol = 2f;

        /// <summary>
        /// Spawns per frame; a burst beyond this would put a load shape into one Modification pass that the
        /// game's tools never produce. The rest wait.
        /// </summary>
        private const int MaxRealizePerFrame = 8;

        /// <summary>Broad search radius for replay candidates. Nearness alone is not identity.</summary>
        private const float DuplicateRadiusSq = 1.5f * 1.5f;
        private const float DuplicateMaxDy = 3f;
        /// <summary>One centimetre squared: an exact overlap is a real simultaneous conflict.</summary>
        private const float ExactDuplicateDistanceSq = 0.0001f;

        private int _rzFrameSpawned;
        private int _rzFrameDuplicates;
        private readonly System.Collections.Generic.List<
            (Entity prefab, float3 position, int randomSeed, quaternion rotation,
                ObjectAttachKind attachKind)> _rzRealizedThisFrame =
            new System.Collections.Generic.List<
                (Entity, float3, int, quaternion, ObjectAttachKind)>();
        private NativeArray<Entity> _dupEntities;
        private NativeArray<global::Game.Objects.Transform> _dupTransforms;
        private NativeArray<PrefabRef> _dupPrefabs;
        private bool _dupSnapshotTaken;

        private readonly HeldTime _targetHold = new HeldTime();

        private void RealizeIncoming(MultiplayerSession session, long now)
        {
            if (_incoming.IsEmpty && _nativeObjectReplayPrefix.Count == 0 &&
                _attachRetry.Count == 0 && !_hasBlockedNativeObject) return;

            // These windows wait for roads, which the pipeline holds back; held time does not count.
            long heldMs = _targetHold.Observe(now,
                RealizeGate.WorldBuildingHeld || _nativeNetCoordinator.IsCommitBusy);
            if (heldMs > 0)
            {
                for (int h = 0; h < _attachRetry.Count; h++)
                    _attachRetry[h] = (_attachRetry[h].command, _attachRetry[h].prefab,
                        _attachRetry[h].originPlayerId, _attachRetry[h].deadline + heldMs);
                if (_hasBlockedNativeObject) _blockedNativeObjectDeadline += heldMs;
            }

            PruneNativeObjectOperations(now);
            if (_nativeNetCoordinator.IsCommitBusy) return;

            _rzFrameSpawned = 0;
            _rzFrameBatchedObjects = 0;
            _rzFrameDuplicates = 0;
            _rzRealizedThisFrame.Clear();
            try
            {
                if (!TryRealizeBlockedNativeObject(now)) return;

                // A transmitted Y assumes the sender's terrain.
                if (!RealizeGate.TerrainBacklog)
                {
                    RetryPendingAttachments(now);
                    DrainIncoming(session, now);
                }

                if (_rzFrameSpawned > 0 || _rzFrameDuplicates > 0)
                {
                    var note = new StringBuilder("build realize n=").Append(_rzFrameSpawned);
                    if (_rzFrameDuplicates > 0) note.Append(" dup=").Append(_rzFrameDuplicates);
                    int held = _incoming.Count + _nativeObjectReplayPrefix.Count;
                    if (held > 0) note.Append(" held=").Append(held);
                    AppendRealizedNames(note);
                    SyncLog.Trace(LogTopic.Buildings, note.ToString());
                }
            }
            finally
            {
                if (_dupSnapshotTaken)
                {
                    _dupEntities.Dispose();
                    _dupTransforms.Dispose();
                    _dupPrefabs.Dispose();
                    _dupSnapshotTaken = false;
                }
            }
        }

        private void DrainIncoming(MultiplayerSession session, long now)
        {
            while (TryTakeNextObjectMessage(out SimulationCommandMessage message))
            {
                // Our own placement coming back to us — already built locally.
                if (message.OriginPlayerId == session.LocalPlayerId) continue;

                if (message.CommandId == ObjectPlacementBatchCommand.Id)
                {
                    if (TryRealizeObjectPlacementBatch(message, now)) continue;
                    _nativeObjectReplayPrefix.Insert(0, message);
                    break;
                }

                if (message.CommandId == ObjectToolOperationCommand.Id ||
                    message.CommandId == AssetStampCommand.Id)
                {
                    SyncLog.Trace(LogTopic.Buildings, "object command received origin=" +
                        message.OriginPlayerId);
                    NativeObjectResult result = TryRealizeRemoteObjectMessage(message, now);
                    if (result == NativeObjectResult.Retry)
                    {
                        BlockNativeObject(message, now);
                        break;
                    }
                    if (result == NativeObjectResult.Armed) break;
                    continue;
                }

                // Brush batches are checked above: each is one indivisible frame.
                if (_rzFrameSpawned >= MaxRealizePerFrame)
                {
                    _nativeObjectReplayPrefix.Insert(0, message);
                    break;
                }

                if (!CommandDecode.TryDecode(message, ObjectPlacementCommand.Decode, LogTopic.Buildings,
                        "BuildSync", out ObjectPlacementCommand command))
                    continue;

                if (!_prefabIndex.TryResolve(command.PrefabName,
                        candidate => EntityManager.HasComponent<ObjectData>(candidate),
                        out Entity prefab))
                {
                    SyncLog.Warn(LogTopic.Buildings, "BuildSync realize: unknown prefab '" +
                        command.PrefabName + "' from player " + message.OriginPlayerId +
                        "; skipping.");
                    continue;
                }

                // A standalone definition cannot link movers, and zone growables are the simulation's.
                if (IsSimulationOnlyPlacementPrefab(prefab))
                {
                    RecordRefused(command.PrefabName);
                    continue;
                }
                if (RequiresCompleteObjectLifecycle(prefab))
                {
                    // A reduced command cannot carry a building's owned graph; recover rather than accept a gap.
                    SyncLog.Warn(LogTopic.Buildings,
                        "BuildSync realize: reduced placement for spatial object '" +
                        command.PrefabName + "' was rejected; requesting world recovery.");
                    SyncInbox.RequestResync(CS2MultiplayerMod.Game.Diagnostics.ResyncReport
                        .Create("reduced spatial object placement rejected", "object",
                            CS2MultiplayerMod.Game.Diagnostics.ResyncEvidence.Contradiction)
                        .About("reduced spatial placement")
                        .Tried("nothing - the reduced form of this placement cannot be committed here"));
                    continue;
                }

                // A net object without its road would be an inert prop; wait for the road.
                if (command.AttachKind != ObjectAttachKind.None && FindAttachTarget(command) == Entity.Null)
                {
                    if (_attachRetry.Count >= MaxPendingAttachments)
                    {
                        _attachRetry.Clear();
                        SyncLog.Warn(LogTopic.Buildings,
                            "BuildSync: attachment retry queue overflowed; dropping the " +
                            "incomplete backlog and requesting world recovery.");
                        SyncInbox.RequestResync(CS2MultiplayerMod.Game.Diagnostics.ResyncReport
                            .Create("object attachment retry queue overflow", "object",
                                CS2MultiplayerMod.Game.Diagnostics.ResyncEvidence.StreamLoss)
                            .About("attachment retry queue")
                            .Tried("nothing - the queue was full and was cleared"));
                        return;
                    }
                    _attachRetry.Add((command, prefab, message.OriginPlayerId, now + AttachRetryWindowMs));
                    continue;
                }

                RealizeCommand(command, prefab, message.OriginPlayerId, now);
            }
        }

        private bool TryTakeNextObjectMessage(out SimulationCommandMessage message)
        {
            if (_nativeObjectReplayPrefix.Count > 0)
            {
                message = _nativeObjectReplayPrefix[0];
                _nativeObjectReplayPrefix.RemoveAt(0);
                return true;
            }
            return _incoming.TryDequeue(out message);
        }

        /// <summary>Re-attempt net objects whose parent node was missing; give up after the window.</summary>
        private void RetryPendingAttachments(long now)
        {
            for (int i = _attachRetry.Count - 1; i >= 0; i--)
            {
                if (_rzFrameSpawned >= MaxRealizePerFrame) return; // budget spent; retry next frame
                var pending = _attachRetry[i];

                if (FindAttachTarget(pending.command) != Entity.Null)
                {
                    _attachRetry.RemoveAt(i);
                    RealizeCommand(pending.command, pending.prefab, pending.originPlayerId, now);
                }
                else if (now >= pending.deadline)
                {
                    // The parent road never arrived; dropping silently would leave known divergence.
                    _attachRetry.RemoveAt(i);
                    SyncLog.Warn(LogTopic.Buildings, "BuildSync realize: no local road for '" +
                        pending.command.PrefabName + "' after " + (AttachRetryWindowMs / 1000) +
                        " s; requesting world recovery.");
                    SyncInbox.RequestResync(CS2MultiplayerMod.Game.Diagnostics.ResyncReport
                        .Create("object attachment target did not resolve", "object",
                            CS2MultiplayerMod.Game.Diagnostics.ResyncEvidence.MissingTarget)
                        .About("parent road for '" + pending.command.PrefabName + "' at (" +
                               pending.command.PosX.ToString("F1") + "," +
                               pending.command.PosZ.ToString("F1") + ")")
                        .Tried("waited 10 s of attempts for the parent road, not counting time the road pipeline was held"));
                }
            }
        }

        private void RealizeCommand(ObjectPlacementCommand command, Entity prefab, int originPlayerId, long now)
        {
            var position = new float3(command.PosX, command.PosY, command.PosZ);
            var rotation = new quaternion(math.normalizesafe(
                new float4(command.RotX, command.RotY, command.RotZ, command.RotW),
                new float4(0f, 0f, 0f, 1f)));

            // A replayed placement would stack a second building inside the first.
            if (AlreadyStandsAt(command, prefab, position, rotation))
            {
                _rzFrameDuplicates++;
                return;
            }

            Entity attachParent = FindAttachTarget(command);

            // Remember it so our own detector treats the soon-to-appear object as a replica.
            _guard.Mark(ReplicationGuard.Key(command.PrefabName, position), now);
            try
            {
                RealizeObject(prefab, position, rotation, attachParent,
                    command.RandomSeed, command.Age);
                ConstructionCharger.ChargeObject(EntityManager, prefab, command.PrefabName);
                _rzFrameSpawned++;
                _rzRealizedThisFrame.Add((prefab, position, command.RandomSeed, rotation,
                    command.AttachKind));
                if (!_suppressBatchedObjectDetail)
                    SyncLog.Detail(LogTopic.Buildings, "BuildSync realize: spawned '" +
                        command.PrefabName + "' from player " + originPlayerId + " at (" +
                        position.x.ToString("F1") + "," + position.z.ToString("F1") + ").");
            }
            catch (System.Exception ex)
            {
                SyncLog.Error(LogTopic.Buildings, "BuildSync realize FAILED for '" +
                    command.PrefabName + "': " + ex);
                SyncInbox.RequestResync(CS2MultiplayerMod.Game.Diagnostics.ResyncReport
                    .Create("object placement realization failed", "object",
                        CS2MultiplayerMod.Game.Diagnostics.ResyncEvidence.Contradiction)
                    .About("object placement")
                    .Tried("nothing - realization threw and the placement was rolled back"));
            }
        }
    }
}
