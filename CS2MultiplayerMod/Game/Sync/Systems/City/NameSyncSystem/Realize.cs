using Colossal.Collections;
using Colossal.Mathematics;
using Game.Areas;
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
            // Names almost always arrive before the road or building they belong to has been rebuilt
            // here, so the retry pass is the normal path, not the exception. Re-attempting it a few
            // times a second rather than every frame keeps a pending name from completing the net
            // search tree's jobs on every single frame of its window.
            if ((_targetRetry.Count > 0 || _autoHold.Count > 0) &&
                now - _lastRetryMs >= RetryIntervalMs)
            {
                _lastRetryMs = now;
                RetryPending(now);
                ReassertHeldDraws(now);
            }

            SimulationCommandMessage message;
            while (_incoming.TryDequeue(out message))
            {
                if (message.OriginPlayerId == session.LocalPlayerId) continue;

                EntityNameCommand command;
                try { command = EntityNameCommand.Decode(message.Body); }
                catch (System.Exception ex)
                {
                    SyncLog.Warn(LogTopic.City, "NameSync: dropping malformed command: " +
                        ex.Message);
                    continue;
                }

                if (!TryApplyName(command, message.OriginPlayerId, now))
                    QueueRetry(command, message.OriginPlayerId, now);
            }
        }

        private void RetryPending(long now)
        {
            for (int i = 0; i < _targetRetry.Count;)
            {
                var pending = _targetRetry[i];
                if (TryApplyName(pending.cmd, pending.origin, now))
                {
                    _targetRetry.RemoveAt(i);
                    continue;
                }
                if (now >= pending.deadline)
                {
                    // A name is cosmetic: the world stays consistent without it, so this never
                    // escalates to a resync the way a missing build target does.
                    SyncLog.Warn(LogTopic.City, "NameSync: no local " +
                        KindName(pending.cmd.TargetKind) + " '" + pending.cmd.TargetPrefabName +
                        "' appeared within " + (TargetRetryWindowMs / 1000) +
                        " s; dropping its name.");
                    _targetRetry.RemoveAt(i);
                    continue;
                }
                i++;
            }
        }

        /// <summary>
        /// Returns false only while the target can still arrive - a name usually reaches a peer
        /// before the road or building it belongs to has been rebuilt there.
        /// </summary>
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
                    // The game's own naming path: it owns the name table, adds/removes the marker
                    // component, and refreshes the rendered label exactly as a local rename does.
                    _nameSystem.SetCustomName(target, name);
                }
                catch (System.Exception ex)
                {
                    SyncLog.Warn(LogTopic.City, "NameSync: naming " + KindName(command.TargetKind) +
                        " '" + command.TargetPrefabName + "' failed: " + ex.Message);
                    return true;
                }

                // Keep the diff baseline in step with what we just applied, or the next scan reads
                // this as a local rename and sends it straight back.
                if (name.Length == 0) _knownNames.Remove(target);
                else _knownNames[target] = name;
            }

            if (command.RandomIndices != null && command.RandomIndices.Length > 0)
            {
                refresh = ApplyRandomIndices(target, command.RandomIndices);
                if (command.TargetKind == EntityNameCommand.KindStreet)
                    HoldDraw(command, target, now);
            }

            // What makes the rendered street/district label pick the new name up; it is also what the
            // game's own naming path adds.
            if (refresh) EntityManager.AddComponent<BatchesUpdated>(target);
            SyncLog.Detail(LogTopic.City, "NameSync realize: " + KindName(command.TargetKind) + " '" +
                command.TargetPrefabName + "' from player " + origin +
                (command.SetsCustomName ? (name.Length == 0 ? " name cleared." : " named '" + name + "'.") : " auto-name applied."));
            return true;
        }

        private void QueueRetry(EntityNameCommand command, int origin, long now)
        {
            string key = PendingKey(command);
            for (int i = 0; i < _targetRetry.Count; i++)
            {
                if (PendingKey(_targetRetry[i].cmd) != key) continue;
                // Only the newest name for a target matters while that target is absent.
                _targetRetry[i] = (command, origin, now + TargetRetryWindowMs);
                return;
            }
            if (_targetRetry.Count >= MaxPendingTargets)
            {
                _targetRetry.RemoveAt(0);
                SyncLog.Warn(LogTopic.City,
                    "NameSync: pending-name queue is full; dropped its oldest entry.");
            }
            _targetRetry.Add((command, origin, now + TargetRetryWindowMs));
        }

        /// <summary>
        /// Keep defending an adopted street draw for a while. Committing roads regroups this
        /// machine's aggregates for as long as an operation's courses keep arriving, and a regroup
        /// deletes one of the two aggregates it joins - which one is a local decision the sender
        /// cannot see, so the draw can be dropped moments after it was written.
        /// </summary>
        private void HoldDraw(EntityNameCommand command, Entity target, long now)
        {
            var anchor = new float3(command.AnchorX, command.AnchorY, command.AnchorZ);
            string key = Infrastructure.ReplicationGuard.Key(command.TargetPrefabName, anchor);
            for (int i = _autoHold.Count - 1; i >= 0; i--)
            {
                AutoNameHold held = _autoHold[i];
                // One writer per street: a newer draw for the same street, or for the same point on
                // it, replaces the older one instead of alternating with it every retry tick.
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
                // Re-resolved from the anchor, not from the entity: the street a draw landed on may
                // have been merged into another one since. Null while its street is absent again.
                _autoHold[i].Target =
                    FindTarget(_autoHold[i].Kind, _autoHold[i].PrefabName, _autoHold[i].Anchor);
                i++;
            }

            // Newest wins. Two draws whose streets merged into one here now name the same entity,
            // and holding both would make it alternate between them every tick.
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

        /// <summary>
        /// One pending entry per target and per field, so an auto-name draw waiting for a street to
        /// arrive is not overwritten by a rename of a different one.
        /// </summary>
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

        /// <summary>
        /// Find the street that owns the edge the anchor sits on. The lookup goes through the game's
        /// own network search tree, so a name waiting for its road scales with local road density
        /// rather than with every edge in the city.
        /// </summary>
        private Entity ResolveStreet(string prefabName, float3 anchor)
        {
            JobHandle dependencies;
            NativeQuadTree<Entity, QuadTreeBoundsXZ> tree =
                _netSearch.GetNetSearchTree(readOnly: true, out dependencies);
            // Read on the main thread; a structural change follows immediately afterwards.
            dependencies.Complete();

            var candidates = new NativeList<Entity>(16, Allocator.Temp);
            try
            {
                var iterator = new NearNetIterator
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
                    float t;
                    float distance = MathUtils.Distance(curve.xz, anchor.xz, out t);
                    if (distance > StreetTolXZ) continue;
                    if (math.abs(MathUtils.Position(curve, t).y - anchor.y) > StreetTolY) continue;

                    Entity aggregate =
                        EntityManager.GetComponentData<global::Game.Net.Aggregated>(edge).m_Aggregate;
                    if (aggregate == Entity.Null || !EntityManager.Exists(aggregate) ||
                        !EntityManager.HasBuffer<global::Game.Net.AggregateElement>(aggregate) ||
                        EntityManager.HasComponent<Deleted>(aggregate)) continue;

                    // Roads of different classes never share an aggregate, so the aggregate prefab
                    // separates two streets whose centrelines cross at exactly this point.
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
            Entity prefab;
            // Several prefab collections can share a display name; only an object prefab can be
            // behind a named building or prop.
            if (!_prefabIndex.TryResolve(prefabName, IsObjectPrefab, out prefab)) return Entity.Null;

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
            Entity prefab;
            if (!_prefabIndex.TryResolve(prefabName, out prefab)) return Entity.Null;
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
                    float3 candidate;
                    if (!TryAnchor(kind, entities[i], out candidate)) continue;
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
        /// Overwrite the local auto-name draw with the sender's. Each slot is clamped to the length
        /// of the name list this machine has for that prefab, so a draw made against different
        /// content still lands on a real name instead of a raw locale key.
        /// </summary>
        private bool ApplyRandomIndices(Entity target, int[] indices)
        {
            if (!EntityManager.HasBuffer<RandomLocalizationIndex>(target)) return false;

            // Copied out before the target's own buffer is touched, so nothing holds a live buffer
            // handle across the resize below.
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

        /// <summary>
        /// How many names the prefab offers per slot, looked up where the game looks it up - on the
        /// prefab itself, or on the zone behind a growable building. Null when the prefab declares
        /// no name lists at all.
        /// </summary>
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

        /// <summary>Collects every net entity whose bounds reach the anchor box; callers filter.</summary>
        private struct NearNetIterator :
            INativeQuadTreeIterator<Entity, QuadTreeBoundsXZ>,
            IUnsafeQuadTreeIterator<Entity, QuadTreeBoundsXZ>
        {
            public Bounds3 Bounds;
            public NativeList<Entity> Results;

            public bool Intersect(QuadTreeBoundsXZ bounds) =>
                MathUtils.Intersect(bounds.m_Bounds, Bounds);

            public void Iterate(QuadTreeBoundsXZ bounds, Entity item)
            {
                if (MathUtils.Intersect(bounds.m_Bounds, Bounds)) Results.Add(item);
            }
        }
    }
}
