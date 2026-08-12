using Game.Buildings;
using Game.Common;
using Game.Objects;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Sync.Commands;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    public partial class GrowableSyncSystem
    {
        /// <summary>Zone cells are 8 m; a lot's half-extent is therefore its cell count times four.</summary>
        private const float ZoneCellSize = 8f;

        /// <summary>
        /// Slack on the overlap test, in metres. Two buildings that merely touch along a shared lot
        /// edge are not in conflict - that is how a street of houses is meant to look.
        /// </summary>
        private const float OverlapTolerance = 0.5f;

        /// <summary>How far from the anchor an existing building may be and still be the same one.</summary>
        private const float AnchorMatchDistance = 4f;

        private const float AnchorSearchRadius = 8f;

        /// <summary>
        /// Applies the host's zoned-building decisions. Called from <see cref="SyncRealizeSystem"/>
        /// during ToolUpdate, the only phase in which a creation definition becomes a building.
        /// </summary>
        public void RealizePending()
        {
            MultiplayerService service = Mod.Service;
            if (service == null || !service.GameplaySyncReady) return;

            MultiplayerSession session = service.Session;
            long now = service.NowMs;

            // A host authors these; it must never apply one. Guards against a client that forges
            // the command as much as against a relay that echoes it back.
            if (session.Role == SessionRole.Host)
            {
                if (!_incoming.IsEmpty) SyncInboxDrop();
                return;
            }

            // A zoned building's transmitted height was sampled on the sender's terrain. Realizing
            // it while remote terraforming is still backlogged buries or floats it.
            if (DeferForTerrain) return;

            _applied.Prune(now);

            int realized = 0;
            SimulationCommandMessage message;
            while (realized < MaxRealizePerFrame && _incoming.TryDequeue(out message))
            {
                GrowableLifecycleCommand command;
                try { command = GrowableLifecycleCommand.Decode(message.Body); }
                catch (System.Exception ex)
                {
                    Mod.log.Warn("[MP] GrowableSync: dropping malformed command from player " +
                                 message.OriginPlayerId + ": " + ex.Message);
                    continue;
                }

                if (_applied.Contains(command.Sequence, now))
                {
                    _duplicates++;
                    Mod.Verbose("[MP] GrowableSync: ignoring duplicate " +
                                GrowableLifecycleCommand.OpName(command.Op) + " seq=" +
                                command.Sequence + " (already applied).");
                    continue;
                }

                if (Apply(command, now)) realized++;
            }

            ReportClientStats(now);
        }

        /// <summary>
        /// Returns true when the command consumed realize budget. Every outcome is terminal -
        /// built, corrected, refused, or aimed at something that is already gone - and each one is
        /// recorded in the replay window, so a redelivery is recognised rather than re-applied.
        /// </summary>
        private bool Apply(GrowableLifecycleCommand command, long now)
        {
            switch (command.Op)
            {
                case GrowableLifecycleCommand.OpSpawn: return ApplySpawn(command, now);
                case GrowableLifecycleCommand.OpLevel: return ApplyLevel(command, now);
                case GrowableLifecycleCommand.OpRemove: return ApplyRemove(command, now);
                case GrowableLifecycleCommand.OpState: return ApplyState(command, now);
                default: return false;
            }
        }

        private bool ApplySpawn(GrowableLifecycleCommand command, long now)
        {
            var position = new float3(command.AnchorX, command.AnchorY, command.AnchorZ);

            Entity prefab;
            if (!_prefabIndex.TryResolve(command.PrefabName,
                    candidate => EntityManager.HasComponent<SpawnableBuildingData>(candidate),
                    out prefab))
            {
                // Either an asset this machine does not have, or a command aimed at something that
                // is not a zoned building at all. Neither is retryable.
                _unknownPrefab++;
                _applied.Remember(command.Sequence, now, ReplayWindowMs);
                Mod.log.Warn("[MP] GrowableSync: unknown zoned-building prefab '" +
                             command.PrefabName + "' at " + Format(position) + "; spawn dropped.");
                return true;
            }

            var rotation = new quaternion(math.normalizesafe(
                new float4(command.RotX, command.RotY, command.RotZ, command.RotW),
                new float4(0f, 0f, 0f, 1f)));

            var blockers = new NativeList<Entity>(8, Allocator.Temp);
            try
            {
                CollectOverlapping(prefab, position, rotation, blockers);

                // Already standing, same building, same lot: a redelivery whose sequence has aged
                // out of the replay window. Rebuilding it would be the duplicate this whole path
                // exists to prevent.
                if (AlreadySatisfied(blockers, prefab, position))
                {
                    _duplicates++;
                    _applied.Remember(command.Sequence, now, ReplayWindowMs);
                    Mod.Verbose("[MP] GrowableSync: '" + command.PrefabName + "' already stands at " +
                                Format(position) + "; spawn seq=" + command.Sequence + " ignored.");
                    return true;
                }

                Entity placedBlocker = FirstPlayerPlaced(blockers);
                if (placedBlocker != Entity.Null)
                {
                    // A building a player put here by hand outranks a grown one: the host's own
                    // simulation would have condemned the growable against it too. Refusing keeps
                    // the two cities agreeing about the building that was deliberately placed.
                    _conflicts++;
                    _applied.Remember(command.Sequence, now, ReplayWindowMs);
                    Mod.log.Warn("[MP] GrowableSync conflict: '" + command.PrefabName + "' at " +
                                 Format(position) + " overlaps " + DescribeBlocker(placedBlocker) +
                                 "; spawn refused (seq=" + command.Sequence + ").");
                    Diagnostics.FlightRecorder.Note("growable spawn refused (placed building)");
                    return true;
                }

                // Everything left is a grown building this machine produced on its own - only
                // possible if its spawner ran while the session was not synchronized. The host is
                // the authority on grown buildings, so these lose and are cleared out of the way.
                for (int i = 0; i < blockers.Length; i++)
                {
                    _conflicts++;
                    Mod.log.Warn("[MP] GrowableSync conflict: evicting locally grown " +
                                 DescribeBlocker(blockers[i]) + " for the host's '" +
                                 command.PrefabName + "' at " + Format(position) + ".");
                    EntityManager.AddComponent<Deleted>(blockers[i]);
                    Diagnostics.FlightRecorder.Note("growable evicted for host spawn");
                }
            }
            finally
            {
                blockers.Dispose();
            }

            _buildSync.RealizeSimulationBuilding(prefab, position, rotation, SeedFor(command),
                (command.Flags & GrowableLifecycleCommand.FlagUnderConstruction) != 0);
            NoteSelfRealized(position, now);
            _applied.Remember(command.Sequence, now, ReplayWindowMs);
            _gotSpawn++;
            Mod.Verbose("[MP] GrowableSync realize: built '" + command.PrefabName + "' at " +
                        Format(position) + " seed=" + command.RandomSeed + " seq=" +
                        command.Sequence + ".");
            return true;
        }

        /// <summary>
        /// Hands a standing building the prefab it is becoming - the game's own level-change
        /// mechanism, so construction, notification and zone bookkeeping all run as usual. The
        /// target may be a prefab this machine's own simulation would never have chosen.
        /// </summary>
        private bool ApplyLevel(GrowableLifecycleCommand command, long now)
        {
            var position = new float3(command.AnchorX, command.AnchorY, command.AnchorZ);

            Entity prefab;
            if (!_prefabIndex.TryResolve(command.PrefabName,
                    candidate => EntityManager.HasComponent<SpawnableBuildingData>(candidate),
                    out prefab))
            {
                _unknownPrefab++;
                _applied.Remember(command.Sequence, now, ReplayWindowMs);
                Mod.log.Warn("[MP] GrowableSync: unknown level-change prefab '" +
                             command.PrefabName + "' at " + Format(position) + "; skipped.");
                return true;
            }

            Entity building = FindGrowableAt(position, Entity.Null);
            if (building == Entity.Null)
            {
                _unmatched++;
                _applied.Remember(command.Sequence, now, ReplayWindowMs);
                Mod.Verbose("[MP] GrowableSync: no building at " + Format(position) +
                            " to level to '" + command.PrefabName + "'; skipped.");
                return true;
            }

            // Already becoming that prefab: re-applying would restart construction. This is the
            // idempotence that matters in practice, because the local simulation may have proposed
            // its own level change for the same building before this one arrived.
            if (EntityManager.HasComponent<UnderConstruction>(building))
            {
                UnderConstruction current = EntityManager.GetComponentData<UnderConstruction>(building);
                if (current.m_NewPrefab == prefab)
                {
                    _applied.Remember(command.Sequence, now, ReplayWindowMs);
                    return true;
                }
                if (current.m_NewPrefab != Entity.Null)
                    Mod.Verbose("[MP] GrowableSync: replacing this machine's own level-change target " +
                                "at " + Format(position) + " with the host's '" +
                                command.PrefabName + "'.");
                current.m_NewPrefab = prefab;
                current.m_Progress = byte.MaxValue;
                EntityManager.SetComponentData(building, current);
            }
            else
            {
                EntityManager.AddComponentData(building, new UnderConstruction
                {
                    m_NewPrefab = prefab,
                    m_Progress = byte.MaxValue,
                });
            }

            ApplyConditionAndState(building, command);
            EntityManager.AddComponent<Updated>(building);
            _applied.Remember(command.Sequence, now, ReplayWindowMs);
            _gotLevel++;
            Mod.Verbose("[MP] GrowableSync realize: level change to '" + command.PrefabName +
                        "' at " + Format(position) + " seq=" + command.Sequence + ".");
            return true;
        }

        private bool ApplyRemove(GrowableLifecycleCommand command, long now)
        {
            var position = new float3(command.AnchorX, command.AnchorY, command.AnchorZ);

            Entity prefab;
            _prefabIndex.TryResolve(command.PrefabName, out prefab);
            Entity building = FindGrowableAt(position, prefab);
            if (building == Entity.Null)
            {
                // Nothing to remove. Convergent either way: the building this refers to was never
                // built here (its spawn was refused), or a player already bulldozed it.
                _unmatched++;
                _applied.Remember(command.Sequence, now, ReplayWindowMs);
                Mod.Verbose("[MP] GrowableSync: no building at " + Format(position) +
                            " to remove ('" + command.PrefabName + "'); already gone.");
                return true;
            }

            EntityManager.AddComponent<Deleted>(building);
            _applied.Remember(command.Sequence, now, ReplayWindowMs);
            _gotRemove++;
            Mod.Verbose("[MP] GrowableSync realize: removed '" + command.PrefabName + "' at " +
                        Format(position) + " seq=" + command.Sequence + ".");
            return true;
        }

        private bool ApplyState(GrowableLifecycleCommand command, long now)
        {
            var position = new float3(command.AnchorX, command.AnchorY, command.AnchorZ);
            Entity prefab;
            _prefabIndex.TryResolve(command.PrefabName, out prefab);

            Entity building = FindGrowableAt(position, prefab);
            if (building == Entity.Null)
            {
                _unmatched++;
                _applied.Remember(command.Sequence, now, ReplayWindowMs);
                return true;
            }

            ApplyConditionAndState(building, command);
            EntityManager.AddComponent<Updated>(building);
            _applied.Remember(command.Sequence, now, ReplayWindowMs);
            _gotState++;
            return true;
        }

        /// <summary>
        /// Writes the host's condition and abandonment state onto a building. Condition is the
        /// level-up progress bar, so leaving it local would have the peer level at its own pace.
        /// </summary>
        private void ApplyConditionAndState(Entity building, GrowableLifecycleCommand command)
        {
            if (EntityManager.HasComponent<BuildingCondition>(building))
            {
                BuildingCondition condition = EntityManager.GetComponentData<BuildingCondition>(building);
                if (condition.m_Condition != command.Condition)
                {
                    condition.m_Condition = command.Condition;
                    EntityManager.SetComponentData(building, condition);
                }
            }

            SetMarker<Abandoned>(building,
                (command.StateFlags & GrowableLifecycleCommand.StateAbandoned) != 0);
            SetMarker<Condemned>(building,
                (command.StateFlags & GrowableLifecycleCommand.StateCondemned) != 0);
            SetMarker<Destroyed>(building,
                (command.StateFlags & GrowableLifecycleCommand.StateDestroyed) != 0);
        }

        private void SetMarker<T>(Entity entity, bool wanted) where T : unmanaged, IComponentData
        {
            bool has = EntityManager.HasComponent<T>(entity);
            if (has == wanted) return;
            if (wanted) EntityManager.AddComponent<T>(entity);
            else EntityManager.RemoveComponent<T>(entity);
        }

        /// <summary>
        /// The building standing at an anchor. Positions are computed from the same road and block
        /// geometry on both machines, so the tolerance only absorbs float noise and a terrain
        /// height that was sampled independently.
        /// </summary>
        private Entity FindGrowableAt(float3 position, Entity prefab)
        {
            var candidates = new NativeList<Entity>(16, Allocator.Temp);
            try
            {
                _objectSearch.CollectNear(position, AnchorSearchRadius, candidates);

                Entity best = Entity.Null;
                float bestDistance = AnchorMatchDistance * AnchorMatchDistance;
                bool bestIsExact = false;

                for (int i = 0; i < candidates.Length; i++)
                {
                    Entity candidate = candidates[i];
                    if (!IsLiveGrowable(candidate)) continue;

                    float distance = math.distancesq(
                        EntityManager.GetComponentData<global::Game.Objects.Transform>(candidate)
                            .m_Position.xz, position.xz);
                    if (distance > AnchorMatchDistance * AnchorMatchDistance) continue;

                    // Prefer the named prefab, but stay tolerant of a different one: a building
                    // that levelled up no longer carries the prefab a removal names, and that
                    // removal still has to reach it.
                    bool exact = prefab != Entity.Null &&
                                 EntityManager.GetComponentData<PrefabRef>(candidate).m_Prefab == prefab;
                    if (bestIsExact && !exact) continue;
                    if (exact && !bestIsExact)
                    {
                        best = candidate;
                        bestDistance = distance;
                        bestIsExact = true;
                        continue;
                    }
                    if (best != Entity.Null && distance > bestDistance) continue;
                    best = candidate;
                    bestDistance = distance;
                }
                return best;
            }
            finally
            {
                candidates.Dispose();
            }
        }

        /// <summary>
        /// Everything already standing on the lot this spawn wants. Compares the two lot rectangles
        /// rather than the two pivots: buildings of different sizes conflict long before their
        /// centres coincide, and two neighbours on one street share a centre-to-centre distance
        /// that says nothing about whether they fit.
        /// </summary>
        private void CollectOverlapping(Entity prefab, float3 position, quaternion rotation,
            NativeList<Entity> blockers)
        {
            blockers.Clear();
            if (!EntityManager.HasComponent<BuildingData>(prefab)) return;
            float2 wantedExtent = LotExtent(EntityManager.GetComponentData<BuildingData>(prefab).m_LotSize);
            float reach = math.length(wantedExtent) + ZoneCellSize;

            var candidates = new NativeList<Entity>(32, Allocator.Temp);
            try
            {
                _objectSearch.CollectNear(position, reach, candidates);
                for (int i = 0; i < candidates.Length; i++)
                {
                    Entity candidate = candidates[i];
                    if (!EntityManager.Exists(candidate) ||
                        !EntityManager.HasComponent<Building>(candidate) ||
                        !EntityManager.HasComponent<global::Game.Objects.Transform>(candidate) ||
                        !EntityManager.HasComponent<PrefabRef>(candidate) ||
                        EntityManager.HasComponent<Temp>(candidate) ||
                        EntityManager.HasComponent<Deleted>(candidate) ||
                        EntityManager.HasComponent<Owner>(candidate)) continue;

                    Entity candidatePrefab = EntityManager.GetComponentData<PrefabRef>(candidate).m_Prefab;
                    if (!EntityManager.HasComponent<BuildingData>(candidatePrefab)) continue;

                    global::Game.Objects.Transform transform =
                        EntityManager.GetComponentData<global::Game.Objects.Transform>(candidate);
                    float2 extent = LotExtent(
                        EntityManager.GetComponentData<BuildingData>(candidatePrefab).m_LotSize);

                    if (RectanglesOverlap(position, rotation, wantedExtent,
                            transform.m_Position, transform.m_Rotation, extent))
                        blockers.Add(candidate);
                }
            }
            finally
            {
                candidates.Dispose();
            }
        }

        /// <summary>
        /// True when the host's building is the one already standing here. Same prefab on the same
        /// lot is the redelivery case; a different prefab at the same anchor is a real level
        /// difference and has to be resolved, not ignored.
        /// </summary>
        private bool AlreadySatisfied(NativeList<Entity> blockers, Entity prefab, float3 position)
        {
            for (int i = 0; i < blockers.Length; i++)
            {
                Entity blocker = blockers[i];
                if (EntityManager.GetComponentData<PrefabRef>(blocker).m_Prefab != prefab) continue;
                float distance = math.distancesq(
                    EntityManager.GetComponentData<global::Game.Objects.Transform>(blocker)
                        .m_Position.xz, position.xz);
                if (distance <= AnchorMatchDistance * AnchorMatchDistance) return true;
            }
            return false;
        }

        /// <summary>A building a player placed, as opposed to one a simulation grew.</summary>
        private Entity FirstPlayerPlaced(NativeList<Entity> blockers)
        {
            for (int i = 0; i < blockers.Length; i++)
            {
                Entity prefab = EntityManager.GetComponentData<PrefabRef>(blockers[i]).m_Prefab;
                if (!IsGrowablePrefab(prefab)) return blockers[i];
            }
            return Entity.Null;
        }

        private static float2 LotExtent(int2 lotSize) =>
            new float2(lotSize.x, lotSize.y) * (ZoneCellSize * 0.5f) - OverlapTolerance;

        /// <summary>
        /// Separating-axis test between two rotated lot rectangles. Four axes suffice: the two
        /// rectangles' own edge normals, which for rectangles are their local x and z.
        /// </summary>
        private static bool RectanglesOverlap(float3 centreA, quaternion rotationA, float2 extentA,
            float3 centreB, quaternion rotationB, float2 extentB)
        {
            if (extentA.x <= 0f || extentA.y <= 0f || extentB.x <= 0f || extentB.y <= 0f) return false;

            float2 rightA = math.normalizesafe(math.rotate(rotationA, new float3(1f, 0f, 0f)).xz,
                new float2(1f, 0f));
            float2 forwardA = math.normalizesafe(math.rotate(rotationA, new float3(0f, 0f, 1f)).xz,
                new float2(0f, 1f));
            float2 rightB = math.normalizesafe(math.rotate(rotationB, new float3(1f, 0f, 0f)).xz,
                new float2(1f, 0f));
            float2 forwardB = math.normalizesafe(math.rotate(rotationB, new float3(0f, 0f, 1f)).xz,
                new float2(0f, 1f));

            float2 delta = centreB.xz - centreA.xz;
            return !(SeparatedOn(rightA, delta, rightA, forwardA, extentA, rightB, forwardB, extentB) ||
                     SeparatedOn(forwardA, delta, rightA, forwardA, extentA, rightB, forwardB, extentB) ||
                     SeparatedOn(rightB, delta, rightA, forwardA, extentA, rightB, forwardB, extentB) ||
                     SeparatedOn(forwardB, delta, rightA, forwardA, extentA, rightB, forwardB, extentB));
        }

        private static bool SeparatedOn(float2 axis, float2 delta,
            float2 rightA, float2 forwardA, float2 extentA,
            float2 rightB, float2 forwardB, float2 extentB)
        {
            float reachA = math.abs(math.dot(rightA, axis)) * extentA.x +
                           math.abs(math.dot(forwardA, axis)) * extentA.y;
            float reachB = math.abs(math.dot(rightB, axis)) * extentB.x +
                           math.abs(math.dot(forwardB, axis)) * extentB.y;
            return math.abs(math.dot(delta, axis)) > reachA + reachB;
        }

        private bool IsLiveGrowable(Entity entity) =>
            EntityManager.Exists(entity) &&
            EntityManager.HasComponent<Building>(entity) &&
            EntityManager.HasComponent<PrefabRef>(entity) &&
            EntityManager.HasComponent<global::Game.Objects.Transform>(entity) &&
            !EntityManager.HasComponent<Temp>(entity) &&
            !EntityManager.HasComponent<Deleted>(entity) &&
            !EntityManager.HasComponent<Owner>(entity) &&
            IsGrowablePrefab(EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab);

        private string DescribeBlocker(Entity blocker)
        {
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(blocker).m_Prefab;
            string name = PrefabIndexSafeName(prefab);
            bool grown = IsGrowablePrefab(prefab);
            return (grown ? "a grown '" : "a placed '") + (name ?? "?") + "'";
        }

        private int SeedFor(GrowableLifecycleCommand command)
        {
            // The built entity keeps the low 16 bits as its PseudoRandomSeed, which is the variant.
            // Zero is the one value the game's own random rejects, so it is nudged rather than
            // passed through - a building with no seed at all would fail to pick a mesh.
            int seed = command.RandomSeed;
            return seed == 0 ? 1 : seed;
        }

        /// <summary>
        /// Remembers that a building was just asked for at this spot. The definition does not
        /// become an entity until a later phase, so the only way to recognise our own building when
        /// it appears is the position we asked for it at.
        /// </summary>
        private void NoteSelfRealized(float3 position, long now)
        {
            if (_selfRealized.Count >= MaxSelfRealized) _selfRealized.RemoveAt(0);
            _selfRealized.Add((position, now + SelfRealizedWindowMs));
        }

        private bool WasSelfRealized(float3 position, long now)
        {
            for (int i = _selfRealized.Count - 1; i >= 0; i--)
            {
                (float3 position, long expiry) entry = _selfRealized[i];
                if (entry.expiry <= now) { _selfRealized.RemoveAt(i); continue; }
                if (math.distancesq(entry.position.xz, position.xz) <=
                    AnchorMatchDistance * AnchorMatchDistance) return true;
            }
            return false;
        }

        /// <summary>
        /// Removes zoned buildings this client grew by itself. Its spawner is held for as long as
        /// the session is synchronized, so in normal running this finds nothing - but authority is
        /// handed back whenever sync drops (a resync, a world reload), and anything grown in that
        /// window would otherwise stand forever on a lot the host has its own plans for.
        ///
        /// Catching them as they appear is what keeps the invariant simple: on a client, every
        /// zoned building came from the host.
        /// </summary>
        private void RejectLocallyGrownBuildings(long now)
        {
            if (_createdBuildings.IsEmptyIgnoreFilter) return;

            NativeArray<Entity> entities = _createdBuildings.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity entity = entities[i];
                    Entity prefab = EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab;
                    if (!IsGrowablePrefab(prefab)) continue;

                    float3 position = EntityManager
                        .GetComponentData<global::Game.Objects.Transform>(entity).m_Position;
                    if (WasSelfRealized(position, now)) continue;

                    EntityManager.AddComponent<Deleted>(entity);
                    _rejectedLocal++;
                    Mod.log.Warn("[MP] GrowableSync: this client grew '" +
                                 PrefabIndexSafeName(prefab) + "' at " + Format(position) +
                                 " on its own; removed (the host decides zoned buildings).");
                    Diagnostics.FlightRecorder.Note("locally grown building rejected");
                }
            }
            finally
            {
                entities.Dispose();
            }
        }

        /// <summary>
        /// A host received one of its own commands back. Drain rather than apply: applying would
        /// duplicate the building the host already has.
        /// </summary>
        private void SyncInboxDrop()
        {
            int dropped = 0;
            SimulationCommandMessage message;
            while (_incoming.TryDequeue(out message)) dropped++;
            if (dropped == 0) return;
            Mod.log.Warn("[MP] GrowableSync: host discarded " + dropped +
                         " zoned-building command(s); only a host may author them.");
        }
    }
}
