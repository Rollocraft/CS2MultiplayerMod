using System.Collections.Generic;
using Game.Areas;
using Game.Common;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.Infrastructure;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    public partial class AreaSyncSystem
    {
        private void RetryOwnedAreaSnapshots(long now)
        {
            for (int i = 0; i < _ownedAreaRetry.Count;)
            {
                var pending = _ownedAreaRetry[i];
                if (TryRealizeOwnedAreaSnapshot(pending.command, pending.origin, now))
                {
                    _ownedAreaRetry.RemoveAt(i);
                    continue;
                }
                if (now < pending.deadline)
                {
                    i++;
                    continue;
                }

                _ownedAreaRetry.RemoveAt(i);
                // Dropping this snapshot is not a divergence worth reloading the world for. The
                // atomic object graph already carried the building and its declared lot, this
                // channel only refines the polygon, and the sender's periodic redraw scan keeps
                // offering another snapshot. Escalating instead cost a full save-stream-reload for
                // a lot outline, and did so on a fixed ten-second timer after any placement whose
                // owner this machine could not match.
                Mod.log.Warn("[MP] AreaSync: owner '" +
                             pending.command.OwnerPrefabName +
                             "' did not appear in time for its owned area " +
                             DescribeOwnedAreaOwnerSearch(pending.command) +
                             "; dropping this snapshot - a later redraw will carry the polygon.");
                Diagnostics.FlightRecorder.Note("owned area owner expired; snapshot dropped");
            }
        }

        private void QueueOwnedAreaRetry(OwnedAreaSnapshotCommand command,
            int originPlayerId, long deadline)
        {
            if (_ownedAreaRetry.Count >= MaxPendingOwnedAreas)
            {
                _ownedAreaRetry.Clear();
                Diagnostics.FlightRecorder.Note(
                    "owned area retry queue overflow; recovery requested");
                SyncInbox.RequestResync(CS2MultiplayerMod.Game.Diagnostics.ResyncReport
                    .Create("owned area retry queue overflow", "area",
                        CS2MultiplayerMod.Game.Diagnostics.ResyncEvidence.StreamLoss)
                    .About("owned area retry queue")
                    .Tried("nothing - the queue was full and was cleared"));
            }
            _ownedAreaRetry.Add((command, originPlayerId, deadline));
        }

        /// <summary>
        /// Apply a complete extractor/storage polygon to its stable owner. If an older partial
        /// placement produced only the building, create the missing owned area and let the normal
        /// reference system add it to the owner's SubArea buffer.
        /// </summary>
        private bool TryRealizeOwnedAreaSnapshot(OwnedAreaSnapshotCommand command,
            int originPlayerId, long now)
        {
            Entity areaPrefab;
            Entity ownerPrefab;
            if (!_prefabIndex.TryResolve(command.AreaPrefabName, out areaPrefab) ||
                !_prefabIndex.TryResolve(command.OwnerPrefabName, out ownerPrefab) ||
                !IsSpecializedAreaPrefab(areaPrefab) ||
                !PrefabDeclaresOwnedArea(ownerPrefab, areaPrefab))
            {
                Mod.log.Warn("[MP] AreaSync: rejected incompatible owned area '" +
                             command.AreaPrefabName + "' on '" +
                             command.OwnerPrefabName + "'.");
                return true;
            }

            Entity owner = FindOwnedAreaOwner(ownerPrefab,
                new float3(command.OwnerX, command.OwnerY, command.OwnerZ),
                new float4(command.OwnerRotX, command.OwnerRotY,
                    command.OwnerRotZ, command.OwnerRotW));
            if (owner == Entity.Null) return false;

            int nodeCount = CanonicalOwnedAreaNodeCount(command);
            if (nodeCount < 3) return true;
            Entity area = FindOwnedSpecializedArea(areaPrefab, owner);
            string guardKey = OwnedAreaUpdateKey(command.AreaPrefabName,
                command.OwnerPrefabName,
                new float3(command.OwnerX, command.OwnerY, command.OwnerZ));
            _guard.Mark(guardKey, now);

            if (area == Entity.Null)
            {
                CreateMissingOwnedArea(command, areaPrefab, owner, nodeCount);
                Mod.Verbose("[MP] AreaSync: restored owned area '" +
                            command.AreaPrefabName + "' on '" +
                            command.OwnerPrefabName + "' from player " +
                            originPlayerId + ".");
                return true;
            }

            DynamicBuffer<Node> nodes = EntityManager.GetBuffer<Node>(area);
            nodes.Clear();
            var ring = new float3[nodeCount];
            for (int i = 0; i < nodeCount; i++)
            {
                float3 position = new float3(command.NodeX[i],
                    command.NodeY[i], command.NodeZ[i]);
                ring[i] = position;
                nodes.Add(new Node(position, command.NodeElevation[i]));
            }

            Area current = EntityManager.GetComponentData<Area>(area);
            current.m_Flags |= AreaFlags.Complete;
            EntityManager.SetComponentData(area, current);
            MarkAreaAndSubAreasUpdated(area);
            EnsureOwnerSubAreaReference(owner, area);
            _knownRings[area] = ring;
            Mod.Verbose("[MP] AreaSync: redrew owned area '" +
                        command.AreaPrefabName + "' on '" +
                        command.OwnerPrefabName + "' (" + nodeCount +
                        " nodes) from player " + originPlayerId + ".");
            return true;
        }

        /// <summary>
        /// Vertical slack for the owner match. Wide enough to absorb a building conformed to this
        /// machine's own ground, narrow enough that a genuinely different level cannot match.
        /// </summary>
        private const float MaxOwnedAreaOwnerHeightGap = 40f;

        /// <summary>
        /// What the owner search actually saw, for a snapshot that never bound. Separates "this
        /// machine has no such building" from "it has one but outside the accepted distance".
        /// </summary>
        private string DescribeOwnedAreaOwnerSearch(OwnedAreaSnapshotCommand command)
        {
            Entity ownerPrefab;
            if (!_prefabIndex.TryResolve(command.OwnerPrefabName, out ownerPrefab))
                return "(owner prefab unavailable)";

            var wanted = new float3(command.OwnerX, command.OwnerY, command.OwnerZ);
            int candidates = 0;
            float nearestSq = float.MaxValue;
            NativeArray<Entity> owners = _ownedAreaOwners.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < owners.Length; i++)
                {
                    if (EntityManager.GetComponentData<PrefabRef>(owners[i]).m_Prefab !=
                        ownerPrefab) continue;
                    candidates++;
                    float distanceSq = math.distancesq(
                        EntityManager.GetComponentData<global::Game.Objects.Transform>(owners[i])
                            .m_Position.xz, wanted.xz);
                    if (distanceSq < nearestSq) nearestSq = distanceSq;
                }
            }
            finally
            {
                owners.Dispose();
            }
            return candidates == 0
                ? "(no local building of that prefab)"
                : "(candidates=" + candidates + " nearestM=" +
                  math.sqrt(nearestSq).ToString("0.##") + ")";
        }

        private Entity FindOwnedAreaOwner(Entity ownerPrefab, float3 position,
            float4 rotation)
        {
            float lengthSq = math.lengthsq(rotation);
            if (lengthSq < 0.25f) return Entity.Null;
            rotation *= math.rsqrt(lengthSq);

            NativeArray<Entity> owners = _ownedAreaOwners.ToEntityArray(Allocator.Temp);
            try
            {
                Entity best = Entity.Null;
                float bestDistance = 4f;
                for (int i = 0; i < owners.Length; i++)
                {
                    Entity candidate = owners[i];
                    if (EntityManager.GetComponentData<PrefabRef>(candidate).m_Prefab !=
                        ownerPrefab) continue;
                    global::Game.Objects.Transform transform =
                        EntityManager.GetComponentData<global::Game.Objects.Transform>(
                            candidate);
                    // Horizontal only: the wire Y is the sender's, while this machine conforms the
                    // building to its own ground, so on sloped terrain the vertical difference can
                    // exceed the whole budget on its own and reject the right owner. Nothing stacks
                    // two lot owners over one point, so the plane is the discriminator; the loose
                    // vertical bound below only rules out a different level entirely.
                    float distance = math.distancesq(transform.m_Position.xz, position.xz);
                    if (distance >= bestDistance ||
                        math.abs(transform.m_Position.y - position.y) > MaxOwnedAreaOwnerHeightGap ||
                        math.abs(math.dot(transform.m_Rotation.value, rotation)) < 0.98f)
                        continue;
                    best = candidate;
                    bestDistance = distance;
                }
                return best;
            }
            finally
            {
                owners.Dispose();
            }
        }

        private Entity FindOwnedSpecializedArea(Entity areaPrefab, Entity owner)
        {
            NativeArray<Entity> areas =
                _ownedSpecializedAreas.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < areas.Length; i++)
                {
                    Entity candidate = areas[i];
                    if (EntityManager.GetComponentData<PrefabRef>(candidate).m_Prefab !=
                        areaPrefab) continue;
                    Entity topOwner;
                    if (TryFindTopAreaOwner(candidate, out topOwner) &&
                        topOwner == owner) return candidate;
                }
            }
            finally
            {
                areas.Dispose();
            }
            return Entity.Null;
        }

        private void CreateMissingOwnedArea(OwnedAreaSnapshotCommand command,
            Entity areaPrefab, Entity owner, int nodeCount)
        {
            Entity definition = EntityManager.CreateEntity();
            EntityManager.AddComponentData(definition, new CreationDefinition
            {
                m_Prefab = areaPrefab,
                m_Owner = owner,
                m_Flags = CreationFlags.Permanent,
            });
            DynamicBuffer<Node> nodes = EntityManager.AddBuffer<Node>(definition);
            for (int i = 0; i < nodeCount; i++)
                nodes.Add(new Node(new float3(command.NodeX[i],
                    command.NodeY[i], command.NodeZ[i]),
                    command.NodeElevation[i]));

            // New area definitions close the ring by repeating the first vertex. Generation strips
            // that sentinel and sets AreaFlags.Complete on the live entity.
            nodes.Add(nodes[0]);
            EntityManager.AddComponent<Updated>(definition);
            EntityManager.AddComponent<Deleted>(definition);
        }

        private void EnsureOwnerSubAreaReference(Entity owner, Entity area)
        {
            if (!EntityManager.HasBuffer<global::Game.Areas.SubArea>(owner)) return;
            DynamicBuffer<global::Game.Areas.SubArea> subAreas =
                EntityManager.GetBuffer<global::Game.Areas.SubArea>(owner);
            for (int i = 0; i < subAreas.Length; i++)
                if (subAreas[i].m_Area == area) return;
            subAreas.Add(new global::Game.Areas.SubArea(area));
            EntityManager.AddComponent<Updated>(owner);
        }

        private void MarkAreaAndSubAreasUpdated(Entity area)
        {
            Entity[] childAreas = null;
            if (area != Entity.Null && EntityManager.Exists(area) &&
                EntityManager.HasBuffer<global::Game.Areas.SubArea>(area))
            {
                DynamicBuffer<global::Game.Areas.SubArea> subAreas =
                    EntityManager.GetBuffer<global::Game.Areas.SubArea>(
                        area, isReadOnly: true);
                childAreas = new Entity[subAreas.Length];
                for (int i = 0; i < subAreas.Length; i++)
                    childAreas[i] = subAreas[i].m_Area;
            }

            // Area edits rebuild prefab-owned slave surfaces in the same transaction. Those
            // surfaces derive their nodes from the parent only when they are tagged Updated.
            MarkAreaUpdated(area);
            if (childAreas == null) return;
            for (int i = 0; i < childAreas.Length; i++)
                MarkAreaUpdated(childAreas[i]);
        }

        private void MarkAreaUpdated(Entity area)
        {
            if (area == Entity.Null || !EntityManager.Exists(area) ||
                !EntityManager.HasComponent<Area>(area) ||
                EntityManager.HasComponent<Deleted>(area) ||
                EntityManager.HasComponent<Updated>(area)) return;
            EntityManager.AddComponent<Updated>(area);
        }

        private static int CanonicalOwnedAreaNodeCount(
            OwnedAreaSnapshotCommand command)
        {
            int count = command.NodeX != null ? command.NodeX.Length : 0;
            if (count >= 4 &&
                command.NodeX[0] == command.NodeX[count - 1] &&
                command.NodeY[0] == command.NodeY[count - 1] &&
                command.NodeZ[0] == command.NodeZ[count - 1])
                count--;
            return count;
        }

        private void RealizeUpdate(AreaUpdateCommand command, int originPlayerId, long now)
        {
            Entity prefab;
            if (!_prefabIndex.TryResolve(command.PrefabName, out prefab))
            {
                Mod.log.Warn("[MP] AreaSync update: unknown prefab '" + command.PrefabName + "'; skipping.");
                return;
            }
            if (command.NodeX == null || command.NodeX.Length < 3) return;

            // The anchor is the polygon's centroid BEFORE the edit — exactly what our
            // not-yet-edited copy still has. Nearest same-prefab area within 500 m wins.
            var anchor = new float3(command.AnchorX, 0f, command.AnchorZ);
            Entity best = Entity.Null;
            float bestSq = 250000f;
            NativeArray<Entity> entities = _liveAreas.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    if (EntityManager.GetComponentData<PrefabRef>(entities[i]).m_Prefab != prefab) continue;
                    float3[] ring = ReadRing(entities[i]);
                    if (ring.Length == 0) continue;
                    float d = math.distancesq(CentroidOf(ring), anchor);
                    if (d > bestSq) continue;
                    bestSq = d;
                    best = entities[i];
                }
            }
            finally
            {
                entities.Dispose();
            }

            if (best == Entity.Null)
            {
                Mod.log.Warn("[MP] AreaSync update: no local '" + command.PrefabName +
                             "' near the old centroid; skipping redraw.");
                return;
            }

            try
            {
                // Rewrite the ring in place — the entity (and with it district policies,
                // citizen assignments, …) keeps its identity; Updated retriangulates.
                DynamicBuffer<Node> nodes = EntityManager.GetBuffer<Node>(best);
                nodes.Clear();
                var newRing = new float3[command.NodeX.Length];
                for (int n = 0; n < command.NodeX.Length; n++)
                {
                    var position = new float3(command.NodeX[n], command.NodeY[n], command.NodeZ[n]);
                    newRing[n] = position;
                    nodes.Add(new Node { m_Position = position, m_Elevation = command.NodeElevation[n] });
                }
                MarkAreaAndSubAreasUpdated(best);

                // Suppress the echo both ways: spatial guard + the scan cache itself.
                _guard.Mark(AreaUpdateKey(command.PrefabName, CentroidOf(newRing)), now);
                _knownRings[best] = newRing;
                Mod.Verbose("[MP] AreaSync update: redrew '" + command.PrefabName + "' (" +
                             command.NodeX.Length + " nodes) from player " + originPlayerId + ".");
            }
            catch (System.Exception ex)
            {
                Mod.log.Error("[MP] AreaSync update FAILED for '" + command.PrefabName + "': " + ex);
            }
        }

        private void RealizeCreate(AreaCreateCommand command, int originPlayerId, long now)
        {
            Entity prefab;
            if (!_prefabIndex.TryResolve(command.PrefabName, out prefab))
            {
                Mod.log.Warn("[MP] AreaSync realize: unknown prefab '" + command.PrefabName + "'; skipping.");
                return;
            }
            if (command.NodeX == null || command.NodeX.Length < 3) return;

            var first = new float3(command.NodeX[0], command.NodeY[0], command.NodeZ[0]);
            _guard.Mark(AreaKey(command.PrefabName, first), now);
            try
            {
                // CreateAreasJob (GenerateAreasSystem) consumes CreationDefinition + a Node
                // ring buffer — same Updated/Deleted definition lifecycle as objects/nets.
                Entity definition = EntityManager.CreateEntity();
                EntityManager.AddComponentData(definition, new CreationDefinition
                {
                    m_Prefab = prefab,
                    m_RandomSeed = 0,
                    m_Flags = CreationFlags.Permanent,
                });
                DynamicBuffer<Node> nodes = EntityManager.AddBuffer<Node>(definition);
                for (int n = 0; n < command.NodeX.Length; n++)
                    nodes.Add(new Node
                    {
                        m_Position = new float3(command.NodeX[n], command.NodeY[n], command.NodeZ[n]),
                        m_Elevation = command.NodeElevation[n],
                    });
                EntityManager.AddComponent<Updated>(definition);
                EntityManager.AddComponent<Deleted>(definition);
                Mod.Verbose("[MP] AreaSync realize: drew '" + command.PrefabName + "' (" +
                             command.NodeX.Length + " nodes) from player " + originPlayerId + ".");
            }
            catch (System.Exception ex)
            {
                Mod.log.Error("[MP] AreaSync realize FAILED for '" + command.PrefabName + "': " + ex);
            }
        }

        private void RealizeDeletes(List<AreaDeleteCommand> commands, long now)
        {
            var targets = new List<(Entity prefab, float3 first, int count, string name)>();
            for (int i = 0; i < commands.Count; i++)
            {
                Entity prefab;
                if (_prefabIndex.TryResolve(commands[i].PrefabName, out prefab))
                    targets.Add((prefab,
                        new float3(commands[i].NodeX, commands[i].NodeY, commands[i].NodeZ),
                        commands[i].NodeCount, commands[i].PrefabName));
            }
            if (targets.Count == 0) return;

            int deleted = 0;
            NativeArray<Entity> entities = _liveAreas.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length && targets.Count > 0; i++)
                {
                    Entity candidatePrefab = EntityManager.GetComponentData<PrefabRef>(entities[i]).m_Prefab;
                    DynamicBuffer<Node> nodes = EntityManager.GetBuffer<Node>(entities[i], true);
                    if (nodes.Length == 0) continue;

                    for (int t = targets.Count - 1; t >= 0; t--)
                    {
                        if (targets[t].prefab != candidatePrefab) continue;
                        if (nodes.Length != targets[t].count) continue;
                        if (math.distancesq(targets[t].first, nodes[0].m_Position) > 4f) continue;

                        _guard.Mark(AreaDeleteKey(targets[t].name, nodes[0].m_Position), now);
                        EntityManager.AddComponent<Deleted>(entities[i]);
                        targets.RemoveAt(t);
                        deleted++;
                        break;
                    }
                }
            }
            finally
            {
                entities.Dispose();
            }

            if (deleted > 0 || targets.Count > 0)
                Mod.Verbose("[MP] AreaSync: removed " + deleted + " area(s); " + targets.Count +
                             " already gone (no local match).");
        }

    }
}
