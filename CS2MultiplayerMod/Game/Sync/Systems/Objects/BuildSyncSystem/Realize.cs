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

        /// <summary>
        /// True when a live same-prefab object (or one spawned earlier this frame) has the same
        /// replay identity inside <see cref="DuplicateRadiusSq"/>, or occupies the exact transform.
        /// The world snapshot is taken once per frame, only on frames that realize something.
        /// </summary>
        private bool AlreadyStandsAt(ObjectPlacementCommand command, Entity prefab,
            float3 position, quaternion rotation)
        {
            for (int i = 0; i < _rzRealizedThisFrame.Count; i++)
            {
                if (_rzRealizedThisFrame[i].prefab != prefab ||
                    _rzRealizedThisFrame[i].attachKind != command.AttachKind) continue;
                float3 p = _rzRealizedThisFrame[i].position;
                float rotationDot = math.abs(math.dot(
                    _rzRealizedThisFrame[i].rotation.value, rotation.value));
                // If both players chose the identical transform before seeing one another, keeping
                // one object is safer than bypassing the game's overlap validation and stacking two.
                if (math.distancesq(p, position) <= ExactDuplicateDistanceSq &&
                    (command.AttachKind != ObjectAttachKind.None || rotationDot >= 0.99999f))
                    return true;
                if (math.distancesq(p.xz, position.xz) >= DuplicateRadiusSq ||
                    math.abs(p.y - position.y) > DuplicateMaxDy ||
                    unchecked((ushort)_rzRealizedThisFrame[i].randomSeed) !=
                    unchecked((ushort)command.RandomSeed)) continue;
                if (command.AttachKind == ObjectAttachKind.None && rotationDot < 0.9999f) continue;
                return true;
            }

            if (!_dupSnapshotTaken)
            {
                _dupEntities = _liveStaticObjects.ToEntityArray(Allocator.Temp);
                _dupTransforms = _liveStaticObjects.ToComponentDataArray<global::Game.Objects.Transform>(Allocator.Temp);
                _dupPrefabs = _liveStaticObjects.ToComponentDataArray<PrefabRef>(Allocator.Temp);
                _dupSnapshotTaken = true;
            }
            for (int i = 0; i < _dupTransforms.Length; i++)
            {
                if (_dupPrefabs[i].m_Prefab != prefab) continue;
                float3 p = _dupTransforms[i].m_Position;
                if (math.distancesq(p.xz, position.xz) >= DuplicateRadiusSq ||
                    math.abs(p.y - position.y) > DuplicateMaxDy) continue;

                Entity candidate = _dupEntities[i];
                bool attached = EntityManager.HasComponent<global::Game.Objects.Attached>(candidate);
                if ((command.AttachKind != ObjectAttachKind.None) != attached) continue;
                if (attached)
                {
                    Entity parent = EntityManager
                        .GetComponentData<global::Game.Objects.Attached>(candidate).m_Parent;
                    bool parentMatchesKind = parent != Entity.Null && EntityManager.Exists(parent) &&
                        (command.AttachKind == ObjectAttachKind.NetNode
                            ? EntityManager.HasComponent<global::Game.Net.Node>(parent)
                            : EntityManager.HasComponent<global::Game.Net.Edge>(parent));
                    if (!parentMatchesKind) continue;
                }

                float rotationDot = attached ? 1f : math.abs(math.dot(
                    math.normalizesafe(_dupTransforms[i].m_Rotation.value,
                        new float4(0f, 0f, 0f, 1f)), rotation.value));
                if (math.distancesq(p, position) <= ExactDuplicateDistanceSq &&
                    rotationDot >= 0.99999f) return true;

                // Proximity is not enough to prove a replay. If the standing entity has no variant
                // identity, keep the new command instead of suppressing a legitimate close build.
                if (!EntityManager.HasComponent<PseudoRandomSeed>(candidate) ||
                    unchecked((ushort)EntityManager
                        .GetComponentData<PseudoRandomSeed>(candidate).m_Seed) !=
                    unchecked((ushort)command.RandomSeed)) continue;

                // Attached props can be rotated by the attachment pass after placement. Their
                // prefab, variant seed and bounded position still form the replay identity. For
                // free-standing props, require the original orientation as well so two intentional
                // close placements are not mistaken for one another.
                if (!attached && rotationDot < 0.9999f) continue;
                return true;
            }
            return false;
        }

        // Prefab-name digest for the per-frame flight note, e.g. " [WaterPumpingStation x3]".
        private void AppendRealizedNames(StringBuilder note)
        {
            if (_rzRealizedThisFrame.Count == 0) return;
            note.Append(" [");
            int written = 0;
            for (int i = 0; i < _rzRealizedThisFrame.Count; i++)
            {
                Entity prefab = _rzRealizedThisFrame[i].prefab;
                bool seen = false;
                int count = 0;
                for (int j = 0; j < _rzRealizedThisFrame.Count; j++)
                {
                    if (_rzRealizedThisFrame[j].prefab != prefab) continue;
                    if (j < i) { seen = true; break; }
                    count++;
                }
                if (seen) continue;
                if (written > 0) note.Append(", ");
                note.Append(_prefabSystem.GetPrefabName(prefab));
                if (count > 1) note.Append(" x").Append(count);
                written++;
            }
            note.Append(']');
        }

        /// <summary>The local net entity this command's object hangs off, or Null (also when unattached).</summary>
        private Entity FindAttachTarget(ObjectPlacementCommand command)
        {
            return ResolveNetAttachment(command.AttachKind,
                new float3(command.AttachX, command.AttachY, command.AttachZ));
        }

        /// <summary>
        /// Resolve a portable node/edge anchor for placement and relocation commands. Keeping one
        /// resolver ensures both paths make the same choice when roads are subdivided differently.
        /// </summary>
        internal Entity ResolveNetAttachment(ObjectAttachKind kind, float3 anchor)
        {
            switch (kind)
            {
                case ObjectAttachKind.NetNode: return FindAttachNode(anchor);
                case ObjectAttachKind.NetEdge: return FindAttachEdge(anchor);
                default: return Entity.Null;
            }
        }

        /// <summary>
        /// The live edge whose centreline passes closest to <paramref name="anchor"/>. The anchor sits
        /// exactly on the sender's parent centreline, so a receiver that subdivided the road differently
        /// still finds the piece under it; 3D distance keeps a bridge overhead from winning.
        /// </summary>
        private Entity FindAttachEdge(float3 anchor)
        {
            Entity best = Entity.Null, bestOwned = Entity.Null;
            float bestDist = AttachEdgeTol, bestOwnedDist = AttachEdgeTol;

            NativeArray<Entity> entities = _liveEdges.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Bezier4x3 curve = EntityManager.GetComponentData<global::Game.Net.Curve>(entities[i]).m_Bezier;

                    // Solving for the closest point on a cubic is far too costly to run against every
                    // edge in the city; the control hull bounds the curve, so this rejects almost all.
                    Bounds3 bounds = MathUtils.Bounds(curve);
                    if (math.any(anchor < bounds.min - AttachEdgeTol) ||
                        math.any(anchor > bounds.max + AttachEdgeTol)) continue;

                    float t;
                    float dist = MathUtils.Distance(curve, anchor, out t);

                    // A driveway meets its road on the road's centreline, so right at that point the
                    // two tie. Road markings belong to the road, so an owned sub-net only ever wins
                    // when nothing else is in range.
                    if (EntityManager.HasComponent<Owner>(entities[i]))
                    {
                        if (dist >= bestOwnedDist) continue;
                        bestOwned = entities[i];
                        bestOwnedDist = dist;
                        continue;
                    }

                    if (dist >= bestDist) continue;
                    best = entities[i];
                    bestDist = dist;
                }
            }
            finally
            {
                entities.Dispose();
            }
            return best != Entity.Null ? best : bestOwned;
        }

        /// <summary>The live road node closest to <paramref name="wanted"/>, or Null when none is near.</summary>
        private Entity FindAttachNode(float3 wanted)
        {
            Entity best = Entity.Null;
            float bestDistSq = AttachNodeTolSq;

            NativeArray<Entity> entities = _liveNodes.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    float3 pos = EntityManager.GetComponentData<global::Game.Net.Node>(entities[i]).m_Position;
                    // A bridge node stacked over a junction sits at the same XZ - never match it.
                    if (math.abs(pos.y - wanted.y) > AttachNodeMaxDy) continue;

                    float distSq = math.distancesq(pos.xz, wanted.xz);
                    if (distSq >= bestDistSq) continue;
                    best = entities[i];
                    bestDistSq = distSq;
                }
            }
            finally
            {
                entities.Dispose();
            }
            return best;
        }

        /// <summary>
        /// Emit three definition entities (object + lot per SubArea + net per SubNet) linked by
        /// <see cref="OwnerDefinition"/>, with <see cref="CreationFlags.Permanent"/> for direct build.
        /// Must run in ToolUpdate (see <see cref="SyncRealizeSystem"/>). Fixes prior recipe: m_ParentMesh=-1
        /// ground marker, local transform, sub-definitions.
        ///
        /// <paramref name="attachParent"/> is the road node or edge a net object hangs off (Null
        /// otherwise). Permanent skips the tool's apply pass, so the parent is tagged here instead -
        /// see <see cref="NetAttachment"/>.
        /// </summary>
        private void RealizeObject(Entity prefab, float3 position, quaternion rotation, Entity attachParent,
            int randomSeed, float age, CreationFlags extraFlags = default(CreationFlags),
            bool simulationSpawn = false)
        {
            var random = new Unity.Mathematics.Random((uint)math.max(1, randomSeed));

            CreationFlags flags = CreationFlags.Permanent | extraFlags;
            if (attachParent != Entity.Null) flags |= CreationFlags.Attach;

            // 1) The building itself.
            Entity definition = EntityManager.CreateEntity();
            EntityManager.AddComponentData(definition, new CreationDefinition
            {
                m_Prefab = prefab,
                m_RandomSeed = randomSeed,
                m_Attached = attachParent,
                m_Flags = flags,
            });
            EntityManager.AddComponentData(definition, new ObjectDefinition
            {
                // -1 = sits on the ground (gets ElevationFlags.OnGround, no Elevation component);
                // any other value makes the game treat it as mesh-attached / elevated.
                m_ParentMesh = -1,
                m_Position = position,
                m_Rotation = rotation,
                // No owner, so local space == world space.
                m_LocalPosition = position,
                m_LocalRotation = rotation,
                m_Scale = new float3(1f, 1f, 1f),
                m_Intensity = 1f,
                m_Age = age,
                m_Probability = 100,
                m_PrefabSubIndex = -1,
            });
            EntityManager.AddComponent<Updated>(definition);
            EntityManager.AddComponent<Deleted>(definition); // CleanupSystem frees the definition once consumed.

            // 2) + 3) Sub-elements link back to the building by prefab + transform.
            var owner = new OwnerDefinition
            {
                m_Prefab = prefab,
                m_Position = position,
                m_Rotation = rotation,
            };
            RealizeOwnedSubElements(prefab, owner, ref random, simulationSpawn: simulationSpawn);

            // The composition that draws the ring, or applies the sign's restriction, is re-selected
            // only for Updated entities, and nothing else will tag them on this path. GenerateObjects
            // (M1) creates the object, AttachSystem (M3) files it under the parent, and
            // CompositionSelect reads it immediately after - all downstream of this ToolUpdate call.
            if (attachParent != Entity.Null) NetAttachment.TagParentUpdated(EntityManager, attachParent);
        }

        /// <summary>
        /// Builds a building the sending machine's zoning simulation grew. The spawner emits the
        /// same object definition a tool placement does - only the Construction flag differs, which
        /// is what puts it behind scaffolding instead of standing it up finished - but its owned
        /// connection nets follow a different recipe, so this path asks for that one (see
        /// <paramref name="simulationSpawn"/> on <see cref="RealizeSubNetCourse"/>).
        ///
        /// <paramref name="randomSeed"/> is the sender's variant seed and reaches the built entity
        /// as its PseudoRandomSeed, which is what makes the same house look the same on both
        /// machines. Called from ToolUpdate by <see cref="GrowableSyncSystem"/>.
        /// </summary>
        internal void RealizeSimulationBuilding(Entity prefab, float3 position, quaternion rotation,
            int randomSeed, bool underConstruction)
        {
            RealizeObject(prefab, position, rotation, Entity.Null, randomSeed, 0f,
                underConstruction ? CreationFlags.Construction : default(CreationFlags),
                simulationSpawn: true);
        }

        /// <summary>
        /// Emit a prefab's owned lot areas and connection nets.
        ///
        /// <paramref name="lotOwner"/> is the building whose lot surface the connection nets are laid
        /// on, or <see cref="Entity.Null"/> to lay them on the terrain. The tools pass the host
        /// building here for a service upgrade (the extension's paths belong on the host's lot) and
        /// nothing for a plain placement.
        /// </summary>
        internal void RealizeOwnedSubElements(Entity prefab, OwnerDefinition owner,
            ref Unity.Mathematics.Random random, Entity lotOwner = default(Entity),
            bool simulationSpawn = false)
        {
            RealizeSubAreas(prefab, owner, Entity.Null, ref random);
            RealizeSubNets(prefab, owner, Entity.Null, lotOwner, simulationSpawn, ref random);
        }

        internal void RealizeOwnedSubElements(Entity prefab, Entity ownerEntity,
            global::Game.Objects.Transform ownerTransform, ref Unity.Mathematics.Random random,
            Entity lotOwner = default(Entity))
        {
            PrefabRef ownerPrefab = EntityManager.GetComponentData<PrefabRef>(ownerEntity);
            var owner = new OwnerDefinition
            {
                m_Prefab = ownerPrefab.m_Prefab,
                m_Position = ownerTransform.m_Position,
                m_Rotation = ownerTransform.m_Rotation,
            };
            RealizeSubAreas(prefab, owner, ownerEntity, ref random);
            RealizeSubNets(prefab, owner, ownerEntity, lotOwner, simulationSpawn: false, ref random);
        }

        /// <summary>
        /// Emit lot/area definitions per <see cref="SubArea"/>, terrain-following polygons from
        /// <see cref="SubAreaNode"/> buffer (local to world). Resolve placeholder prefabs via
        /// SelectAreaPrefab, guarded against missing <see cref="SpawnableObjectData"/>.
        /// </summary>
        private void RealizeSubAreas(Entity prefab, OwnerDefinition owner, Entity ownerEntity,
            ref Unity.Mathematics.Random random)
        {
            if (!EntityManager.HasBuffer<SubArea>(prefab)) return;
            DynamicBuffer<SubArea> subAreas = EntityManager.GetBuffer<SubArea>(prefab, isReadOnly: true);
            if (subAreas.Length == 0) return;
            DynamicBuffer<SubAreaNode> subAreaNodes = EntityManager.GetBuffer<SubAreaNode>(prefab, isReadOnly: true);

            NativeParallelHashMap<Entity, int> selectedSpawnables = default;
            try
            {
                for (int i = 0; i < subAreas.Length; i++)
                {
                    SubArea subArea = subAreas[i];
                    Entity areaPrefab = subArea.m_Prefab;

                    int seed;
                    if (EntityManager.HasBuffer<PlaceholderObjectElement>(areaPrefab))
                    {
                        DynamicBuffer<PlaceholderObjectElement> placeholders =
                            EntityManager.GetBuffer<PlaceholderObjectElement>(areaPrefab, isReadOnly: true);
                        // SelectAreaPrefab reads SpawnableObjectData[candidate] with NO existence check —
                        // a candidate missing it is a hard (native) crash, not a catchable exception. Guard.
                        if (!AllHaveSpawnableData(placeholders))
                        {
                            Mod.log.Warn("[MP] BuildSync realize: a placeholder sub-area of '" +
                                _prefabSystem.GetPrefabName(prefab) +
                                "' has a candidate without SpawnableObjectData; skipping that area.");
                            continue;
                        }
                        if (!selectedSpawnables.IsCreated)
                            selectedSpawnables = new NativeParallelHashMap<Entity, int>(10, Allocator.Temp);
                        _spawnableObjectLookup.Update(this);
                        if (!global::Game.Areas.AreaUtils.SelectAreaPrefab(placeholders, _spawnableObjectLookup,
                                selectedSpawnables, ref random, out areaPrefab, out seed))
                            continue;
                    }
                    else
                    {
                        seed = random.NextInt();
                    }

                    // GenerateAreasSystem reads AreaData[prefab] with NO existence check → a non-area
                    // prefab here hard-crashes the game. Only emit a definition for a real area prefab.
                    if (!EntityManager.HasComponent<AreaData>(areaPrefab))
                    {
                        Mod.log.Warn("[MP] BuildSync realize: sub-area prefab '" +
                            _prefabSystem.GetPrefabName(areaPrefab) + "' of '" + _prefabSystem.GetPrefabName(prefab) +
                            "' has no AreaData; skipping that area.");
                        continue;
                    }

                    Entity areaDef = EntityManager.CreateEntity();
                    EntityManager.AddComponentData(areaDef, new CreationDefinition
                    {
                        m_Prefab = areaPrefab,
                        m_Owner = ownerEntity,
                        m_RandomSeed = seed,
                        m_Flags = CreationFlags.Permanent,
                    });
                    EntityManager.AddComponent<Updated>(areaDef);
                    EntityManager.AddComponent<Deleted>(areaDef); // consumed this frame, swept at Cleanup
                    if (ownerEntity == Entity.Null) EntityManager.AddComponentData(areaDef, owner);

                    DynamicBuffer<global::Game.Areas.Node> nodes =
                        EntityManager.AddBuffer<global::Game.Areas.Node>(areaDef);
                    nodes.ResizeUninitialized(subArea.m_NodeRange.y - subArea.m_NodeRange.x + 1);
                    int src = ObjectToolBaseSystem.GetFirstNodeIndex(subAreaNodes, subArea.m_NodeRange);
                    int dst = 0;
                    for (int j = subArea.m_NodeRange.x; j <= subArea.m_NodeRange.y; j++)
                    {
                        float3 local = subAreaNodes[src].m_Position;
                        float3 world = global::Game.Objects.ObjectUtils.LocalToWorld(owner.m_Position, owner.m_Rotation, local);
                        int parentMesh = subAreaNodes[src].m_ParentMesh;
                        // float.MinValue = "follow the terrain"; a real height only when mesh-relative.
                        float elevation = math.select(float.MinValue, local.y, parentMesh >= 0);
                        nodes[dst] = new global::Game.Areas.Node(world, elevation);
                        dst++;
                        if (++src == subArea.m_NodeRange.y) src = subArea.m_NodeRange.x;
                    }
                }
            }
            finally
            {
                if (selectedSpawnables.IsCreated) selectedSpawnables.Dispose();
            }
        }

        /// <summary>
        /// True only when every placeholder candidate carries <see cref="SpawnableObjectData"/>, which
        /// <c>AreaUtils.SelectAreaPrefab</c> dereferences without checking. Empty buffers return false
        /// (nothing to select).
        /// </summary>
        private bool AllHaveSpawnableData(DynamicBuffer<PlaceholderObjectElement> placeholders)
        {
            if (placeholders.Length == 0) return false;
            for (int i = 0; i < placeholders.Length; i++)
                if (!EntityManager.HasComponent<SpawnableObjectData>(placeholders[i].m_Object)) return false;
            return true;
        }

        /// <summary>
        /// Emit connection-net definitions per <see cref="SubNet"/>, curves averaged at shared
        /// node indices, mirrored for left-hand traffic and transformed local to world.
        /// </summary>
        private void RealizeSubNets(Entity prefab, OwnerDefinition owner, Entity ownerEntity,
            Entity lotOwner, bool simulationSpawn, ref Unity.Mathematics.Random random)
        {
            if (!EntityManager.HasBuffer<SubNet>(prefab)) return;
            DynamicBuffer<SubNet> subNets = EntityManager.GetBuffer<SubNet>(prefab, isReadOnly: true);
            if (subNets.Length == 0) return;

            // Height fields for the per-course snapping below. GetHeightData(waitForPending) is how
            // the terrain path already reads a settled surface; the water dependency is completed
            // before the data is touched. The spawner recipe snaps to no surface at all, so it does
            // not pay for either read.
            var heightData = default(TerrainHeightData);
            var waterData = default(WaterSurfaceData<SurfaceWater>);
            var lotInfo = default(global::Game.Buildings.BuildingUtils.LotInfo);
            bool hasLot = false;
            if (!simulationSpawn)
            {
                heightData = _terrainSystem.GetHeightData(waitForPending: true);
                Unity.Jobs.JobHandle waterDeps;
                waterData = _waterSystem.GetSurfaceData(out waterDeps);
                waterDeps.Complete();
                hasLot = TryGetOwnerLot(lotOwner, out lotInfo);
            }

            // Average the curve endpoints that share a node index, so sub-nets meeting at a node agree
            // on one position (.w counts contributors; divide to get the mean).
            var nodePositions = new NativeList<float4>(subNets.Length * 2, Allocator.Temp);
            try
            {
                for (int i = 0; i < subNets.Length; i++)
                {
                    SubNet subNet = subNets[i];
                    if (subNet.m_NodeIndex.x >= 0)
                    {
                        while (nodePositions.Length <= subNet.m_NodeIndex.x) nodePositions.Add(default);
                        nodePositions[subNet.m_NodeIndex.x] += new float4(subNet.m_Curve.a, 1f);
                    }
                    if (subNet.m_NodeIndex.y >= 0)
                    {
                        while (nodePositions.Length <= subNet.m_NodeIndex.y) nodePositions.Add(default);
                        nodePositions[subNet.m_NodeIndex.y] += new float4(subNet.m_Curve.d, 1f);
                    }
                }
                for (int i = 0; i < nodePositions.Length; i++)
                    nodePositions[i] /= math.max(1f, nodePositions[i].w);

                bool lefthand = _cityConfig.leftHandTraffic;
                for (int k = 0; k < subNets.Length; k++)
                {
                    _netGeometryLookup.Update(this);
                    SubNet subNet = global::Game.Net.NetUtils.GetSubNet(subNets, k, lefthand, ref _netGeometryLookup);
                    // GenerateNodes/EdgesSystem read NetData/NetGeometryData[prefab] with NO existence
                    // check → a sub-net prefab missing them hard-crashes the game. Skip rather than risk it.
                    if (!EntityManager.HasComponent<NetData>(subNet.m_Prefab) ||
                        !EntityManager.HasComponent<NetGeometryData>(subNet.m_Prefab))
                    {
                        Mod.log.Warn("[MP] BuildSync realize: sub-net prefab '" +
                            _prefabSystem.GetPrefabName(subNet.m_Prefab) + "' of '" + _prefabSystem.GetPrefabName(prefab) +
                            "' lacks NetData/NetGeometryData; skipping that driveway.");
                        continue;
                    }
                    RealizeSubNetCourse(subNet.m_Prefab, subNet.m_Curve, subNet.m_NodeIndex,
                        subNet.m_ParentMesh, subNet.m_Upgrades, nodePositions, owner, ownerEntity,
                        ref heightData, ref waterData, ref lotInfo, hasLot, simulationSpawn,
                        ref random);
                }
            }
            finally
            {
                nodePositions.Dispose();
            }
        }

        /// <summary>
        /// Reproduce the lot info the game derives for the building a set of connection nets is laid
        /// on. Requires a <see cref="global::Game.Buildings.Lot"/>; without one the caller falls back
        /// to terrain snapping, exactly as the tools do.
        /// </summary>
        private bool TryGetOwnerLot(Entity lotOwner,
            out global::Game.Buildings.BuildingUtils.LotInfo lotInfo)
        {
            lotInfo = default(global::Game.Buildings.BuildingUtils.LotInfo);
            if (lotOwner == Entity.Null || !EntityManager.Exists(lotOwner) ||
                !EntityManager.HasComponent<global::Game.Buildings.Lot>(lotOwner) ||
                !EntityManager.HasComponent<global::Game.Objects.Transform>(lotOwner) ||
                !EntityManager.HasComponent<PrefabRef>(lotOwner)) return false;

            Entity ownerPrefab = EntityManager.GetComponentData<PrefabRef>(lotOwner).m_Prefab;
            if (!EntityManager.HasComponent<BuildingData>(ownerPrefab)) return false;

            _transformLookup.Update(this);
            _prefabRefLookup.Update(this);
            _objectGeometryLookup.Update(this);
            _buildingTerraformLookup.Update(this);
            _buildingExtensionLookup.Update(this);

            global::Game.Objects.Elevation elevation = default(global::Game.Objects.Elevation);
            if (EntityManager.HasComponent<global::Game.Objects.Elevation>(lotOwner))
                elevation = EntityManager.GetComponentData<global::Game.Objects.Elevation>(lotOwner);
            DynamicBuffer<global::Game.Buildings.InstalledUpgrade> upgrades =
                EntityManager.HasBuffer<global::Game.Buildings.InstalledUpgrade>(lotOwner)
                    ? EntityManager.GetBuffer<global::Game.Buildings.InstalledUpgrade>(
                        lotOwner, isReadOnly: true)
                    : default(DynamicBuffer<global::Game.Buildings.InstalledUpgrade>);

            bool hasExtensionLots;
            lotInfo = global::Game.Buildings.BuildingUtils.CalculateLotInfo(
                new float2(EntityManager.GetComponentData<BuildingData>(ownerPrefab).m_LotSize) * 4f,
                EntityManager.GetComponentData<global::Game.Objects.Transform>(lotOwner),
                elevation,
                EntityManager.GetComponentData<global::Game.Buildings.Lot>(lotOwner),
                EntityManager.GetComponentData<PrefabRef>(lotOwner),
                upgrades, _transformLookup, _prefabRefLookup, _objectGeometryLookup,
                _buildingTerraformLookup, _buildingExtensionLookup, defaultNoSmooth: false,
                out hasExtensionLots);
            return true;
        }

        /// <summary>
        /// The world position of a node index several sub-nets share. A water net takes its height
        /// from the water surface rather than from the averaged prefab-local position - except on
        /// the spawner recipe, which never samples a surface at all.
        /// </summary>
        private static float3 SharedSubNetNodePosition(float3 localPosition, OwnerDefinition owner,
            NetGeometryData netGeometry, bool simulationSpawn, ref TerrainHeightData heightData,
            ref WaterSurfaceData<SurfaceWater> waterData)
        {
            float3 world = global::Game.Objects.ObjectUtils.LocalToWorld(
                owner.m_Position, owner.m_Rotation, localPosition);
            if (simulationSpawn ||
                (netGeometry.m_Flags & global::Game.Net.GeometryFlags.OnWater) == 0) return world;
            world.y = global::Game.Simulation.WaterUtils.SampleHeight(ref waterData, ref heightData, world);
            return world;
        }

        /// <summary>
        /// <paramref name="simulationSpawn"/> picks the recipe everything the simulation grows uses
        /// instead of the tool's: prefab-local height, no surface snapping, and merging disabled on
        /// both ends. The two are not interchangeable - a tool placement's driveway is meant to join
        /// the road it snapped to, a grown building's is not allowed to touch it. See
        /// docs/internals/building-placement-and-subnets.md.
        /// </summary>
        private void RealizeSubNetCourse(Entity netPrefab, Bezier4x3 curve, int2 nodeIndex, int2 parentMesh,
            CompositionFlags upgrades, NativeList<float4> nodePositions, OwnerDefinition owner,
            Entity ownerEntity, ref TerrainHeightData heightData,
            ref WaterSurfaceData<SurfaceWater> waterData,
            ref global::Game.Buildings.BuildingUtils.LotInfo lotInfo, bool hasLot,
            bool simulationSpawn, ref Unity.Mathematics.Random random)
        {
            Entity netDef = EntityManager.CreateEntity();
            EntityManager.AddComponentData(netDef, new CreationDefinition
            {
                m_Prefab = netPrefab,
                m_Owner = ownerEntity,
                m_RandomSeed = random.NextInt(),
                m_Flags = CreationFlags.Permanent,
            });
            EntityManager.AddComponent<Updated>(netDef);
            EntityManager.AddComponent<Deleted>(netDef); // consumed this frame, swept at Cleanup
            if (ownerEntity == Entity.Null) EntityManager.AddComponentData(netDef, owner);

            var course = default(NetCourse);
            // Tool-recipe height handling. A course whose BOTH ends are mesh-relative keeps its
            // prefab-local height; otherwise the free end(s) are snapped - to water, to the host
            // building's lot surface, or to the terrain - and the prefab-local height is then
            // re-applied as an offset. Laying a tool placement's paths at raw LocalToWorld height
            // instead is why they met the street at the wrong height and read as unconnected.
            _netGeometryLookup.Update(this);
            NetGeometryData netGeometry = _netGeometryLookup.HasComponent(netPrefab)
                ? _netGeometryLookup[netPrefab]
                : default(NetGeometryData);
            bool bothEndsOnMesh = parentMesh.x >= 0 && parentMesh.y >= 0;
            var worldCurve = new global::Game.Net.Curve
            {
                m_Bezier = global::Game.Objects.ObjectUtils.LocalToWorld(
                    owner.m_Position, owner.m_Rotation, curve),
            };
            if (simulationSpawn) course.m_Curve = worldCurve.m_Bezier;
            else if ((netGeometry.m_Flags & global::Game.Net.GeometryFlags.OnWater) != 0)
            {
                curve.y = default(Bezier4x1);
                worldCurve.m_Bezier = global::Game.Objects.ObjectUtils.LocalToWorld(
                    owner.m_Position, owner.m_Rotation, curve);
                course.m_Curve = global::Game.Net.NetUtils.AdjustPosition(worldCurve,
                    fixedStart: false, linearMiddle: false, fixedEnd: false,
                    ref heightData, ref waterData).m_Bezier;
            }
            else if (!bothEndsOnMesh)
            {
                bool fixedStart = parentMesh.x >= 0;
                bool fixedEnd = parentMesh.y >= 0;
                bool linearMiddle = fixedStart || fixedEnd;
                if ((netGeometry.m_Flags & global::Game.Net.GeometryFlags.FlattenTerrain) != 0)
                {
                    if (hasLot)
                    {
                        course.m_Curve = global::Game.Net.NetUtils.AdjustPosition(worldCurve,
                            fixedStart, linearMiddle, fixedEnd, ref lotInfo).m_Bezier;
                        course.m_Curve.a.y += curve.a.y;
                        course.m_Curve.b.y += curve.b.y;
                        course.m_Curve.c.y += curve.c.y;
                        course.m_Curve.d.y += curve.d.y;
                    }
                    else course.m_Curve = worldCurve.m_Bezier;
                }
                else
                {
                    course.m_Curve = global::Game.Net.NetUtils.AdjustPosition(worldCurve,
                        fixedStart, linearMiddle, fixedEnd, ref heightData).m_Bezier;
                    course.m_Curve.a.y += curve.a.y;
                    course.m_Curve.b.y += curve.b.y;
                    course.m_Curve.c.y += curve.c.y;
                    course.m_Curve.d.y += curve.d.y;
                }
            }
            else course.m_Curve = worldCurve.m_Bezier;

            course.m_StartPosition.m_Position = course.m_Curve.a;
            course.m_StartPosition.m_Rotation = global::Game.Net.NetUtils.GetNodeRotation(MathUtils.StartTangent(course.m_Curve), owner.m_Rotation);
            course.m_StartPosition.m_CourseDelta = 0f;
            course.m_StartPosition.m_Elevation = curve.a.y;
            course.m_StartPosition.m_ParentMesh = parentMesh.x;
            if (nodeIndex.x >= 0)
                course.m_StartPosition.m_Position = SharedSubNetNodePosition(
                    nodePositions[nodeIndex.x].xyz, owner, netGeometry, simulationSpawn,
                    ref heightData, ref waterData);

            course.m_EndPosition.m_Position = course.m_Curve.d;
            course.m_EndPosition.m_Rotation = global::Game.Net.NetUtils.GetNodeRotation(MathUtils.EndTangent(course.m_Curve), owner.m_Rotation);
            course.m_EndPosition.m_CourseDelta = 1f;
            course.m_EndPosition.m_Elevation = curve.d.y;
            course.m_EndPosition.m_ParentMesh = parentMesh.y;
            if (nodeIndex.y >= 0)
                course.m_EndPosition.m_Position = SharedSubNetNodePosition(
                    nodePositions[nodeIndex.y].xyz, owner, netGeometry, simulationSpawn,
                    ref heightData, ref waterData);

            course.m_Length = MathUtils.Length(course.m_Curve);
            course.m_FixedIndex = -1;
            course.m_StartPosition.m_Flags |= CoursePosFlags.IsFirst;
            course.m_EndPosition.m_Flags |= CoursePosFlags.IsLast;
            if (simulationSpawn)
            {
                course.m_StartPosition.m_Flags |= CoursePosFlags.DisableMerge;
                course.m_EndPosition.m_Flags |= CoursePosFlags.DisableMerge;
            }
            if (course.m_StartPosition.m_Position.Equals(course.m_EndPosition.m_Position))
            {
                course.m_StartPosition.m_Flags |= CoursePosFlags.IsLast;
                course.m_EndPosition.m_Flags |= CoursePosFlags.IsFirst;
            }
            EntityManager.AddComponentData(netDef, course);

            if (!upgrades.Equals(default(CompositionFlags)))
                EntityManager.AddComponentData(netDef, new global::Game.Net.Upgraded { m_Flags = upgrades });
        }
    }
}
