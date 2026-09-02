using System;
using System.Collections.Generic;
using System.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.Infrastructure;
using Game.Agents;
using Game.Buildings;
using Game.Citizens;
using Game.Common;
using Game.Companies;
using Game.Economy;
using Game.Prefabs;
using Game.Simulation;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    // Turning a page's properties into local entities: resolve by identity and position, hold
    // what cannot be matched yet for a later retry, and keep the resolved set cached so the
    // apply pass has somewhere to start from.
    public partial class ResidentialOccupancySyncSystem
    {
        private void ResolveOrPend(OccupancyProperty wanted, uint sweepId, long now,
            ObjectSearch.Batch search, NativeList<Entity> candidates)
        {
            TraceReceivedRoster(wanted);
            ObserveIncomingRoster(wanted, sweepId);
            bool ambiguous;
            Entity property = ResolveProperty(wanted, search, candidates, out ambiguous);
            if (property != Entity.Null && Cache(property, wanted, sweepId))
            {
                PendingProperty newerPending;
                if (!_pending.TryGetValue(wanted.Identity, out newerPending) ||
                    newerPending.Property.Revision <= wanted.Revision)
                    _pending.Remove(wanted.Identity);
                _resolved++;
                return;
            }
            if (ambiguous) _ambiguous++;
            else _unresolved++;

            PropertyRentIdentity identity = wanted.Identity;
            PendingProperty pending;
            if (_pending.TryGetValue(identity, out pending))
            {
                if (pending.Property.Revision <= wanted.Revision)
                {
                    bool newer = pending.Property.Revision < wanted.Revision;
                    pending.Property = wanted;
                    pending.SweepId = sweepId;
                    if (newer)
                    {
                        pending.ExpiresMs = now + ResolveTimeoutMs;
                        pending.NextAttemptMs = now + ResolveRetryMs;
                        long retry = pending.NextAttemptMs;
                        if (_nextPendingPumpMs == 0 || retry < _nextPendingPumpMs)
                            _nextPendingPumpMs = retry;
                    }
                }
                return;
            }
            if (_pending.Count >= MaxPendingIdentities)
            {
                _cacheDrops++;
                return;
            }
            _pending[identity] = new PendingProperty
            {
                Property = wanted,
                SweepId = sweepId,
                ExpiresMs = now + ResolveTimeoutMs,
                NextAttemptMs = now + ResolveRetryMs,
            };
            _pendingOrder.Enqueue(identity);
            long firstRetry = now + ResolveRetryMs;
            if (_nextPendingPumpMs == 0 || firstRetry < _nextPendingPumpMs)
                _nextPendingPumpMs = firstRetry;
        }

        private void RetryPending(long now, ObjectSearch.Batch search, NativeList<Entity> candidates)
        {
            if (_pending.Count == 0) return;
            int examined = 0;
            PropertyRentIdentity identity;
            while (examined++ < MaxPendingRetriesPerPump && _pendingOrder.TryDequeue(out identity))
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
                Entity property = ResolveProperty(pending.Property, search, candidates, out ambiguous);
                if (property != Entity.Null &&
                    Cache(property, pending.Property, pending.SweepId))
                {
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

        /// <summary>
        /// Find the local building a roster entry describes. Position is the primary identity;
        /// prefab only breaks a same-distance tie because a level change swaps prefab in place.
        ///
        /// That fallback is what keeps occupancy working across a level change. A building that
        /// levels up carries a different prefab, and the two peers do not change level at the same
        /// moment - the renovation each of them runs is given its own build rate. Insisting on the
        /// prefab would silently stop syncing that house until the levels happened to agree, which
        /// is exactly the case where the two cities visibly disagree. The same reasoning is why
        /// growable realization matches on the nearest grown building.
        /// </summary>
        private Entity ResolveProperty(OccupancyProperty wanted, ObjectSearch.Batch search,
            NativeList<Entity> candidates, out bool ambiguous)
        {
            ambiguous = false;
            Entity prefab;
            _prefabIndex.TryResolve(wanted.PrefabName,
                candidate => EntityManager.HasComponent<BuildingPropertyData>(candidate),
                out prefab);

            float3 anchor = new float3(wanted.AnchorX, wanted.AnchorY, wanted.AnchorZ);
            search.CollectNear(anchor, AnchorSearchRadius, candidates);

            Entity mapped;
            if (_propertiesByIdentity.TryGetValue(wanted.Identity, out mapped) &&
                IsLiveProperty(mapped) && PositionMatchesAnchor(mapped, wanted.Identity) &&
                CanClaimProperty(mapped, wanted.Identity))
                return mapped;

            Entity best = Entity.Null;
            float bestDistance = 0f;
            bool bestExact = false, bestAmbiguous = false;
            for (int i = 0; i < candidates.Length; i++)
            {
                Entity candidate = candidates[i];
                if (!IsLiveProperty(candidate)) continue;
                float distance = math.distancesq(
                    EntityManager.GetComponentData<global::Game.Objects.Transform>(candidate)
                        .m_Position.xz, anchor.xz);
                if (distance > AnchorMatchDistance * AnchorMatchDistance) continue;
                if (!CanClaimProperty(candidate, wanted.Identity)) continue;
                bool exact = prefab != Entity.Null &&
                             EntityManager.GetComponentData<PrefabRef>(candidate).m_Prefab == prefab;
                Consider(candidate, distance, exact, ref best, ref bestDistance,
                    ref bestExact, ref bestAmbiguous);
            }

            ambiguous = bestAmbiguous;
            return best != Entity.Null && !bestAmbiguous ? best : Entity.Null;
        }

        private static void Consider(Entity candidate, float distance, bool exact,
            ref Entity best, ref float bestDistance, ref bool bestExact, ref bool ambiguous)
        {
            if (best == Entity.Null || distance < bestDistance - AmbiguousDistanceEpsilon)
            {
                best = candidate;
                bestDistance = distance;
                bestExact = exact;
                ambiguous = false;
                return;
            }
            if (math.abs(distance - bestDistance) > AmbiguousDistanceEpsilon || candidate == best)
                return;
            if (exact && !bestExact)
            {
                best = candidate;
                bestDistance = distance;
                bestExact = true;
                ambiguous = false;
                return;
            }
            if (!exact && bestExact) return;
            ambiguous = true;
        }

        private bool CanClaimProperty(Entity property, PropertyRentIdentity wanted)
        {
            CachedProperty owner;
            if (!_cache.TryGetValue(property, out owner) || owner.Identity.Equals(wanted) ||
                SameAnchor(owner.Identity, wanted)) return true;

            // A moved entity may shed its stale cache ownership. A house which still stands at its
            // old anchor must never be borrowed for a roster whose real house has not appeared yet.
            return !PositionMatchesAnchor(property, owner.Identity);
        }

        private static bool SameAnchor(PropertyRentIdentity first, PropertyRentIdentity second)
        {
            float dx = first.AnchorX - second.AnchorX;
            float dz = first.AnchorZ - second.AnchorZ;
            return dx * dx + dz * dz <= AnchorMatchDistance * AnchorMatchDistance;
        }

        private bool PositionMatchesAnchor(Entity property, PropertyRentIdentity identity)
        {
            if (!IsLiveProperty(property)) return false;
            float3 position = EntityManager
                .GetComponentData<global::Game.Objects.Transform>(property).m_Position;
            float dx = position.x - identity.AnchorX;
            float dz = position.z - identity.AnchorZ;
            return dx * dx + dz * dz <= AnchorMatchDistance * AnchorMatchDistance;
        }

        /// <summary>
        /// Installs one half of the property-identity bijection. False means the candidate is still
        /// owned by another anchor (or the bounded cache is full), so the caller must keep the
        /// roster pending instead of treating it as resolved.
        /// </summary>
        private bool Cache(Entity property, OccupancyProperty wanted, uint sweepId)
        {
            Entity alreadyMapped;
            if (_propertiesByIdentity.TryGetValue(wanted.Identity, out alreadyMapped) &&
                alreadyMapped != property && IsLiveProperty(alreadyMapped) &&
                PositionMatchesAnchor(alreadyMapped, wanted.Identity))
                return false;

            int bucket = (int)(EntityManager.GetSharedComponent<UpdateFrame>(property).m_Index %
                               UpdatePartitions);
            CachedProperty cached;
            if (_cache.TryGetValue(property, out cached))
            {
                bool changedIdentity = !cached.Identity.Equals(wanted.Identity);
                bool sameAnchor = changedIdentity && SameAnchor(cached.Identity, wanted.Identity);
                if (changedIdentity && !sameAnchor &&
                    PositionMatchesAnchor(property, cached.Identity))
                    return false;

                // Revisions are issued from one world-global counter, so even a delayed page from
                // before an object move must not roll a newer cross-anchor mapping back.
                if (wanted.Revision < cached.Revision)
                {
                    _stalePages++;
                    // A stale page for some other identity did not resolve that identity. Keep it
                    // pending; a newer queued page may be the legitimate in-place/move migration.
                    return !changedIdentity;
                }
                if (wanted.Revision == cached.Revision)
                {
                    if (changedIdentity) return false;
                    if (sweepId == _clientSweepId) cached.LastSeenSweep = sweepId;
                    return true;
                }
            }
            else
            {
                if (_cache.Count >= MaxCachedProperties)
                {
                    _cacheDrops++;
                    return false;
                }
                cached = new CachedProperty();
                _cache[property] = cached;
            }

            if (cached.Identity.PrefabName != null && !cached.Identity.Equals(wanted.Identity))
                UnregisterResolvedProperty(cached.Identity, property);
            cached.Identity = wanted.Identity;
            cached.Prefab = EntityManager.GetComponentData<PrefabRef>(property).m_Prefab;
            cached.Revision = wanted.Revision;
            cached.ConstructionSpeed = wanted.ConstructionSpeed;
            cached.HasElectricityConsumer = wanted.HasElectricityConsumer;
            cached.ElectricityFulfilledConsumption = wanted.ElectricityFulfilledConsumption;
            cached.HasWaterConsumer = wanted.HasWaterConsumer;
            cached.WaterFulfilledFresh = wanted.WaterFulfilledFresh;
            cached.WaterFulfilledSewage = wanted.WaterFulfilledSewage;
            cached.Households = wanted.Households;
            cached.Bucket = bucket;
            cached.LastSeenSweep = sweepId;
            cached.RemoveAfterApply = false;
            RegisterResolvedProperty(wanted.Identity, property);
            AddToCacheBucket(bucket, property);
            MarkDirty(property);
            return true;
        }

        private void PruneCacheAfterCompleteSweep(uint sweepId, ulong revisionWatermark)
        {
            _cacheScratch.Clear();
            foreach (KeyValuePair<Entity, CachedProperty> pair in _cache)
                if (pair.Value.LastSeenSweep != sweepId) _cacheScratch.Add(pair.Key);
            for (int i = 0; i < _cacheScratch.Count; i++)
            {
                Entity property = _cacheScratch[i];
                CachedProperty cached;
                if (!_cache.TryGetValue(property, out cached)) continue;
                // A delayed older sweep must never erase a roster learned at a revision beyond
                // that sweep's closing watermark.
                if (cached.Revision > revisionWatermark) continue;
                if (!IsLiveProperty(property))
                {
                    if (RemoveCachedProperty(property)) _pruned++;
                    continue;
                }
                // Keep a local tombstone until GameSimulation has drained or transferred every
                // renter. Dropping the cache here would lose the only safe structural-write point.
                cached.Households = new OccupancyHousehold[0];
                if (revisionWatermark > cached.Revision) cached.Revision = revisionWatermark;
                cached.LastSeenSweep = sweepId;
                cached.RemoveAfterApply = true;
                MarkDirty(property);
            }
            _cacheScratch.Clear();
        }

        private bool RemoveCachedProperty(Entity property)
        {
            CachedProperty cached;
            if (!_cache.TryGetValue(property, out cached)) return false;
            UnregisterResolvedProperty(cached.Identity, property);
            return _cache.Remove(property);
        }

        private void AddToCacheBucket(int bucket, Entity property)
        {
            if (_cacheBucketMembers[bucket].Add(property)) _cacheBuckets[bucket].Add(property);
        }

        private void MarkDirty(Entity property)
        {
            if (!_dirtyMembers.Add(property)) return;
            _dirty.Add(property);
            // Shedding the oldest is safe: the entry is still cached and still in the rolling
            // partition, so it is repaired on the next pass over its bucket rather than lost.
            if (_dirty.Count <= MaxDirtyProperties) return;
            _dirtyMembers.Remove(_dirty[0]);
            _dirty.RemoveAt(0);
        }

        // ---- Apply ------------------------------------------------------------

        /// <summary>
        /// Properties whose roster just changed are reconciled first; whatever budget is left goes
        /// to one rolling partition, which is what repairs drift the host never reported (a local
        /// death, a local birth the host did not have).
        /// </summary>
        private void ApplyPending(int bucket)
        {
            _budget.Reset();
            _appliedThisUpdate.Clear();
            PruneSettling();
            RepairStagedTransfers();
            ApplyCitizenRetirements();

            // Anything created last update is re-examined now: the game's own initialization has
            // since run over those residents, randomising the very fields the roster specifies.
            for (int i = 0; i < _reapply.Count; i++) MarkDirty(_reapply[i]);
            _reapply.Clear();

            int processed = 0;
            while (processed < _dirty.Count && !_budget.Exhausted)
            {
                Entity property = _dirty[processed++];
                _dirtyMembers.Remove(property);
                ApplyOne(property);
            }
            if (processed > 0) _dirty.RemoveRange(0, processed);

            if (!_budget.Exhausted) ApplyBucket(bucket);
            _appliedThisUpdate.Clear();
            SweepUnreachableHouseholds();
        }
    }
}
