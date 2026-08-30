using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.Infrastructure;
using Game;
using Game.Buildings;
using Game.Common;
using Game.Objects;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    // The client's side: resolving each entry in a page against a local property - by identity
    // first, then by position - holding what cannot be matched yet, and applying the rents once
    // it can. Ends with the periodic stats line that says how well that is going.
    public partial class PropertyRentSyncSystem
    {
        private void DropIncomingPages()
        {
            if (_incoming.IsEmpty) return;
            lock (_incoming)
            {
                PropertyRentSnapshot ignored;
                while (_incoming.TryDequeue(out ignored)) _droppedPages++;
            }
        }

        private void DrainIncoming(long now, ObjectSearch.Batch search,
            NativeList<Entity> candidates, int maxPages)
        {
            PropertyRentSnapshot snapshot;
            int pages = 0;
            while (pages < maxPages && _incoming.TryDequeue(out snapshot))
            {
                pages++;
                _receivedPages++;
                NotePageContinuity(snapshot);
                for (int i = 0; i < snapshot.Entries.Count; i++)
                    ResolveOrPend(snapshot.Entries[i], snapshot.SweepId, now, search, candidates);
                if (snapshot.EndOfSweep && _clientSweepIntact &&
                    snapshot.SweepId == _clientSweepId &&
                    snapshot.PageIndex + 1 == _clientNextPage)
                    PruneCacheAfterCompleteSweep(snapshot.SweepId);
            }
        }

        private void NotePageContinuity(PropertyRentSnapshot snapshot)
        {
            if (snapshot.SweepId != _clientSweepId)
            {
                _clientSweepId = snapshot.SweepId;
                _clientNextPage = 0;
                _clientSweepIntact = snapshot.PageIndex == 0;
            }
            if (snapshot.PageIndex != _clientNextPage) _clientSweepIntact = false;
            if (snapshot.PageIndex >= _clientNextPage)
                _clientNextPage = snapshot.PageIndex + 1;
        }

        private void ResolveOrPend(PropertyRentEntry entry, uint sweepId, long now,
            ObjectSearch.Batch search, NativeList<Entity> candidates)
        {
            bool ambiguous;
            Entity property = ResolveProperty(entry, search, candidates, out ambiguous);
            if (property != Entity.Null)
            {
                Cache(property, entry, sweepId);
                _pending.Remove(entry.Identity);
                _resolved++;
                return;
            }
            if (ambiguous) _ambiguous++;
            else _unresolved++;

            PropertyRentIdentity identity = entry.Identity;
            PendingProperty pending;
            if (_pending.TryGetValue(identity, out pending))
            {
                pending.Entry = entry;
                pending.SweepId = sweepId;
                return;
            }
            if (_pending.Count >= MaxPendingIdentities)
            {
                _cacheDrops++;
                return;
            }
            _pending[identity] = new PendingProperty
            {
                Entry = entry,
                SweepId = sweepId,
                ExpiresMs = now + ResolveTimeoutMs,
                NextAttemptMs = now + ResolveRetryMs,
            };
            _pendingOrder.Enqueue(identity);
            long firstRetry = now + ResolveRetryMs;
            if (_nextPendingPumpMs == 0 || firstRetry < _nextPendingPumpMs)
                _nextPendingPumpMs = firstRetry;
        }

        private void RetryPending(long now, ObjectSearch.Batch search,
            NativeList<Entity> candidates)
        {
            if (_pending.Count == 0) return;
            int examined = 0;
            PropertyRentIdentity identity;
            while (examined++ < MaxPendingRetriesPerUpdate &&
                   _pendingOrder.TryDequeue(out identity))
            {
                PendingProperty pending;
                if (!_pending.TryGetValue(identity, out pending)) continue;
                if (pending.ExpiresMs <= now)
                {
                    _pending.Remove(identity);
                    _expired++;
                    continue;
                }
                if (pending.NextAttemptMs > now)
                {
                    _pendingOrder.Enqueue(identity);
                    continue;
                }
                bool ambiguous;
                Entity property = ResolveProperty(pending.Entry, search, candidates, out ambiguous);
                if (property != Entity.Null)
                {
                    Cache(property, pending.Entry, pending.SweepId);
                    _pending.Remove(identity);
                    _resolved++;
                }
                else
                {
                    pending.NextAttemptMs = now + ResolveRetryMs;
                    _pendingOrder.Enqueue(identity);
                }
            }
        }

        private Entity ResolveProperty(PropertyRentEntry entry, ObjectSearch.Batch search,
            NativeList<Entity> candidates, out bool ambiguous)
        {
            ambiguous = false;
            Entity prefab;
            _prefabIndex.TryResolve(entry.PrefabName,
                candidate => EntityManager.HasComponent<BuildingPropertyData>(candidate),
                out prefab);

            float3 anchor = new float3(entry.AnchorX, entry.AnchorY, entry.AnchorZ);
            search.CollectNear(anchor, AnchorSearchRadius, candidates);
            Entity exact = Entity.Null, nearest = Entity.Null;
            float exactDistance = 0f, nearestDistance = 0f;
            bool exactAmbiguous = false, nearestAmbiguous = false;
            for (int i = 0; i < candidates.Length; i++)
            {
                Entity candidate = candidates[i];
                if (!IsLiveProperty(candidate)) continue;
                float distance = math.distancesq(
                    EntityManager.GetComponentData<global::Game.Objects.Transform>(candidate)
                        .m_Position.xz, anchor.xz);
                if (distance > AnchorMatchDistance * AnchorMatchDistance) continue;
                if (prefab != Entity.Null &&
                    EntityManager.GetComponentData<PrefabRef>(candidate).m_Prefab == prefab)
                    ConsiderRentCandidate(candidate, distance, ref exact, ref exactDistance,
                        ref exactAmbiguous);
                ConsiderRentCandidate(candidate, distance, ref nearest, ref nearestDistance,
                    ref nearestAmbiguous);
            }
            if (exact != Entity.Null && !exactAmbiguous) return exact;
            ambiguous = exact != Entity.Null ? exactAmbiguous : nearestAmbiguous;
            return nearest != Entity.Null && !nearestAmbiguous ? nearest : Entity.Null;
        }

        private static void ConsiderRentCandidate(Entity candidate, float distance, ref Entity best,
            ref float bestDistance, ref bool ambiguous)
        {
            if (best == Entity.Null || distance < bestDistance - AmbiguousDistanceEpsilon)
            {
                best = candidate;
                bestDistance = distance;
                ambiguous = false;
                return;
            }
            if (math.abs(distance - bestDistance) <= AmbiguousDistanceEpsilon && candidate != best)
                ambiguous = true;
        }

        private void Cache(Entity property, PropertyRentEntry entry, uint sweepId)
        {
            int bucket = (int)(EntityManager.GetSharedComponent<UpdateFrame>(property).m_Index %
                               UpdatePartitions);
            CachedProperty cached;
            if (_cache.TryGetValue(property, out cached))
            {
                cached.Identity = entry.Identity;
                cached.Prefab = EntityManager.GetComponentData<PrefabRef>(property).m_Prefab;
                cached.Rent = entry.Rent;
                cached.Bucket = bucket;
                cached.LastSeenSweep = sweepId;
                AddToCacheBucket(bucket, property);
                return;
            }
            if (_cache.Count >= MaxCachedProperties)
            {
                _cacheDrops++;
                return;
            }
            _cache[property] = new CachedProperty
            {
                Identity = entry.Identity,
                Prefab = EntityManager.GetComponentData<PrefabRef>(property).m_Prefab,
                Rent = entry.Rent,
                Bucket = bucket,
                LastSeenSweep = sweepId,
            };
            AddToCacheBucket(bucket, property);
        }

        private void AddToCacheBucket(int bucket, Entity property)
        {
            if (_cacheBucketMembers[bucket].Add(property))
                _cacheBuckets[bucket].Add(property);
        }

        private void PruneCacheAfterCompleteSweep(uint sweepId)
        {
            _cacheScratch.Clear();
            foreach (KeyValuePair<Entity, CachedProperty> pair in _cache)
                if (pair.Value.LastSeenSweep != sweepId) _cacheScratch.Add(pair.Key);
            for (int i = 0; i < _cacheScratch.Count; i++)
                if (_cache.Remove(_cacheScratch[i])) _pruned++;
            _cacheScratch.Clear();
        }

        private void ApplyBucket(int bucket)
        {
            List<Entity> entities = _cacheBuckets[bucket];
            HashSet<Entity> members = _cacheBucketMembers[bucket];
            // Rebuild membership while compacting. This removes complete-sweep tombstones and any
            // legacy duplicate list entries, and preserves exactly one slot for each live cache.
            members.Clear();
            int write = 0;
            for (int i = 0; i < entities.Count; i++)
            {
                Entity property = entities[i];
                CachedProperty cached;
                if (!_cache.TryGetValue(property, out cached))
                {
                    continue;
                }
                // A stale entry may remain in its old bucket list after a local UpdateFrame move.
                // Do not delete the live cache now owned by the new bucket.
                if (cached.Bucket != bucket) continue;
                if (!MatchesCachedProperty(property, cached))
                {
                    _cache.Remove(property);
                    continue;
                }

                int currentBucket = (int)(EntityManager
                    .GetSharedComponent<UpdateFrame>(property).m_Index % UpdatePartitions);
                if (currentBucket != cached.Bucket)
                {
                    cached.Bucket = currentBucket;
                    AddToCacheBucket(currentBucket, property);
                    continue;
                }

                if (!members.Add(property)) continue;

                bool wrote = false;

                if (EntityManager.HasComponent<PropertyOnMarket>(property))
                {
                    PropertyOnMarket market =
                        EntityManager.GetComponentData<PropertyOnMarket>(property);
                    if (market.m_AskingRent != cached.Rent)
                    {
                        market.m_AskingRent = cached.Rent;
                        EntityManager.SetComponentData(property, market);
                        _appliedMarkets++;
                        wrote = true;
                    }
                }

                DynamicBuffer<Renter> renters = EntityManager.GetBuffer<Renter>(property, true);
                for (int j = 0; j < renters.Length; j++)
                {
                    Entity renter = renters[j].m_Renter;
                    if (!IsValidRenter(renter, property)) continue;
                    if (EntityManager.HasComponent<global::Game.Citizens.Household>(renter))
                        continue;
                    PropertyRenter propertyRenter =
                        EntityManager.GetComponentData<PropertyRenter>(renter);
                    if (propertyRenter.m_Rent == cached.Rent) continue;
                    propertyRenter.m_Rent = cached.Rent;
                    EntityManager.SetComponentData(renter, propertyRenter);
                    _appliedRenters++;
                    wrote = true;
                }
                if (wrote) _appliedProperties++;
                entities[write++] = property;
            }
            if (write < entities.Count) entities.RemoveRange(write, entities.Count - write);
        }

        private bool MatchesCachedProperty(Entity property, CachedProperty cached)
        {
            if (!IsLiveProperty(property)) return false;
            cached.Prefab = EntityManager.GetComponentData<PrefabRef>(property).m_Prefab;
            float3 position = EntityManager
                .GetComponentData<global::Game.Objects.Transform>(property).m_Position;
            float2 anchor = new float2(cached.Identity.AnchorX, cached.Identity.AnchorZ);
            return math.distancesq(position.xz, anchor) <=
                   AnchorMatchDistance * AnchorMatchDistance;
        }

        private void ReportStats(MultiplayerSession session, long now)
        {
            if (_lastStatsMs == 0) { _lastStatsMs = now; return; }
            if (now - _lastStatsMs < StatsIntervalMs) return;
            _lastStatsMs = now;

            if (session.Role == SessionRole.Host)
            {
                int clients = 0;
                foreach (Peer peer in session.Peers)
                    if (peer.Handshaked) clients++;
                long estimatedFanoutBytes = _sentBytes * clients;
                SyncLog.Detail(LogTopic.Residential, "PropertyRent/30s host: pages=" + _sentPages +
                    ", entries=" + _sentEntries + ", bytes=" + _sentBytes + ", clients=" + clients +
                    ", estimatedFanoutBytes=" + estimatedFanoutBytes + ", transportPendingBytes=" +
                    session.PendingSendBytes + ", changedPriority=" + _priorityChanges +
                    ", priorityQueued=" + _priority.Count + ", priorityDropped=" + _priorityDrops +
                    ", captureSkipped=" + _localCaptureSkips + ", identityCollision=" +
                    _localIdentityCollisions + ".");
            }
            else
            {
                SyncLog.Detail(LogTopic.Residential, "PropertyRent/30s client: pages=" +
                    _receivedPages + ", queueDropped=" + _droppedPages + ", cached=" + _cache.Count +
                    ", pending=" + _pending.Count + ", resolved=" + _resolved + ", unresolved=" +
                    _unresolved + ", ambiguous=" + _ambiguous + ", expired=" + _expired +
                    ", cacheDropped=" + _cacheDrops + ", pruned=" + _pruned + ", appliedProperties=" +
                    _appliedProperties + ", renterWrites=" + _appliedRenters + ", marketWrites=" +
                    _appliedMarkets + ".");
            }
            _sentPages = _sentEntries = _priorityChanges = _priorityDrops = 0;
            _localCaptureSkips = _localIdentityCollisions = 0;
            _sentBytes = 0;
            _receivedPages = _droppedPages = _resolved = _unresolved = _ambiguous = 0;
            _expired = _cacheDrops = _pruned = 0;
            _appliedProperties = _appliedRenters = _appliedMarkets = 0;
        }
    }
}
