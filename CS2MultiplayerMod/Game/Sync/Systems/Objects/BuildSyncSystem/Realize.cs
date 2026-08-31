using System.Text;
using Colossal.Mathematics;
using Game.Common;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Sync.Infrastructure;
using CS2MultiplayerMod.Game.Sync.Commands;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    // Realizing a remote object placement: drain the queue, retry the ones whose attachment target
    // has not arrived, and place the rest.
    //
    // Recognising an object this peer already has, and finding what a placement attaches to, is in
    // RealizeMatch.cs. Creating the object and the lot, areas and sub-nets that come with it is in
    // RealizeOwned.cs and RealizeSubNets.cs.
    public partial class BuildSyncSystem
    {
        /// <summary>Attach-node position match tolerance, squared metres (2 m XZ).</summary>
        private const float AttachNodeTolSq = 4f;

        /// <summary>Never attach to a node stacked on another level (bridge over junction).</summary>
        private const float AttachNodeMaxDy = 4f;

        /// <summary>How far (metres, 3D) an anchor may sit off an edge's centreline to match it.</summary>
        private const float AttachEdgeTol = 2f;

        /// <summary>
        /// Ceiling on object spawns per frame. A human's placement rate is a few per second; a
        /// burst beyond this (a flood, or a backlog draining after a stall) would materialise many
        /// buildings plus their lot/net sub-definitions in ONE Modification pass — a load shape the
        /// game's own tools never produce. The rest stay queued for the following frames.
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

            // What these windows wait for is a ROAD - the attachment parent below says so in as
            // many words - and roads are exactly what the realize pipeline holds back while
            // terrain or the net commit catches up. Spending the window during that hold expires
            // it against a parent that could not have arrived, and the expiry asks for a full
            // world reload. Below, the same three conditions skip the attempt entirely.
            long heldMs = _targetHold.Observe(now,
                RealizeGate.WorldBuildingHeld || DeferForTerrain ||
                _nativeNetCoordinator.IsCommitBusy);
            if (heldMs > 0)
            {
                for (int h = 0; h < _attachRetry.Count; h++)
                    _attachRetry[h] = (_attachRetry[h].command, _attachRetry[h].prefab,
                        _attachRetry[h].originPlayerId, _attachRetry[h].deadline + heldMs);
                if (_hasBlockedNativeObject) _blockedNativeObjectDeadline += heldMs;
            }

            PruneNativeObjectOperations(now);
            if (_nativeNetCoordinator.IsCommitBusy) return;
            if (!TryRealizeBlockedNativeObject(now)) return;

            _rzFrameSpawned = 0;
            _rzFrameDuplicates = 0;
            _rzRealizedThisFrame.Clear();
            try
            {
                if (!DeferForTerrain)
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
                    Diagnostics.FlightRecorder.Note(note.ToString());
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
            SimulationCommandMessage message;
            while (_rzFrameSpawned < MaxRealizePerFrame && TryTakeNextObjectMessage(out message))
            {
                // Our own placement coming back to us — already built locally.
                if (message.OriginPlayerId == session.LocalPlayerId) continue;

                if (message.CommandId == ObjectToolOperationCommand.Id ||
                    message.CommandId == AssetStampCommand.Id)
                {
                    Diagnostics.FlightRecorder.Note("object command received origin=" +
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

                ObjectPlacementCommand command;
                try { command = ObjectPlacementCommand.Decode(message.Body); }
                catch (System.Exception ex) { Mod.log.Warn("[MP] BuildSync: dropping malformed command: " + ex.Message); continue; }

                Entity prefab;
                if (!_prefabIndex.TryResolve(command.PrefabName,
                        candidate => EntityManager.HasComponent<ObjectData>(candidate),
                        out prefab))
                {
                    Mod.log.Warn("[MP] BuildSync realize: unknown prefab '" + command.PrefabName +
                                 "' from player " + message.OriginPlayerId + "; skipping.");
                    continue;
                }

                // A standalone definition cannot establish the ownership links required by
                // movers, and zone growables are created by the zoning simulation rather than
                // a player placement. Refuse both before any game definition is allocated.
                if (IsSimulationOnlyPlacementPrefab(prefab))
                {
                    RecordRefused(command.PrefabName);
                    continue;
                }
                if (RequiresCompleteObjectLifecycle(prefab))
                {
                    // A reduced command can't represent a building's owned graph; the native
                    // object-tool path owns those. This should not be emitted by v38 senders; if it
                    // arrives, recover rather than silently accepting a missing building.
                    Mod.log.Warn("[MP] BuildSync realize: reduced placement for spatial object '" +
                                 command.PrefabName +
                                 "' was rejected; requesting world recovery.");
                    SyncInbox.RequestResync(CS2MultiplayerMod.Game.Diagnostics.ResyncReport
                        .Create("reduced spatial object placement rejected", "object",
                            CS2MultiplayerMod.Game.Diagnostics.ResyncEvidence.Contradiction)
                        .About("reduced spatial placement")
                        .Tried("nothing - the reduced form of this placement cannot be committed here"));
                    continue;
                }

                // A net object placed on a road that has not reached us yet has nothing to hang off.
                // Placing it now would strand it as an inert prop, so wait for the road instead.
                if (command.AttachKind != ObjectAttachKind.None && FindAttachTarget(command) == Entity.Null)
                {
                    if (_attachRetry.Count >= MaxPendingAttachments)
                    {
                        _attachRetry.Clear();
                        Mod.log.Warn("[MP] BuildSync: attachment retry queue overflowed; dropping the " +
                                     "incomplete backlog and requesting world recovery.");
                        Diagnostics.FlightRecorder.Note(
                            "attachment retry queue overflow; recovery requested");
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
                    // The parent road never reached us. The prop cannot safely be created without
                    // it, but silently dropping it leaves known divergence.
                    _attachRetry.RemoveAt(i);
                    Mod.log.Warn("[MP] BuildSync realize: no local road for '" + pending.command.PrefabName +
                                 "' after " + (AttachRetryWindowMs / 1000) +
                                 " s; requesting world recovery.");
                    Diagnostics.FlightRecorder.Note(
                        "attachment target expired; recovery requested");
                    SyncInbox.RequestResync(CS2MultiplayerMod.Game.Diagnostics.ResyncReport
                        .Create("object attachment target did not resolve", "object",
                            CS2MultiplayerMod.Game.Diagnostics.ResyncEvidence.MissingTarget)
                        .About("attachment parent road")
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

            // The same placement arriving twice (a replayed message, a lagged echo) would stack a
            // second building exactly inside the first — geometry the sender's own validation can
            // never produce, and native systems don't tolerate what the tools forbid.
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
                Mod.Verbose("[MP] BuildSync realize: spawned '" + command.PrefabName + "' from player " +
                            originPlayerId + " at (" + position.x.ToString("F1") + "," +
                            position.z.ToString("F1") + ").");
            }
            catch (System.Exception ex)
            {
                Mod.log.Error("[MP] BuildSync realize FAILED for '" + command.PrefabName + "': " + ex);
                Diagnostics.FlightRecorder.Note("build realize FAILED '" + command.PrefabName + "': "
                    + ex.GetType().Name + "; recovery requested");
                SyncInbox.RequestResync(CS2MultiplayerMod.Game.Diagnostics.ResyncReport
                    .Create("object placement realization failed", "object",
                        CS2MultiplayerMod.Game.Diagnostics.ResyncEvidence.Contradiction)
                    .About("object placement")
                    .Tried("nothing - realization threw and the placement was rolled back"));
            }
        }
    }
}
