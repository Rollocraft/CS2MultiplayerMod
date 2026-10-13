using Colossal.Collections;
using Colossal.Mathematics;
using Game.Common;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Commands;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    public partial class NameSyncSystem
    {
        private void ApplyIncoming(MultiplayerSession session, long now)
        {
            _targetRetry.Observe(now, Infrastructure.RealizeGate.WorldBuildingHeld);
            // Names usually arrive before their target; retry a few times a second, not every frame.
            if ((_targetRetry.Count > 0 || _autoHold.Count > 0) &&
                now - _lastRetryMs >= RetryIntervalMs)
            {
                _lastRetryMs = now;
                RetryPending(now);
                ReassertHeldDraws(now);
            }

            while (_incoming.TryDequeue(out SimulationCommandMessage message))
            {
                if (message.OriginPlayerId == session.LocalPlayerId) continue;

                if (!Infrastructure.CommandDecode.TryDecode(message, EntityNameCommand.Decode, LogTopic.City,
                        "NameSync", out EntityNameCommand command))
                    continue;

                if (!TryApplyName(command, message.OriginPlayerId, now))
                    QueueRetry(command, message.OriginPlayerId);
                else _targetRetry.Remove(PendingKey(command));
            }
        }

        private void RetryPending(long now)
        {
            _targetRetry.Pump(pending => TryApplyName(pending.cmd, pending.origin, now),
                pending => SyncLog.Warn(LogTopic.City, "NameSync: no local " +
                    KindName(pending.cmd.TargetKind) + " '" + pending.cmd.TargetPrefabName +
                    "' appeared within " + (TargetRetryWindowMs / 1000) +
                    " s of eligible retry time; dropping its name."));
        }

        /// <summary>False only while the target can still arrive.</summary>
        private bool TryApplyName(EntityNameCommand command, int origin, long now)
        {
            var anchor = new float3(command.AnchorX, command.AnchorY, command.AnchorZ);
            Entity target = FindTarget(command.TargetKind, command.TargetPrefabName, anchor);
            if (target == Entity.Null) return false;

            bool refresh = false;
            string name = command.CustomName ?? string.Empty;
            if (command.SetsCustomName)
            {
                try
                {
                    // The game's own naming path: name table, marker and label, as a local rename.
                    _nameSystem.SetCustomName(target, name);
                }
                catch (System.Exception ex)
                {
                    SyncLog.Warn(LogTopic.City, "NameSync: naming " + KindName(command.TargetKind) +
                        " '" + command.TargetPrefabName + "' failed: " + ex.Message);
                    return true;
                }

                // Keep the baseline in step, or the next scan sends this back.
                if (name.Length == 0) _knownNames.Remove(target);
                else _knownNames[target] = name;
            }

            if (command.RandomIndices != null && command.RandomIndices.Length > 0)
            {
                refresh = ApplyRandomIndices(target, command.RandomIndices);
                if (command.TargetKind == EntityNameCommand.KindStreet)
                    HoldDraw(command, target, now);
            }

            // Makes the rendered label pick up the name, as the game's naming path does.
            if (refresh) EntityManager.AddComponent<BatchesUpdated>(target);
            SyncLog.Detail(LogTopic.City, "NameSync realize: " + KindName(command.TargetKind) + " '" +
                command.TargetPrefabName + "' from player " + origin +
                (command.SetsCustomName ? (name.Length == 0 ? " name cleared." : " named '" + name + "'.") : " auto-name applied."));
            return true;
        }

        private void QueueRetry(EntityNameCommand command, int origin)
        {
            if (!_targetRetry.SetLatest(PendingKey(command), (command, origin)))
                SyncLog.Warn(LogTopic.City,
                    "NameSync: pending-name queue is full; dropped its oldest entry.");
        }

        /// <summary>
        /// Keeps defending an adopted draw: while courses arrive, regrouping can delete the aggregate it
        /// was written to.
        /// </summary>
        private void HoldDraw(EntityNameCommand command, Entity target, long now)
        {
            var anchor = new float3(command.AnchorX, command.AnchorY, command.AnchorZ);
            string key = Infrastructure.ReplicationGuard.Key(command.TargetPrefabName, anchor);
            for (int i = _autoHold.Count - 1; i >= 0; i--)
            {
                AutoNameHold held = _autoHold[i];
                // One writer per street, or two draws alternate every tick.
                if (held.Target == target ||
                    Infrastructure.ReplicationGuard.Key(held.PrefabName, held.Anchor) == key)
                    _autoHold.RemoveAt(i);
            }
            if (_autoHold.Count >= MaxPendingTargets) _autoHold.RemoveAt(0);
            _autoHold.Add(new AutoNameHold
            {
                Target = target,
                Kind = command.TargetKind,
                PrefabName = command.TargetPrefabName,
                Anchor = anchor,
                Indices = command.RandomIndices,
                Deadline = now + AutoNameHoldMs,
            });
        }

        private void ReassertHeldDraws(long now)
        {
            for (int i = 0; i < _autoHold.Count;)
            {
                if (now >= _autoHold[i].Deadline) { _autoHold.RemoveAt(i); continue; }
                // Re-resolved from the anchor: the street may have merged since.
                _autoHold[i].Target =
                    FindTarget(_autoHold[i].Kind, _autoHold[i].PrefabName, _autoHold[i].Anchor);
                i++;
            }

            // Newest wins when two held draws now name one street.
            _heldTargets.Clear();
            for (int i = _autoHold.Count - 1; i >= 0; i--)
            {
                Entity target = _autoHold[i].Target;
                if (target != Entity.Null && !_heldTargets.Add(target)) _autoHold.RemoveAt(i);
            }

            for (int i = 0; i < _autoHold.Count; i++)
            {
                AutoNameHold held = _autoHold[i];
                if (held.Target == Entity.Null) continue;
                if (!ApplyRandomIndices(held.Target, held.Indices)) continue;

                EntityManager.AddComponent<BatchesUpdated>(held.Target);
                SyncLog.Detail(LogTopic.City, "NameSync: restored auto-name " +
                    Describe(held.Indices) + " on a " + KindName(held.Kind) +
                    " that regrouped locally.");
            }
        }

        /// <summary>One entry per target and field, so a pending draw and a rename do not overwrite each other.</summary>
        private static string PendingKey(EntityNameCommand command) =>
            command.TargetKind + "|" + (command.SetsCustomName ? "custom" : "auto") + "|" +
            command.TargetPrefabName + "|" +
            Infrastructure.ReplicationGuard.Key(command.TargetPrefabName,
                new float3(command.AnchorX, command.AnchorY, command.AnchorZ));

        private Entity FindTarget(byte kind, string prefabName, float3 anchor)
        {
            switch (kind)
            {
                case EntityNameCommand.KindStreet: return ResolveStreet(prefabName, anchor);
                case EntityNameCommand.KindDistrict:
                    return ResolveByAnchor(_districts, kind, prefabName, anchor,
                        DistrictMatchDistance);
                case EntityNameCommand.KindRoute:
                    return ResolveByAnchor(_routes, kind, prefabName, anchor, RouteMatchDistance);
                default: return ResolveObject(prefabName, anchor);
            }
        }

        /// <summary>The street owning the edge under the anchor, via the net search tree.</summary>
        private Entity ResolveStreet(string prefabName, float3 anchor)
        {
            NativeQuadTree<Entity, QuadTreeBoundsXZ> tree =
                _netSearch.GetNetSearchTree(readOnly: true, out JobHandle dependencies);
            // Read on the main thread; a structural change follows immediately afterwards.
            dependencies.Complete();

            var candidates = new NativeList<Entity>(16, Allocator.Temp);
            try
            {
                var iterator = new Infrastructure.Bounds3Collector
                {
                    Bounds = new Bounds3(
                        anchor - new float3(StreetSearchRadius, StreetTolY, StreetSearchRadius),
                        anchor + new float3(StreetSearchRadius, StreetTolY, StreetSearchRadius)),
                    Results = candidates,
                };
                tree.Iterate(ref iterator);

                Entity best = Entity.Null;
                float bestDistance = float.MaxValue;
                bool bestPrefabMatch = false;
                for (int i = 0; i < candidates.Length; i++)
                {
                    // Tree entries are only as fresh as its last update, so start from existence.
                    Entity edge = candidates[i];
                    if (!EntityManager.Exists(edge) ||
                        !EntityManager.HasComponent<global::Game.Net.Curve>(edge) ||
                        !EntityManager.HasComponent<global::Game.Net.Aggregated>(edge) ||
                        EntityManager.HasComponent<Temp>(edge) ||
                        EntityManager.HasComponent<Deleted>(edge)) continue;

                    Bezier4x3 curve =
                        EntityManager.GetComponentData<global::Game.Net.Curve>(edge).m_Bezier;
                    float distance = MathUtils.Distance(curve.xz, anchor.xz, out float t);
                    if (distance > StreetTolXZ) continue;
                    if (math.abs(MathUtils.Position(curve, t).y - anchor.y) > StreetTolY) continue;

                    Entity aggregate =
                        EntityManager.GetComponentData<global::Game.Net.Aggregated>(edge).m_Aggregate;
                    if (aggregate == Entity.Null || !EntityManager.Exists(aggregate) ||
                        !EntityManager.HasBuffer<global::Game.Net.AggregateElement>(aggregate) ||
                        EntityManager.HasComponent<Deleted>(aggregate)) continue;

                    // Road classes never share an aggregate: the prefab separates crossing streets.
                    bool prefabMatch = PrefabNameMatches(aggregate, prefabName);
                    bool better = best == Entity.Null || (prefabMatch && !bestPrefabMatch) ||
                                  (prefabMatch == bestPrefabMatch && distance < bestDistance);
                    if (!better) continue;
                    best = aggregate;
                    bestDistance = distance;
                    bestPrefabMatch = prefabMatch;
                }
                return best;
            }
            finally
            {
                candidates.Dispose();
            }
        }

        private Entity ResolveObject(string prefabName, float3 anchor)
        {
            // Prefab collections can share a display name; only an object prefab applies.
            if (!_prefabIndex.TryResolve(prefabName, IsObjectPrefab, out Entity prefab)) return Entity.Null;

            var candidates = new NativeList<Entity>(16, Allocator.Temp);
            try
            {
                _objectSearch.CollectNear(anchor, ObjectSearchRadius, candidates);

                Entity best = Entity.Null;
                float bestDistanceSq = ObjectMatchDistance * ObjectMatchDistance;
                for (int i = 0; i < candidates.Length; i++)
                {
                    Entity candidate = candidates[i];
                    if (!EntityManager.Exists(candidate) ||
                        !EntityManager.HasComponent<PrefabRef>(candidate) ||
                        !EntityManager.HasComponent<global::Game.Objects.Transform>(candidate) ||
                        EntityManager.HasComponent<Temp>(candidate) ||
                        EntityManager.HasComponent<Deleted>(candidate)) continue;
                    if (EntityManager.GetComponentData<PrefabRef>(candidate).m_Prefab != prefab) continue;

                    float distanceSq = math.distancesq(anchor,
                        EntityManager.GetComponentData<global::Game.Objects.Transform>(candidate)
                            .m_Position);
                    if (distanceSq > bestDistanceSq) continue;
                    bestDistanceSq = distanceSq;
                    best = candidate;
                }
                return best;
            }
            finally
            {
                candidates.Dispose();
            }
        }

        private Entity ResolveByAnchor(EntityQuery query, byte kind, string prefabName,
            float3 anchor, float maxDistance)
        {
            if (!_prefabIndex.TryResolve(prefabName, out Entity prefab)) return Entity.Null;
            if (query.IsEmptyIgnoreFilter) return Entity.Null;

            Entity best = Entity.Null;
            float bestDistanceSq = maxDistance * maxDistance;
            NativeArray<Entity> entities = query.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    if (EntityManager.GetComponentData<PrefabRef>(entities[i]).m_Prefab != prefab)
                        continue;
                    if (!TryAnchor(kind, entities[i], out float3 candidate)) continue;
                    float distanceSq = math.distancesq(candidate, anchor);
                    if (distanceSq > bestDistanceSq) continue;
                    bestDistanceSq = distanceSq;
                    best = entities[i];
                }
            }
            finally
            {
                entities.Dispose();
            }
            return best;
        }

        private bool IsObjectPrefab(Entity prefab) => EntityManager.HasComponent<ObjectData>(prefab);

        private bool PrefabNameMatches(Entity entity, string prefabName)
        {
            if (!EntityManager.HasComponent<PrefabRef>(entity)) return false;
            string local =
                _prefabIndex.NameOf(EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab);
            return !string.IsNullOrEmpty(local) && local == prefabName;
        }

        /// <summary>
        /// Writes the sender's draw, each slot clamped to this machine's name list so different content
        /// still lands on a real name.
        /// </summary>
        private bool ApplyRandomIndices(Entity target, int[] indices)
        {
            if (!EntityManager.HasBuffer<RandomLocalizationIndex>(target)) return false;

            // Read before resizing, so no buffer handle crosses the resize.
            int[] counts = ReadLocalizationCounts(target);
            int slots = counts != null ? counts.Length : indices.Length;
            if (slots == 0 || slots > EntityNameCommand.MaxRandomIndices) return false;

            DynamicBuffer<RandomLocalizationIndex> buffer =
                EntityManager.GetBuffer<RandomLocalizationIndex>(target);
            if (buffer.Length != slots) buffer.ResizeUninitialized(slots);

            bool changed = false;
            for (int i = 0; i < slots; i++)
            {
                int wanted = i < indices.Length ? indices[i] : -1;
                if (counts != null) wanted = counts[i] > 0 ? math.clamp(wanted, 0, counts[i] - 1) : -1;
                if (buffer[i].m_Index == wanted) continue;
                buffer[i] = new RandomLocalizationIndex(wanted);
                changed = true;
            }
            return changed;
        }

        /// <summary>Names per slot, from the prefab or a growable's zone; null when it declares none.</summary>
        private int[] ReadLocalizationCounts(Entity entity)
        {
            if (!EntityManager.HasComponent<PrefabRef>(entity)) return null;

            Entity prefab = EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab;
            if (prefab == Entity.Null || !EntityManager.Exists(prefab)) return null;
            if (!EntityManager.HasBuffer<LocalizationCount>(prefab))
            {
                if (!EntityManager.HasComponent<SpawnableBuildingData>(prefab)) return null;
                prefab = EntityManager.GetComponentData<SpawnableBuildingData>(prefab).m_ZonePrefab;
                if (prefab == Entity.Null || !EntityManager.Exists(prefab) ||
                    !EntityManager.HasBuffer<LocalizationCount>(prefab)) return null;
            }

            DynamicBuffer<LocalizationCount> counts =
                EntityManager.GetBuffer<LocalizationCount>(prefab, true);
            var lengths = new int[counts.Length];
            for (int i = 0; i < counts.Length; i++) lengths[i] = counts[i].m_Count;
            return lengths;
        }
    }
}
