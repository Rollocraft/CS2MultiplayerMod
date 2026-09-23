using System.Collections.Generic;
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.Infrastructure;
using Game.Prefabs;
using Game.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    public partial class ResidentialOccupancySyncSystem
    {
        private void ResolveOrPend(OccupancyProperty wanted, uint sweepId, long now,
            ObjectSearch.Batch search, NativeList<Entity> candidates)
        {
            TraceReceivedRoster(wanted);
            ObserveIncomingRoster(wanted, sweepId);
            Entity property = ResolveProperty(wanted, search, candidates, out bool ambiguous);
            if (property != Entity.Null && Cache(property, wanted, sweepId))
            {
                if (!_pending.TryGetValue(wanted.Identity, out PendingProperty newerPending) ||
                    newerPending.Property.Revision <= wanted.Revision)
                    _pending.Remove(wanted.Identity);
                _resolved++;
                return;
            }
            if (ambiguous) _ambiguous++;
            else _unresolved++;

            PropertyIdentity identity = wanted.Identity;
            if (_pending.TryGetValue(identity, out PendingProperty pending))
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
                        _propertyState.ScheduleRetry(pending.NextAttemptMs);
                    }
                }
                return;
            }
            if (!_propertyState.Hold(identity, new PendingProperty { Property = wanted }, sweepId, now,
                    ResolveTimeoutMs))
                _cacheDrops++;
        }

        private void RetryPending(long now, ObjectSearch.Batch search, NativeList<Entity> candidates)
        {
            _propertyState.RetryPending(now, MaxPendingRetriesPerPump,
                value =>
                {
                    Entity property = ResolveProperty(value.Property, search, candidates, out bool ambiguous);
                    if (property == Entity.Null) return false;
                    if (!Cache(property, value.Property, value.SweepId)) return false;
                    _resolved++;
                    return true;
                }, () => _expired++);
        }

        /// <summary>
        /// Position is identity; the prefab only breaks a tie, because a level change swaps the prefab at
        /// a different moment on each peer.
        /// </summary>
        private Entity ResolveProperty(OccupancyProperty wanted, ObjectSearch.Batch search,
            NativeList<Entity> candidates, out bool ambiguous)
        {
            ambiguous = false;
            // Validate an existing binding before resolving again.
            if (_propertiesByIdentity.TryGetValue(wanted.Identity, out Entity mapped) &&
                PositionMatchesAnchor(mapped, wanted.Identity) &&
                CanClaimProperty(mapped, wanted.Identity))
                return mapped;

            _prefabIndex.TryResolve(wanted.PrefabName,
                candidate => EntityManager.Exists(candidate) &&
                    EntityManager.HasComponent<BuildingPropertyData>(candidate), out Entity prefab);
            PropertyResolution result = PropertyEntityResolver.Resolve(EntityManager, search,
                candidates, new float3(wanted.AnchorX, wanted.AnchorY, wanted.AnchorZ),
                AnchorSearchRadius, AnchorMatchDistance, AmbiguousDistanceEpsilon, prefab,
                IsLiveProperty, PropertyPrefabPreference.BreakDistanceTie,
                candidate => CanClaimProperty(candidate, wanted.Identity));
            ambiguous = result.Ambiguous;
            return result.Entity;
        }

        private bool CanClaimProperty(Entity property, PropertyIdentity wanted)
        {
            if (!_cache.TryGetValue(property, out CachedProperty owner) || owner.Identity.Equals(wanted) ||
                SameAnchor(owner.Identity, wanted)) return true;

            // A house still at its old anchor is never borrowed for another roster.
            return !PositionMatchesAnchor(property, owner.Identity);
        }

        private static bool SameAnchor(PropertyIdentity first, PropertyIdentity second)
        {
            float dx = first.AnchorX - second.AnchorX;
            float dz = first.AnchorZ - second.AnchorZ;
            return dx * dx + dz * dz <= AnchorMatchDistance * AnchorMatchDistance;
        }

        private bool PositionMatchesAnchor(Entity property, PropertyIdentity identity)
        {
            if (!IsLiveProperty(property)) return false;
            float3 position = EntityManager
                .GetComponentData<global::Game.Objects.Transform>(property).m_Position;
            float dx = position.x - identity.AnchorX;
            float dz = position.z - identity.AnchorZ;
            return dx * dx + dz * dz <= AnchorMatchDistance * AnchorMatchDistance;
        }

        /// <summary>
        /// One half of the property-identity bijection. False: still owned by another anchor or the cache
        /// is full, so the roster stays pending.
        /// </summary>
        private bool Cache(Entity property, OccupancyProperty wanted, uint sweepId)
        {
            if (_propertiesByIdentity.TryGetValue(wanted.Identity, out Entity alreadyMapped) &&
                alreadyMapped != property && IsLiveProperty(alreadyMapped) &&
                PositionMatchesAnchor(alreadyMapped, wanted.Identity))
                return false;

            int bucket = (int)(EntityManager.GetSharedComponent<UpdateFrame>(property).m_Index %
                               UpdatePartitions);
            if (_cache.TryGetValue(property, out CachedProperty cached))
            {
                bool changedIdentity = !cached.Identity.Equals(wanted.Identity);
                bool sameAnchor = changedIdentity && SameAnchor(cached.Identity, wanted.Identity);
                if (changedIdentity && !sameAnchor &&
                    PositionMatchesAnchor(property, cached.Identity))
                    return false;

                // Revisions are world-global: a delayed page never rolls back a newer mapping.
                if (wanted.Revision < cached.Revision)
                {
                    _stalePages++;
                    // Stays pending: a newer queued page may be the legitimate migration.
                    return !changedIdentity;
                }
                if (wanted.Revision == cached.Revision)
                {
                    if (changedIdentity) return false;
                    if (sweepId == _propertyState.SweepId) cached.LastSeenSweep = sweepId;
                    return true;
                }

                // Baseline pages get a fresh revision even when unchanged: keep coverage, skip the reapply.
                if (!changedIdentity && !cached.RemoveAfterApply &&
                    OccupancyContentComparer.Same(in wanted, in cached.LastReceived))
                {
                    ulong previousRevision = cached.Revision;
                    cached.Revision = wanted.Revision;
                    cached.LastReceived = wanted;
                    cached.LastSeenSweep = sweepId;
                    _unchangedProperties++;
                    if (!_dirtyMembers.Contains(property) &&
                        !_reapplyRequested.Contains(property) &&
                        _appliedState.TryGetValue(property, out AppliedState applied) &&
                        applied.Revision == previousRevision)
                    {
                        applied.Revision = wanted.Revision;
                        _appliedState[property] = applied;
                    }
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
            cached.LastReceived = wanted;
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
                if (!_cache.TryGetValue(property, out CachedProperty cached)) continue;
                // An older sweep never erases a roster learned beyond its closing watermark.
                if (cached.Revision > revisionWatermark) continue;
                if (!IsLiveProperty(property))
                {
                    if (RemoveCachedProperty(property)) _pruned++;
                    continue;
                }
                // Tombstone until GameSimulation drains every renter: the only safe structural-write point.
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
            if (!_cache.TryGetValue(property, out CachedProperty cached)) return false;
            UnregisterResolvedProperty(cached.Identity, property);
            _appliedState.Remove(property);
            return _cache.Remove(property);
        }

        private void AddToCacheBucket(int bucket, Entity property)
        {
            if (_cacheBucketMembers[bucket].Add(property)) _cacheBuckets[bucket].Add(property);
        }

        private void MarkDirty(Entity property) =>
            // Shedding the oldest is safe: the rolling partition can still repair it.
            BoundedUniquePropertyList.Enqueue(_dirty, _dirtyMembers, property, MaxDirtyProperties);

        /// <summary>Changed properties first, then one rolling partition for unreported drift.</summary>
        private void ApplyPending()
        {
            _budget.Reset();
            _appliedThisUpdate.Clear();
            using (Diagnostics.SyncProfiler.Measure("Occupancy.Maintenance", Diagnostics.SyncZone.Residential))
            {
                PruneSettling();
                RepairStagedTransfers();
                ApplyCitizenRetirements();
            }

            using (Diagnostics.SyncProfiler.Measure("Occupancy.Apply", Diagnostics.SyncZone.Residential))
            {
                // Initialization has since randomized the fields the roster specifies.
                for (int i = 0; i < _reapply.Count; i++) MarkDirty(_reapply[i]);
                _reapply.Clear();
                _reapplyRequested.Clear();

                int processed = 0;
                while (processed < _dirty.Count && !_budget.Exhausted)
                {
                    Entity property = _dirty[processed++];
                    _dirtyMembers.Remove(property);
                    ApplyOne(property);
                }
                if (processed > 0) _dirty.RemoveRange(0, processed);

                if (!_budget.Exhausted && _repairScanCadence.TryTakePartition(
                        _simulationSystem.selectedSpeed, UpdatePartitions, out int repairBucket))
                    ApplyBucket(repairBucket);
                _appliedThisUpdate.Clear();
            }
            using (Diagnostics.SyncProfiler.Measure("Occupancy.Unreachable", Diagnostics.SyncZone.Residential))
                SweepUnreachableHouseholds();
        }
    }
}
