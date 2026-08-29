using System;
using System.Collections.Generic;
using System.Diagnostics;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Diagnostics;
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
    public partial class ResidentialOccupancySyncSystem
    {
        /// <summary>
        /// Citizen flag bits the host owns: who the person is. The rest of the word is local
        /// behaviour — walking to an outside connection, looking for a partner, riding a bicycle —
        /// and overwriting it would interrupt whatever this machine's citizen is in the middle of.
        /// </summary>
        private const short HostOwnedCitizenFlags =
            (short)(CitizenFlags.AgeBit1 | CitizenFlags.AgeBit2 | CitizenFlags.Male |
                    CitizenFlags.EducationBit1 | CitizenFlags.EducationBit2 |
                    CitizenFlags.EducationBit3 | CitizenFlags.FailedEducationBit1 |
                    CitizenFlags.FailedEducationBit2 | CitizenFlags.Tourist |
                    CitizenFlags.Commuter);

        // MovedIn is deliberately local: setting the host's bit before this peer's residents
        // arrive suppresses the native CitizensMovedIn statistic and leaves population short.
        private const byte HouseholdFlagMask =
            (byte)(HouseholdFlags.Tourist | HouseholdFlags.Commuter);

        /// <summary>
        /// Simulation frames a freshly created household is left alone for. Its citizens and pets
        /// only enter their buffers when the game's own initialization systems run, one frame
        /// later; counting members before that would create the same family twice.
        /// </summary>
        private const uint SettleFrames = 2 * UpdateIntervalFrames;

        /// <summary>Cap on the queue of just-changed properties; the bucket rotation is the backstop.</summary>
        private const int MaxDirtyProperties = 8192;

        /// <summary>
        /// How long a household with nowhere to live is left alone before it is retired. Long
        /// enough that a client which just loaded the host's world does not evict the families the
        /// host is in the middle of re-housing.
        /// </summary>
        private const uint UnreachableGraceFrames = 8192;
        private const uint BootstrapRetirementGraceFrames = 8192;

        private const int MaxUnreachableRetiredPerUpdate = 8;

        private readonly Dictionary<Entity, uint> _settling = new Dictionary<Entity, uint>();
        private readonly Dictionary<Entity, uint> _unreachableSince = new Dictionary<Entity, uint>();
        private readonly Dictionary<Entity, uint> _unboundHouseholdSince =
            new Dictionary<Entity, uint>();
        private readonly Dictionary<Entity, uint> _unboundCitizenSince =
            new Dictionary<Entity, uint>();
        private readonly Dictionary<int, List<Entity>> _bootstrapHouseholdIndex =
            new Dictionary<int, List<Entity>>();
        private readonly Dictionary<int, List<Entity>> _bootstrapCitizenIndex =
            new Dictionary<int, List<Entity>>();
        private bool _bootstrapIdentityIndexBuilt;
        private readonly List<Entity> _localHouseholds = new List<Entity>();
        private readonly List<Entity> _memberScratch = new List<Entity>();
        private readonly HashSet<Entity> _claimedHouseholds = new HashSet<Entity>();
        private readonly HashSet<Entity> _claimedCitizens = new HashSet<Entity>();
        private readonly HashSet<Entity> _claimedPets = new HashSet<Entity>();
        private readonly HashSet<ulong> _wantedHouseholdIds = new HashSet<ulong>();
        private readonly HashSet<ulong> _wantedCitizenIds = new HashSet<ulong>();
        private readonly List<string> _missingPetPrefabs = new List<string>();
        private readonly Dictionary<string, int> _localVehiclePrefabCounts =
            new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _matchedVehiclePrefabCounts =
            new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly HashSet<string> _vehicleSpawnWarnings =
            new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<Entity, Entity> _arrivalSources =
            new Dictionary<Entity, Entity>();
        private readonly List<Entity> _settlingScratch = new List<Entity>();
        private readonly HashSet<Entity> _appliedThisUpdate = new HashSet<Entity>();
        private readonly HashSet<Entity> _unreachableSeen = new HashSet<Entity>();
        private readonly List<Entity> _reapply = new List<Entity>();
        private readonly Budget _budget = new Budget();
        private bool _applyWarned;
        private bool _arrivalSourceWarned;

        private sealed class Budget
        {
            public int Properties;
            public int HouseholdsCreated;
            public int CitizensCreated;
            public int VehiclesCreated;
            public int HouseholdsRetired;

            public void Reset()
            {
                Properties = 0;
                HouseholdsCreated = 0;
                CitizensCreated = 0;
                VehiclesCreated = 0;
                HouseholdsRetired = 0;
            }

            public bool Exhausted =>
                Properties >= MaxPropertiesAppliedPerUpdate ||
                HouseholdsCreated >= MaxHouseholdsCreatedPerUpdate ||
                CitizensCreated >= MaxCitizensCreatedPerUpdate ||
                VehiclesCreated >= MaxVehiclesCreatedPerUpdate ||
                HouseholdsRetired >= MaxHouseholdsRetiredPerUpdate;
        }

        /// <summary>
        /// Turn arrived pages into resolved cache entries. Read-only against ECS and cheap enough
        /// to run from the city-state pump every frame; the structural writes stay in
        /// <see cref="ApplyPending"/>, which runs at the simulation cadence.
        /// </summary>
        internal void PumpIncoming()
        {
            MultiplayerService service = Mod.Service;
            if (service == null || !service.GameplaySyncReady) return;
            if (service.Session.Role == SessionRole.Host)
            {
                DropIncomingPages();
                return;
            }

            long now = service.NowMs;
            bool retryDue = _pending.Count > 0 && now >= _nextPendingPumpMs;
            if (_incoming.IsEmpty && !retryDue) return;

            ObjectSearch.Batch search = _objectSearch.BeginBatch();
            var candidates = new NativeList<Entity>(16, Allocator.Temp);
            try
            {
                DrainIncoming(now, search, candidates, MaxPumpPages);
                if (retryDue)
                {
                    RetryPending(now, search, candidates);
                    _nextPendingPumpMs = now + ResolveRetryMs;
                }
            }
            finally
            {
                candidates.Dispose();
            }
        }

        private void DrainIncoming(long now, ObjectSearch.Batch search,
            NativeList<Entity> candidates, int maxPages)
        {
            ResidentialOccupancySnapshot snapshot;
            int pages = 0;
            while (pages < maxPages && _incoming.TryDequeue(out snapshot))
            {
                pages++;
                _receivedPages++;
                bool trackedSweep = NotePageContinuity(snapshot);
                for (int i = 0; i < snapshot.Departures.Count; i++)
                    ObserveDepartureRecord(snapshot.Departures[i], snapshot.SweepId);
                for (int i = 0; i < snapshot.CitizenDepartures.Count; i++)
                    ObserveCitizenDepartureRecord(snapshot.CitizenDepartures[i], snapshot.SweepId);
                for (int i = 0; i < snapshot.Properties.Count; i++)
                    ResolveOrPend(snapshot.Properties[i], snapshot.SweepId, now, search, candidates);
                if (trackedSweep && snapshot.EndOfSweep && snapshot.SweepComplete &&
                    _clientSweepIntact &&
                    snapshot.SweepId == _clientSweepId &&
                    snapshot.PageIndex + 1 == _clientNextPage)
                    PruneCacheAfterCompleteSweep(snapshot.SweepId,
                        snapshot.RevisionWatermark);
            }
        }

        private bool NotePageContinuity(ResidentialOccupancySnapshot snapshot)
        {
            if (snapshot.SweepId != _clientSweepId)
            {
                if (_clientSweepId != 0 && !IsNewerSerial(snapshot.SweepId, _clientSweepId))
                    return false;
                _clientSweepId = snapshot.SweepId;
                _clientNextPage = 0;
                _clientSweepIntact = snapshot.PageIndex == 0;
            }
            if (snapshot.PageIndex != _clientNextPage) _clientSweepIntact = false;
            if (snapshot.PageIndex >= _clientNextPage)
                _clientNextPage = snapshot.PageIndex + 1;
            return true;
        }

        private static bool IsNewerSerial(uint candidate, uint current) =>
            candidate != current && unchecked((int)(candidate - current)) > 0;

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

        private void RepairStagedTransfers()
        {
            uint now = _simulationSystem.frameIndex;
            PruneStagedTransferCooldowns(now);
            if (_stagedTransfers.Count == 0) return;
            _stagedTransferScratch.Clear();
            foreach (KeyValuePair<ulong, StagedTransfer> pair in _stagedTransfers)
            {
                ulong householdId = pair.Key;
                StagedTransfer staged = pair.Value;
                Entity mapped, desiredDestination;
                bool mappingValid = TryResolveHousehold(householdId, out mapped) &&
                                    mapped == staged.Household;
                bool destinationValid = TryGetDesiredProperty(householdId,
                    out desiredDestination) && desiredDestination == staged.Destination;
                if (!mappingValid || !destinationValid)
                {
                    RepairStagedTransferLink(staged);
                    _stagedTransferScratch.Add(householdId);
                    continue;
                }
                if (IsHouseholdAtProperty(staged.Household, staged.Destination))
                {
                    _stagedTransferCooldownUntil.Remove(householdId);
                    _stagedTransferScratch.Add(householdId);
                    continue;
                }
                if (now - staged.StartedFrame >= UnreachableGraceFrames)
                {
                    RepairStagedTransferLink(staged);
                    StartStagedTransferCooldown(householdId, now);
                    MarkDirty(staged.Destination);
                    _stagedTransferScratch.Add(householdId);
                    continue;
                }
                MarkDirty(staged.Destination);
            }
            for (int i = 0; i < _stagedTransferScratch.Count; i++)
                _stagedTransfers.Remove(_stagedTransferScratch[i]);
            _stagedTransferScratch.Clear();
        }

        /// <summary>
        /// A staged swap removes the source buffer entry while retaining PropertyRenter until the
        /// native rent action commits. If that action never commits, repair whichever live property
        /// PropertyRenter currently names. This avoids both a missing source entry and restoring an
        /// obsolete source after the native action already selected a different valid property.
        /// </summary>
        private void RepairStagedTransferLink(StagedTransfer staged)
        {
            if (staged == null || staged.Household == Entity.Null ||
                !EntityManager.Exists(staged.Household) ||
                !EntityManager.HasComponent<Household>(staged.Household) ||
                EntityManager.HasComponent<Deleted>(staged.Household)) return;

            if (EntityManager.HasComponent<PropertyRenter>(staged.Household))
            {
                Entity current = EntityManager.GetComponentData<PropertyRenter>(staged.Household)
                    .m_Property;
                if (IsLiveProperty(current))
                {
                    IsHouseholdAtProperty(staged.Household, current);
                    MarkDirty(current);
                }
                else
                {
                    // A dangling one-way link cannot be completed. Removing it lets the ordinary
                    // desired-location reconciler enqueue a clean rent action on its next pass.
                    EntityManager.RemoveComponent<PropertyRenter>(staged.Household);
                }
            }

            if (IsLiveProperty(staged.Source)) MarkDirty(staged.Source);
            if (IsLiveProperty(staged.Destination)) MarkDirty(staged.Destination);
        }

        private void RestoreAllStagedTransferLinks()
        {
            foreach (KeyValuePair<ulong, StagedTransfer> pair in _stagedTransfers)
                RepairStagedTransferLink(pair.Value);
        }

        private void StartStagedTransferCooldown(ulong householdId, uint now)
        {
            _stagedTransferCooldownUntil[householdId] = now + UnreachableGraceFrames;
        }

        private void PruneStagedTransferCooldowns(uint now)
        {
            if (_stagedTransferCooldownUntil.Count == 0) return;
            _stagedTransferScratch.Clear();
            foreach (KeyValuePair<ulong, uint> pair in _stagedTransferCooldownUntil)
                if (!FramePrecedes(now, pair.Value)) _stagedTransferScratch.Add(pair.Key);
            for (int i = 0; i < _stagedTransferScratch.Count; i++)
                _stagedTransferCooldownUntil.Remove(_stagedTransferScratch[i]);
            _stagedTransferScratch.Clear();
        }

        private bool TrackStagedTransfer(ulong householdId, Entity household, Entity source,
            Entity destination)
        {
            StagedTransfer existing;
            if (_stagedTransfers.TryGetValue(householdId, out existing))
            {
                existing.Household = household;
                existing.Source = source;
                existing.Destination = destination;
                return true;
            }
            uint now = _simulationSystem.frameIndex;
            uint cooldownUntil;
            if (_stagedTransferCooldownUntil.TryGetValue(householdId, out cooldownUntil))
            {
                if (FramePrecedes(now, cooldownUntil)) return false;
                _stagedTransferCooldownUntil.Remove(householdId);
            }
            if (_stagedTransfers.Count >= MaxStagedTransfers) return false;
            _stagedTransfers[householdId] = new StagedTransfer
            {
                Household = household,
                Source = source,
                Destination = destination,
                StartedFrame = now,
            };
            return true;
        }

        private static bool FramePrecedes(uint frame, uint deadline) =>
            unchecked((int)(frame - deadline)) < 0;

        private void ApplyCitizenRetirements()
        {
            int remaining = MaxCitizensRetiredPerUpdate;
            while (remaining-- > 0 && _pendingCitizenRetirements.TryDequeue(out ulong citizenId))
            {
                _pendingCitizenRetirementIds.Remove(citizenId);
                DesiredCitizenLocation desired;
                if (!_desiredCitizens.TryGetValue(citizenId, out desired) || desired.Active)
                    continue;

                Entity citizen;
                if (!TryResolveCitizen(citizenId, out citizen)) continue;

                // A whole-household departure is executed by HouseholdMoveAwaySystem. Leave a
                // resident still linked to that family for the native emigration transaction; the
                // exact-person path below is for death/individual departure and detached strays.
                DesiredHouseholdLocation householdLocation;
                Entity household;
                if (desired.HouseholdId != 0 &&
                    _desiredHouseholds.TryGetValue(desired.HouseholdId, out householdLocation) &&
                    !householdLocation.Active &&
                    TryResolveHousehold(desired.HouseholdId, out household) &&
                    CitizenBelongsToHousehold(citizen, household))
                {
                    if (EntityManager.HasComponent<PropertyRenter>(household))
                    {
                        Entity property = EntityManager.GetComponentData<PropertyRenter>(household)
                            .m_Property;
                        if (property != Entity.Null && EntityManager.Exists(property))
                            MarkDirty(property);
                    }
                    continue;
                }

                UnbindCitizen(citizenId);
                if (!EntityManager.HasComponent<Deleted>(citizen))
                    EntityManager.AddComponent<Deleted>(citizen);
                _removedCitizens++;
            }
        }

        /// <summary>
        /// On a client every household that belongs in the city is a renter the host reported.
        /// Nothing re-houses the rest: the system that would is held, so a family whose building
        /// was demolished would otherwise stay in the city forever with no home and no way out.
        /// After a grace period they take the game's ordinary emigration path.
        /// </summary>
        private void SweepUnreachableHouseholds()
        {
            if (_unreachableHouseholds.IsEmptyIgnoreFilter)
            {
                if (_unreachableSince.Count > 0) _unreachableSince.Clear();
                return;
            }
            uint now = _simulationSystem.frameIndex;
            NativeArray<Entity> households = default(NativeArray<Entity>);
            try
            {
                households = _unreachableHouseholds.ToEntityArray(Allocator.Temp);
                _unreachableSeen.Clear();
                int retired = 0;
                for (int i = 0; i < households.Length; i++)
                {
                    Entity household = households[i];
                    _unreachableSeen.Add(household);
                    if (IsSettling(household)) continue;
                    ulong householdId;
                    PropertyRentIdentity desiredIdentity;
                    bool bound = TryGetBoundHouseholdId(household, out householdId);
                    if (bound && IsHouseholdDesiredUnhoused(householdId))
                    {
                        _unreachableSince.Remove(household);
                        continue;
                    }
                    if (bound && TryGetDesiredPropertyIdentity(householdId, out desiredIdentity) &&
                        (TryGetDesiredProperty(householdId, out Entity desiredProperty) ||
                         _pending.ContainsKey(desiredIdentity)))
                    {
                        // The destination may still be unresolved or its native rent action may be
                        // pending. A positive host location always outranks the local homeless scan.
                        _unreachableSince.Remove(household);
                        continue;
                    }
                    uint since;
                    if (!_unreachableSince.TryGetValue(household, out since))
                    {
                        _unreachableSince[household] = now;
                        continue;
                    }
                    if (now - since < UnreachableGraceFrames) continue;
                    if (retired >= MaxUnreachableRetiredPerUpdate) continue;
                    if (!Retire(household)) continue;
                    _unreachableSince.Remove(household);
                    UnbindDepartingHousehold(household);
                    retired++;
                    _retiredHouseholds++;
                }
                PruneUnreachable();
            }
            finally
            {
                if (households.IsCreated) households.Dispose();
                _unreachableSeen.Clear();
            }
        }

        /// <summary>Forget households that found a home again, so their grace period restarts.</summary>
        private void PruneUnreachable()
        {
            if (_unreachableSince.Count == 0) return;
            _settlingScratch.Clear();
            foreach (KeyValuePair<Entity, uint> pair in _unreachableSince)
                if (!_unreachableSeen.Contains(pair.Key)) _settlingScratch.Add(pair.Key);
            for (int i = 0; i < _settlingScratch.Count; i++)
                _unreachableSince.Remove(_settlingScratch[i]);
            _settlingScratch.Clear();
        }

        private void ApplyBucket(int bucket)
        {
            List<Entity> entities = _cacheBuckets[bucket];
            HashSet<Entity> members = _cacheBucketMembers[bucket];
            // Rebuild membership while compacting: this drops entries whose cache is gone and any
            // duplicate left behind by a property that changed partition.
            members.Clear();
            int write = 0;
            for (int i = 0; i < entities.Count; i++)
            {
                Entity property = entities[i];
                CachedProperty cached;
                if (!_cache.TryGetValue(property, out cached)) continue;
                // A stale entry can remain in its old bucket list after a local partition move.
                // Do not delete the live cache the new bucket now owns.
                if (cached.Bucket != bucket) continue;
                if (!MatchesCachedProperty(property, cached))
                {
                    RemoveCachedProperty(property);
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
                entities[write++] = property;
                if (_budget.Exhausted) continue;
                ApplyOne(property);
            }
            if (write < entities.Count) entities.RemoveRange(write, entities.Count - write);
        }

        private void ApplyOne(Entity property)
        {
            // A property can be both freshly changed and in the partition this update walks.
            // Reconciling it twice would create the same household twice, because the move-in it
            // asked for the first time is still queued.
            if (!_appliedThisUpdate.Add(property)) return;
            CachedProperty cached;
            if (!_cache.TryGetValue(property, out cached)) return;
            if (!MatchesCachedProperty(property, cached))
            {
                RemoveCachedProperty(property);
                return;
            }
            bool applied = false;
            try
            {
                ApplyProperty(property, cached);
                applied = true;
            }
            catch (Exception ex)
            {
                // One malformed property must not take the whole reconcile down. Drop its cache so
                // the next page re-resolves it from scratch.
                RemoveCachedProperty(property);
                if (!_applyWarned)
                {
                    _applyWarned = true;
                    Mod.log.Warn("[MP] Occupancy: reconcile failed for one property; dropped it " +
                                 "until the next page (logged once): " + ex.Message);
                }
            }
            if (applied && cached.RemoveAfterApply)
            {
                if (HasResidentialRenter(property)) ScheduleReapply(property);
                else if (RemoveCachedProperty(property)) _pruned++;
            }
            _budget.Properties++;
            _appliedProperties++;
        }

        /// <summary>
        /// A cache entry stays valid as long as the same residential building still stands on the
        /// same spot. It deliberately does not require the prefab to be unchanged: a building that
        /// levels up keeps its entity and its position but swaps its prefab, and dropping the cache
        /// there would stop reconciling the house exactly when its two copies diverge.
        /// </summary>
        private bool MatchesCachedProperty(Entity property, CachedProperty cached)
        {
            if (!IsLiveProperty(property)) return false;
            if (!PositionMatchesAnchor(property, cached.Identity)) return false;
            cached.Prefab = EntityManager.GetComponentData<PrefabRef>(property).m_Prefab;
            return true;
        }

        private void ApplyProperty(Entity property, CachedProperty cached)
        {
            OccupancyHousehold[] wanted = cached.Households;
            bool localUnderConstruction = ApplyConstruction(property, cached.ConstructionSpeed);

            CollectLocalHouseholds(property);
            _claimedHouseholds.Clear();
            _wantedHouseholdIds.Clear();

            // Bind a freshly downloaded world's already-identical families by their semantic
            // fingerprint. After that one bootstrap, every reconcile is exclusively keyed by the
            // opaque host id; renter-buffer order never has identity meaning again.
            for (int i = 0; i < wanted.Length; i++)
            {
                OccupancyHousehold desired = wanted[i];
                if (desired.Departing)
                {
                    Entity leaving;
                    if (!TryResolveHousehold(desired.HouseholdId, out leaving))
                    {
                        leaving = FindBootstrapHousehold(desired);
                        if (leaving != Entity.Null)
                            BindHousehold(desired.HouseholdId, leaving);
                    }
                    // Do not claim it: the unmatched pass below invokes the native move-away
                    // lifecycle for this exact host identity.
                    continue;
                }
                if (!IsHouseholdDesiredHere(desired.HouseholdId, property)) continue;
                _wantedHouseholdIds.Add(desired.HouseholdId);

                Entity household;
                if (!TryResolveHousehold(desired.HouseholdId, out household))
                {
                    household = FindBootstrapHousehold(desired);
                    if (household != Entity.Null) BindHousehold(desired.HouseholdId, household);
                }
                if (household == Entity.Null) continue;
                _claimedHouseholds.Add(household);

                if (!IsHouseholdAtProperty(household, property)) continue;
                CancelUnauthorizedDeparture(household);
                ApplyHousehold(household, property, desired);
                NotePlacedHousehold(cached, desired, household);
            }

            bool settling = IsSettling(property);
            if (settling) ScheduleReapply(property);

            // Do not move a family into a building this peer is still putting up when the host's
            // is already finished: the two are describing different things, and the completion
            // just forced above lands on the next update anyway. Retirement and the numbers on
            // families already living here are unaffected.
            bool hostFinished = cached.ConstructionSpeed == 0;
            bool deferMoveIns = localUnderConstruction && hostFinished;
            if (deferMoveIns)
            {
                _deferredForConstruction++;
                ScheduleReapply(property);
            }

            // Remove every local identity that this absolute roster no longer places here. A
            // household desired at another resolved property is transferred through the normal
            // rent queue; an absent identity takes the ordinary move-away cleanup path.
            for (int i = 0; i < _localHouseholds.Count; i++)
            {
                Entity local = _localHouseholds[i];
                if (_claimedHouseholds.Contains(local)) continue;

                ulong localId;
                PropertyRentIdentity desiredIdentity, localIdentity;
                Entity destination;
                bool hasLocalId = TryGetBoundHouseholdId(local, out localId);
                if (hasLocalId &&
                    TryGetDesiredPropertyIdentity(localId, out desiredIdentity) &&
                    TryGetPropertyIdentity(property, out localIdentity))
                {
                    if (desiredIdentity.Equals(localIdentity))
                    {
                        // This property says "not here", but no received destination has superseded
                        // the last positive location. Preserve identity until a move page arrives or
                        // the host explicitly marks the household as departing.
                        ScheduleReapply(property);
                        continue;
                    }

                    if (!TryGetDesiredProperty(localId, out destination) ||
                        destination == property || !CanStageTransferTo(destination))
                    {
                        // Keep the native two-way source link intact until the destination exists
                        // locally. A page can arrive before its building resolves (or be the only
                        // surviving half of a move); breaking the link here would strand the
                        // household forever if that pending identity later expires.
                        ScheduleReapply(property);
                        if (destination != Entity.Null && destination != property)
                            MarkDirty(destination);
                        continue;
                    }

                    // Stage an outgoing transfer by freeing only the source buffer slot. Keep the
                    // PropertyRenter component until the native rent action changes it; this breaks
                    // full A<->B swaps without inventing a half-valid destination link.
                    if (!TrackStagedTransfer(localId, local, property, destination))
                    {
                        ScheduleReapply(property);
                        MarkDirty(destination);
                        continue;
                    }
                    RemoveRenterReference(property, local);
                    MarkDirty(destination);
                    ScheduleReapply(destination);
                    continue;
                }

                if (hasLocalId && IsHouseholdDesiredUnhoused(localId))
                {
                    ReleaseUnhousedHousehold(localId, local, property);
                    continue;
                }
                if (hasLocalId && HasActiveDesiredCitizenStillLinked(local))
                {
                    // A vanished household shell can be one side of a split. Its retained
                    // household tombstone does not prove that still-live members left the city;
                    // wait until their higher-revision destination rosters move them, or exact
                    // citizen tombstones make them inactive.
                    ScheduleReapply(property);
                    continue;
                }
                if (!hasLocalId && DeferUnboundRetirement(local, _unboundHouseholdSince))
                {
                    ScheduleReapply(property);
                    continue;
                }

                if (_budget.HouseholdsRetired >= MaxHouseholdsRetiredPerUpdate)
                {
                    ScheduleReapply(property);
                    break;
                }
                if (!Retire(local)) continue;
                _unboundHouseholdSince.Remove(local);
                RemoveRenterReference(property, local);
                UnbindDepartingHousehold(local);
                _budget.HouseholdsRetired++;
                _retiredHouseholds++;
            }

            if (settling || deferMoveIns) return;

            int free = FreeResidentialSlots(property);
            for (int i = 0; i < wanted.Length; i++)
            {
                OccupancyHousehold desired = wanted[i];
                if (desired.Departing) continue;
                if (!IsHouseholdDesiredHere(desired.HouseholdId, property)) continue;

                Entity existing;
                if (TryResolveHousehold(desired.HouseholdId, out existing))
                {
                    if (IsHouseholdAtProperty(existing, property))
                    {
                        CancelUnauthorizedDeparture(existing);
                        ApplyHousehold(existing, property, desired);
                        NotePlacedHousehold(cached, desired, existing);
                        continue;
                    }
                    if (free <= 0)
                    {
                        _refusedMoveIns++;
                        ScheduleReapply(property);
                        break;
                    }
                    if (!EnqueueRentAction(property, existing))
                    {
                        ScheduleReapply(property);
                        break;
                    }
                    CancelUnauthorizedDeparture(existing);
                    MarkSettling(existing);
                    MarkSettling(property);
                    TrackPendingMoveIn(desired, existing, property, cached.Revision, false);
                    ScheduleReapply(property);
                    free--;
                    continue;
                }

                int initialVehicleCount = desired.OwnedVehicles != null
                    ? desired.OwnedVehicles.Length : 0;
                if (_budget.HouseholdsCreated >= MaxHouseholdsCreatedPerUpdate ||
                    _budget.CitizensCreated + desired.Citizens.Length >
                    MaxCitizensCreatedPerUpdate ||
                    _budget.VehiclesCreated + initialVehicleCount >
                    MaxVehiclesCreatedPerUpdate) break;
                if (desired.Citizens.Length == 0) continue;
                if (free <= 0)
                {
                    // The local building has fewer homes than the host's - normally a level change
                    // that has not reached this peer yet. Retried on the next pass.
                    _refusedMoveIns++;
                    ScheduleReapply(property);
                    break;
                }
                Entity created = CreateHousehold(property, desired);
                if (created == Entity.Null) break;
                TrackPendingMoveIn(desired, created, property, cached.Revision, true);
                free--;
                _budget.HouseholdsCreated++;
                _createdHouseholds++;
                MarkSettling(property);
                ScheduleReapply(property);
            }
        }

        /// <summary>
        /// Keep this peer's building site in step with the host's, and report whether it is still
        /// one. The build rate is drawn independently on each machine — a house given 39 takes over
        /// twice as long as the same house given 88 — so without this the same building finishes
        /// minutes apart on the two cities. Adopting the host's rate makes them finish together;
        /// forcing completion when the host is already done closes the gap that is left.
        /// </summary>
        private bool ApplyConstruction(Entity property, byte hostSpeed)
        {
            if (!EntityManager.HasComponent<global::Game.Objects.UnderConstruction>(property))
                return false;
            global::Game.Objects.UnderConstruction site =
                EntityManager.GetComponentData<global::Game.Objects.UnderConstruction>(property);

            if (hostSpeed == 0)
            {
                // 100 is where the game stops building and swaps in the finished prefab, so any
                // value at or above it completes on the next construction update.
                if (site.m_Progress >= 100) return true;
                site.m_Progress = byte.MaxValue;
                EntityManager.SetComponentData(property, site);
                _forcedCompletions++;
                return true;
            }

            if (site.m_Speed != hostSpeed)
            {
                site.m_Speed = hostSpeed;
                EntityManager.SetComponentData(property, site);
                _alignedBuildRates++;
            }
            return true;
        }

        private void CollectLocalHouseholds(Entity property)
        {
            _localHouseholds.Clear();
            DynamicBuffer<Renter> renters = EntityManager.GetBuffer<Renter>(property);
            bool changed = false;
            for (int i = renters.Length - 1; i >= 0; i--)
            {
                Entity renter = renters[i].m_Renter;
                if (renter == Entity.Null || !EntityManager.Exists(renter)) continue;
                if (!EntityManager.HasComponent<Household>(renter)) continue;
                if (EntityManager.HasComponent<Deleted>(renter) ||
                    !EntityManager.HasComponent<PropertyRenter>(renter) ||
                    EntityManager.GetComponentData<PropertyRenter>(renter).m_Property != property)
                {
                    renters.RemoveAt(i);
                    changed = true;
                    continue;
                }
                if (EntityManager.HasComponent<TouristHousehold>(renter) ||
                    EntityManager.HasComponent<CommuterHousehold>(renter)) continue;
                if (_localHouseholds.Contains(renter))
                {
                    renters.RemoveAt(i);
                    changed = true;
                    continue;
                }
                _localHouseholds.Add(renter);
            }
            if (changed) MarkRentersUpdated(property);
        }

        private Entity FindBootstrapHousehold(OccupancyHousehold wanted)
        {
            EnsureBootstrapIdentityIndex();
            List<Entity> globalCandidates;
            if (!_bootstrapHouseholdIndex.TryGetValue(HouseholdBootstrapKey(wanted),
                out globalCandidates)) return Entity.Null;
            Entity match = Entity.Null;
            for (int i = 0; i < globalCandidates.Count; i++)
            {
                Entity candidate = globalCandidates[i];
                if (_claimedHouseholds.Contains(candidate) || candidate == Entity.Null ||
                    !EntityManager.Exists(candidate) || EntityManager.HasComponent<Deleted>(candidate))
                    continue;
                ulong alreadyBound;
                if (TryGetBoundHouseholdId(candidate, out alreadyBound)) continue;
                if (!HouseholdBootstrapMatches(candidate, wanted)) continue;
                if (match != Entity.Null && match != candidate) return Entity.Null;
                match = candidate;
            }
            return match;
        }

        private void EnsureBootstrapIdentityIndex()
        {
            if (_bootstrapIdentityIndexBuilt) return;
            _bootstrapIdentityIndexBuilt = true;
            NativeArray<Entity> households = default(NativeArray<Entity>);
            NativeArray<Entity> citizens = default(NativeArray<Entity>);
            try
            {
                households = _bootstrapHouseholds.ToEntityArray(Allocator.Temp);
                for (int i = 0; i < households.Length; i++)
                    AddBootstrapCandidate(_bootstrapHouseholdIndex,
                        HouseholdBootstrapKey(households[i]), households[i]);
                citizens = _bootstrapCitizens.ToEntityArray(Allocator.Temp);
                for (int i = 0; i < citizens.Length; i++)
                    AddBootstrapCandidate(_bootstrapCitizenIndex,
                        CitizenBootstrapKey(citizens[i]), citizens[i]);
            }
            finally
            {
                if (households.IsCreated) households.Dispose();
                if (citizens.IsCreated) citizens.Dispose();
            }
        }

        private static void AddBootstrapCandidate(Dictionary<int, List<Entity>> index, int key,
            Entity entity)
        {
            List<Entity> candidates;
            if (!index.TryGetValue(key, out candidates))
            {
                candidates = new List<Entity>();
                index[key] = candidates;
            }
            candidates.Add(entity);
        }

        private int HouseholdBootstrapKey(Entity household)
        {
            if (household == Entity.Null || !EntityManager.Exists(household) ||
                !EntityManager.HasComponent<PrefabRef>(household) ||
                !EntityManager.HasBuffer<HouseholdCitizen>(household)) return 0;
            string prefabName = PrefabIndex.SafeName(_prefabSystem,
                EntityManager.GetComponentData<PrefabRef>(household).m_Prefab);
            DynamicBuffer<HouseholdCitizen> members =
                EntityManager.GetBuffer<HouseholdCitizen>(household, true);
            var citizenKeys = new int[members.Length];
            for (int i = 0; i < members.Length; i++)
                citizenKeys[i] = CitizenBootstrapKey(members[i].m_Citizen);
            Array.Sort(citizenKeys);
            return CombineBootstrapKey(prefabName, citizenKeys);
        }

        private static int HouseholdBootstrapKey(OccupancyHousehold household)
        {
            var citizenKeys = new int[household.Citizens.Length];
            for (int i = 0; i < household.Citizens.Length; i++)
                citizenKeys[i] = CitizenBootstrapKey(household.Citizens[i]);
            Array.Sort(citizenKeys);
            return CombineBootstrapKey(household.PrefabName, citizenKeys);
        }

        private int CitizenBootstrapKey(Entity citizen)
        {
            if (citizen == Entity.Null || !EntityManager.Exists(citizen) ||
                !EntityManager.HasComponent<Citizen>(citizen) ||
                !EntityManager.HasComponent<PrefabRef>(citizen)) return 0;
            Citizen data = EntityManager.GetComponentData<Citizen>(citizen);
            string prefabName = PrefabIndex.SafeName(_prefabSystem,
                EntityManager.GetComponentData<PrefabRef>(citizen).m_Prefab);
            return CombineCitizenBootstrapKey(prefabName, data.m_PseudoRandom, data.m_BirthDay,
                (short)data.m_State & HostOwnedCitizenFlags);
        }

        private static int CitizenBootstrapKey(OccupancyCitizen citizen) =>
            CombineCitizenBootstrapKey(citizen.PrefabName, citizen.PseudoRandom, citizen.BirthDay,
                citizen.State & HostOwnedCitizenFlags);

        private static int CombineCitizenBootstrapKey(string prefabName, ushort pseudoRandom,
            short birthDay, int state)
        {
            unchecked
            {
                int hash = prefabName != null ? prefabName.GetHashCode() : 0;
                hash = hash * 397 ^ pseudoRandom;
                hash = hash * 397 ^ birthDay;
                return hash * 397 ^ state;
            }
        }

        private static int CombineBootstrapKey(string prefabName, int[] citizenKeys)
        {
            unchecked
            {
                int hash = prefabName != null ? prefabName.GetHashCode() : 0;
                hash = hash * 397 ^ citizenKeys.Length;
                for (int i = 0; i < citizenKeys.Length; i++)
                    hash = hash * 397 ^ citizenKeys[i];
                return hash;
            }
        }

        private bool HouseholdBootstrapMatches(Entity household, OccupancyHousehold wanted)
        {
            if (!EntityManager.HasComponent<PrefabRef>(household) ||
                !EntityManager.HasBuffer<HouseholdCitizen>(household)) return false;
            string prefabName = PrefabIndex.SafeName(_prefabSystem,
                EntityManager.GetComponentData<PrefabRef>(household).m_Prefab);
            if (!string.Equals(prefabName, wanted.PrefabName, StringComparison.Ordinal)) return false;
            if (!BootstrapNameIndicesMatch(household, wanted.NameIndices)) return false;

            DynamicBuffer<HouseholdCitizen> members =
                EntityManager.GetBuffer<HouseholdCitizen>(household, true);
            if (members.Length != wanted.Citizens.Length) return false;
            _claimedCitizens.Clear();
            for (int i = 0; i < wanted.Citizens.Length; i++)
            {
                Entity match = Entity.Null;
                for (int j = 0; j < members.Length; j++)
                {
                    Entity candidate = members[j].m_Citizen;
                    if (_claimedCitizens.Contains(candidate) ||
                        !CitizenBootstrapMatches(candidate, wanted.Citizens[i])) continue;
                    match = candidate;
                    break;
                }
                if (match == Entity.Null)
                {
                    _claimedCitizens.Clear();
                    return false;
                }
                _claimedCitizens.Add(match);
            }
            _claimedCitizens.Clear();
            return true;
        }

        private bool CitizenBootstrapMatches(Entity citizen, OccupancyCitizen wanted)
        {
            if (citizen == Entity.Null || !EntityManager.Exists(citizen) ||
                EntityManager.HasComponent<Deleted>(citizen) ||
                !EntityManager.HasComponent<Citizen>(citizen) ||
                !EntityManager.HasComponent<PrefabRef>(citizen)) return false;
            Citizen data = EntityManager.GetComponentData<Citizen>(citizen);
            if (data.m_PseudoRandom != wanted.PseudoRandom || data.m_BirthDay != wanted.BirthDay ||
                ((short)data.m_State & HostOwnedCitizenFlags) !=
                (wanted.State & HostOwnedCitizenFlags)) return false;
            string prefabName = PrefabIndex.SafeName(_prefabSystem,
                EntityManager.GetComponentData<PrefabRef>(citizen).m_Prefab);
            return string.Equals(prefabName, wanted.PrefabName, StringComparison.Ordinal) &&
                   BootstrapNameIndicesMatch(citizen, wanted.NameIndices);
        }

        private bool BootstrapNameIndicesMatch(Entity entity, int[] wanted)
        {
            if (wanted == null || wanted.Length == 0)
                return !EntityManager.HasBuffer<RandomLocalizationIndex>(entity) ||
                       EntityManager.GetBuffer<RandomLocalizationIndex>(entity, true).Length == 0;
            if (!EntityManager.HasBuffer<RandomLocalizationIndex>(entity)) return false;
            DynamicBuffer<RandomLocalizationIndex> indices =
                EntityManager.GetBuffer<RandomLocalizationIndex>(entity, true);
            if (indices.Length < wanted.Length) return false;
            for (int i = 0; i < wanted.Length; i++)
                if (indices[i].m_Index != wanted[i]) return false;
            return true;
        }

        private bool IsHouseholdAtProperty(Entity household, Entity property)
        {
            if (!EntityManager.HasComponent<PropertyRenter>(household) ||
                EntityManager.GetComponentData<PropertyRenter>(household).m_Property != property)
                return false;
            DynamicBuffer<Renter> renters = EntityManager.GetBuffer<Renter>(property);
            bool found = false;
            for (int i = renters.Length - 1; i >= 0; i--)
            {
                if (renters[i].m_Renter != household) continue;
                if (!found) found = true;
                else renters.RemoveAt(i);
            }
            if (!found)
            {
                renters.Add(new Renter { m_Renter = household });
                MarkRentersUpdated(property);
            }
            return true;
        }

        private void CancelUnauthorizedDeparture(Entity household)
        {
            _authorizedMoveAways.Remove(household);
            if (EntityManager.HasComponent<MovingAway>(household))
                EntityManager.RemoveComponent<MovingAway>(household);
            if (EntityManager.HasComponent<PropertySeeker>(household))
                EntityManager.SetComponentEnabled<PropertySeeker>(household, false);
        }

        private void RemoveRenterReference(Entity property, Entity household)
        {
            if (!EntityManager.HasBuffer<Renter>(property)) return;
            DynamicBuffer<Renter> renters = EntityManager.GetBuffer<Renter>(property);
            bool changed = false;
            for (int i = renters.Length - 1; i >= 0; i--)
            {
                if (renters[i].m_Renter != household) continue;
                renters.RemoveAt(i);
                changed = true;
            }
            if (changed) MarkRentersUpdated(property);
        }

        private void ReleaseUnhousedHousehold(ulong householdId, Entity household, Entity property)
        {
            _authorizedMoveAways.Remove(household);
            RemoveRenterReference(property, household);
            if (EntityManager.HasComponent<PropertyRenter>(household))
                EntityManager.RemoveComponent<PropertyRenter>(household);
            if (EntityManager.HasComponent<PropertySeeker>(household))
                EntityManager.SetComponentEnabled<PropertySeeker>(household, false);
            _pendingMoveIns.Remove(householdId);
            _stagedTransfers.Remove(householdId);
            _stagedTransferCooldownUntil.Remove(householdId);
            _settling.Remove(household);
            _unreachableSince.Remove(household);
        }

        private void MarkRentersUpdated(Entity property)
        {
            // This notification is an event entity, not a tag on the building. Clean up the old
            // malformed marker as well so upgraded sessions can emit a valid notification.
            if (EntityManager.HasComponent<RentersUpdated>(property) &&
                !EntityManager.HasComponent<global::Game.Common.Event>(property))
                EntityManager.RemoveComponent<RentersUpdated>(property);

            Entity update = EntityManager.CreateEntity();
            EntityManager.AddComponent<global::Game.Common.Event>(update);
            EntityManager.AddComponentData(update, new RentersUpdated(property));
        }

        private int FreeResidentialSlots(Entity property)
        {
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(property).m_Prefab;
            if (!EntityManager.HasComponent<BuildingPropertyData>(prefab)) return 0;
            int capacity = EntityManager.GetComponentData<BuildingPropertyData>(prefab)
                .CountProperties(global::Game.Zones.AreaType.Residential);
            DynamicBuffer<Renter> renters = EntityManager.GetBuffer<Renter>(property, true);
            for (int i = 0; i < renters.Length; i++)
            {
                Entity renter = renters[i].m_Renter;
                if (renter == Entity.Null || !EntityManager.Exists(renter) ||
                    !EntityManager.HasComponent<Household>(renter) ||
                    EntityManager.HasComponent<Deleted>(renter) ||
                    !EntityManager.HasComponent<PropertyRenter>(renter) ||
                    EntityManager.GetComponentData<PropertyRenter>(renter).m_Property != property)
                    continue;
                capacity--;
            }
            return capacity;
        }

        private bool CanStageTransferTo(Entity destination)
        {
            if (!CanEnqueueRentAction() || !IsLiveProperty(destination)) return false;
            CachedProperty cached;
            if (!_cache.TryGetValue(destination, out cached) || cached.RemoveAfterApply ||
                cached.Households == null) return false;
            if (EntityManager.HasComponent<global::Game.Objects.UnderConstruction>(destination) &&
                cached.ConstructionSpeed == 0) return false;

            Entity prefab = EntityManager.GetComponentData<PrefabRef>(destination).m_Prefab;
            if (!EntityManager.HasComponent<BuildingPropertyData>(prefab)) return false;
            int capacity = EntityManager.GetComponentData<BuildingPropertyData>(prefab)
                .CountProperties(global::Game.Zones.AreaType.Residential);

            int desiredCount = 0;
            for (int i = 0; i < cached.Households.Length; i++)
            {
                OccupancyHousehold wanted = cached.Households[i];
                if (!wanted.Departing && IsHouseholdDesiredHere(wanted.HouseholdId, destination))
                    desiredCount++;
            }

            int fixedOccupants = 0;
            DynamicBuffer<Renter> renters = EntityManager.GetBuffer<Renter>(destination, true);
            for (int i = 0; i < renters.Length; i++)
            {
                Entity renter = renters[i].m_Renter;
                if (renter == Entity.Null || !EntityManager.Exists(renter) ||
                    !EntityManager.HasComponent<Household>(renter) ||
                    EntityManager.HasComponent<Deleted>(renter) ||
                    !EntityManager.HasComponent<PropertyRenter>(renter) ||
                    EntityManager.GetComponentData<PropertyRenter>(renter).m_Property != destination)
                    continue;
                if (EntityManager.HasComponent<TouristHousehold>(renter) ||
                    EntityManager.HasComponent<CommuterHousehold>(renter))
                {
                    fixedOccupants++;
                    continue;
                }

                ulong renterId;
                PropertyRentIdentity desiredIdentity, destinationIdentity;
                if (!TryGetBoundHouseholdId(renter, out renterId))
                    continue; // an unbound bootstrap extra is retireable
                if (!TryGetDesiredPropertyIdentity(renterId, out desiredIdentity))
                {
                    if (HasActiveDesiredCitizenStillLinked(renter)) fixedOccupants++;
                    continue;
                }
                if (!TryGetPropertyIdentity(destination, out destinationIdentity))
                {
                    fixedOccupants++;
                    continue;
                }

                if (desiredIdentity.Equals(destinationIdentity))
                {
                    bool inRoster = false;
                    for (int h = 0; h < cached.Households.Length; h++)
                    {
                        if (cached.Households[h].Departing ||
                            cached.Households[h].HouseholdId != renterId) continue;
                        inRoster = true;
                        break;
                    }
                    if (!inRoster) fixedOccupants++;
                    continue;
                }

                Entity outgoingDestination;
                if (!TryGetDesiredProperty(renterId, out outgoingDestination)) fixedOccupants++;
            }
            return desiredCount + fixedOccupants <= capacity;
        }

        private bool HasResidentialRenter(Entity property)
        {
            if (!IsLiveProperty(property)) return false;
            DynamicBuffer<Renter> renters = EntityManager.GetBuffer<Renter>(property, true);
            for (int i = 0; i < renters.Length; i++)
            {
                Entity renter = renters[i].m_Renter;
                if (renter != Entity.Null && EntityManager.Exists(renter) &&
                    EntityManager.HasComponent<Household>(renter) &&
                    !EntityManager.HasComponent<TouristHousehold>(renter) &&
                    !EntityManager.HasComponent<CommuterHousehold>(renter) &&
                    !EntityManager.HasComponent<Deleted>(renter)) return true;
            }
            return false;
        }

        private void ApplyHousehold(Entity household, Entity property, OccupancyHousehold wanted)
        {
            Entity prefab;
            if (ResolvePrefab<HouseholdData>(wanted.PrefabName, out prefab) &&
                EntityManager.HasComponent<PrefabRef>(household) &&
                EntityManager.GetComponentData<PrefabRef>(household).m_Prefab != prefab)
                EntityManager.SetComponentData(household, new PrefabRef(prefab));

            Household data = EntityManager.GetComponentData<Household>(household);
            var flags = (HouseholdFlags)(wanted.Flags & HouseholdFlagMask);
            // Arrival owns this bit on every peer. Preserve a completed local move-in, but never
            // import it early from a host page because that would suppress the population event.
            if ((data.m_Flags & HouseholdFlags.MovedIn) != 0)
                flags |= HouseholdFlags.MovedIn;
            if (data.m_Flags != flags)
            {
                data.m_Flags = flags;
                EntityManager.SetComponentData(household, data);
            }
            ApplyHouseholdEconomy(household, property,
                DesiredHouseholdEconomy.From(wanted, default(PropertyRentIdentity), 0));

            ApplyNameIndices(household, wanted.NameIndices);
            ApplyCitizens(household, property, wanted);
            ApplyPets(household, property, wanted);
            ApplyOwnedVehicles(household, property, wanted);
        }

        private void ApplyOwnedVehicles(Entity household, Entity property,
            OccupancyHousehold wanted)
        {
            string[] desired = wanted.OwnedVehicles;
            if (desired == null || desired.Length == 0) return;
            if (IsSettling(household))
            {
                ScheduleReapply(property);
                return;
            }

            _localVehiclePrefabCounts.Clear();
            if (EntityManager.HasBuffer<OwnedVehicle>(household))
            {
                DynamicBuffer<OwnedVehicle> owned =
                    EntityManager.GetBuffer<OwnedVehicle>(household, true);
                for (int i = 0; i < owned.Length; i++)
                {
                    Entity vehicle = owned[i].m_Vehicle;
                    if (vehicle == Entity.Null || !EntityManager.Exists(vehicle) ||
                        EntityManager.HasComponent<Deleted>(vehicle) ||
                        !EntityManager.HasComponent<global::Game.Vehicles.PersonalCar>(vehicle) ||
                        !EntityManager.HasComponent<PrefabRef>(vehicle) ||
                        !EntityManager.HasComponent<Owner>(vehicle) ||
                        EntityManager.GetComponentData<Owner>(vehicle).m_Owner != household)
                        continue;
                    string name = PrefabIndex.SafeName(_prefabSystem,
                        EntityManager.GetComponentData<PrefabRef>(vehicle).m_Prefab);
                    if (string.IsNullOrEmpty(name)) continue;
                    int count;
                    _localVehiclePrefabCounts.TryGetValue(name, out count);
                    _localVehiclePrefabCounts[name] = count + 1;
                }
            }

            _matchedVehiclePrefabCounts.Clear();
            bool createdAny = false;
            Entity source = GetVehicleCreationSource(household, property);
            for (int i = 0; i < desired.Length; i++)
            {
                string prefabName = desired[i];
                int matched;
                _matchedVehiclePrefabCounts.TryGetValue(prefabName, out matched);
                matched++;
                _matchedVehiclePrefabCounts[prefabName] = matched;
                int local;
                _localVehiclePrefabCounts.TryGetValue(prefabName, out local);
                if (local >= matched) continue;

                if (_budget.VehiclesCreated >= MaxVehiclesCreatedPerUpdate)
                {
                    ScheduleReapply(property);
                    break;
                }
                if (!EntityManager.HasBuffer<OwnedVehicle>(household))
                    EntityManager.AddBuffer<OwnedVehicle>(household);

                Entity vehicle = CreateOwnedVehicle(household, source, wanted.HouseholdId,
                    prefabName, i);
                if (vehicle == Entity.Null)
                {
                    TraceVehicleSpawnFailure(wanted.HouseholdId, prefabName, property, source);
                    ScheduleReapply(property);
                    continue;
                }

                LinkOwnedVehicle(household, vehicle);
                _localVehiclePrefabCounts[prefabName] = local + 1;
                _budget.VehiclesCreated++;
                _createdVehicles++;
                createdAny = true;
                TraceVehicleSpawn(wanted.HouseholdId, prefabName, vehicle, property, source,
                    false);
            }
            if (!createdAny) return;
            MarkSettling(household);
            ScheduleReapply(property);
        }

        /// <summary>
        /// Cars that already belong to a newly arriving family must exist before its citizens run
        /// their first behaviour pass. That gives the native trip planner an owned car to reserve
        /// for the journey from the outside connection to the new home.
        /// </summary>
        private void CreateInitialOwnedVehicles(Entity household, Entity property, Entity source,
            OccupancyHousehold wanted)
        {
            string[] desired = wanted.OwnedVehicles;
            if (desired == null || desired.Length == 0) return;
            if (!EntityManager.HasBuffer<OwnedVehicle>(household))
                EntityManager.AddBuffer<OwnedVehicle>(household);

            for (int i = 0; i < desired.Length; i++)
            {
                string prefabName = desired[i];
                Entity vehicle = CreateOwnedVehicle(household, source, wanted.HouseholdId,
                    prefabName, i);
                if (vehicle == Entity.Null)
                {
                    TraceVehicleSpawnFailure(wanted.HouseholdId, prefabName, property, source);
                    continue;
                }

                LinkOwnedVehicle(household, vehicle);
                _budget.VehiclesCreated++;
                _createdVehicles++;
                TraceVehicleSpawn(wanted.HouseholdId, prefabName, vehicle, property, source, true);
            }
        }

        private void LinkOwnedVehicle(Entity household, Entity vehicle)
        {
            if (!EntityManager.HasBuffer<OwnedVehicle>(household))
                EntityManager.AddBuffer<OwnedVehicle>(household);
            DynamicBuffer<OwnedVehicle> owned = EntityManager.GetBuffer<OwnedVehicle>(household);
            for (int i = 0; i < owned.Length; i++)
                if (owned[i].m_Vehicle == vehicle) return;
            owned.Add(new OwnedVehicle(vehicle));
        }

        [Conditional(DevTrace.Symbol)]
        private void TraceVehicleSpawn(ulong householdId, string prefabName, Entity vehicle,
            Entity property, Entity source, bool initial)
        {
            Mod.log.Info("[MP][OCC-DEV] CAR-SPAWN family=0x" +
                         householdId.ToString("X16") + " vehicle='" + prefabName +
                         "' local=" + vehicle + " house='" + SafePrefabName(property) +
                         "' origin='" + SafePrefabName(source) + "' initial=" + initial + ".");
        }

        private void TraceVehicleSpawnFailure(ulong householdId, string prefabName,
            Entity property, Entity source)
        {
            string warningKey = householdId.ToString("X16") + "|" + prefabName;
            if (!_vehicleSpawnWarnings.Add(warningKey)) return;
            Mod.Verbose("[MP] Occupancy: could not spawn owned vehicle '" + prefabName +
                        "' for family 0x" + householdId.ToString("X16") + " at '" +
                        SafePrefabName(property) + "' (from '" + SafePrefabName(source) + "').");
        }

        /// <summary>
        /// A family's surname and a person's first name are stored as indices into localized name
        /// lists, drawn on each machine from its own clock. Nothing else about the household says
        /// what it is called, so these have to be copied for two players to be talking about the
        /// same family.
        /// </summary>
        private void ApplyNameIndices(Entity entity, int[] wanted)
        {
            if (wanted.Length == 0 ||
                !EntityManager.HasBuffer<RandomLocalizationIndex>(entity)) return;
            DynamicBuffer<RandomLocalizationIndex> indices =
                EntityManager.GetBuffer<RandomLocalizationIndex>(entity);
            // The local buffer is sized from this peer's own prefab, and the host's name lists are
            // the same content. Write the slots both sides have and leave any extra alone.
            int count = math.min(indices.Length, wanted.Length);
            bool changed = false;
            for (int i = 0; i < count; i++)
            {
                if (indices[i].m_Index == wanted[i]) continue;
                indices[i] = new RandomLocalizationIndex(wanted[i]);
                changed = true;
            }
            if (changed) _renamedEntities++;
        }

        private void ApplyCitizens(Entity household, Entity property, OccupancyHousehold wanted)
        {
            if (!EntityManager.HasBuffer<HouseholdCitizen>(household)) return;
            DedupeCitizens(household);
            // Snapshot before touching anything: creating or deleting an entity is a structural
            // change, and every dynamic buffer handle taken before it becomes invalid.
            _memberScratch.Clear();
            DynamicBuffer<HouseholdCitizen> members =
                EntityManager.GetBuffer<HouseholdCitizen>(household, true);
            for (int i = 0; i < members.Length; i++) _memberScratch.Add(members[i].m_Citizen);

            _claimedCitizens.Clear();
            _wantedCitizenIds.Clear();
            bool missingWanted = false;
            bool settling = IsSettling(household);
            if (settling) ScheduleReapply(property);

            for (int i = 0; i < wanted.Citizens.Length; i++)
            {
                OccupancyCitizen desired = wanted.Citizens[i];
                if (!IsCitizenDesiredHere(desired.CitizenId, wanted.HouseholdId)) continue;
                _wantedCitizenIds.Add(desired.CitizenId);

                Entity citizen;
                bool createdNow = false;
                if (!TryResolveCitizen(desired.CitizenId, out citizen))
                {
                    citizen = FindBootstrapCitizen(desired);
                    if (citizen != Entity.Null) BindCitizen(desired.CitizenId, citizen);
                }
                if (citizen == Entity.Null)
                {
                    if (settling || _budget.CitizensCreated >= MaxCitizensCreatedPerUpdate)
                    {
                        missingWanted = true;
                        continue;
                    }
                    citizen = CreateCitizen(household, property, desired);
                    if (citizen == Entity.Null)
                    {
                        missingWanted = true;
                        continue;
                    }
                    _budget.CitizensCreated++;
                    _createdCitizens++;
                    createdNow = true;
                    MarkSettling(household);
                    ScheduleReapply(property);
                }
                else if (!CitizenBelongsToHousehold(citizen, household))
                {
                    if (settling)
                    {
                        missingWanted = true;
                        continue;
                    }
                    MoveCitizenToHousehold(citizen, household);
                    MarkSettling(household);
                    ScheduleReapply(property);
                }

                _claimedCitizens.Add(citizen);
                // CitizenInitializeSystem consumes the small age-class marker on a Created
                // citizen. Replacing it with the host's calendar birthday in this same frame
                // would skip native initialization and leave the person outside population.
                if (!createdNow) ApplyCitizen(citizen, desired);
            }

            // Do not remove unmatched residents until every desired identity is present. This
            // keeps a creation budget boundary from momentarily emptying and retiring the family.
            if (settling || missingWanted) return;
            for (int i = _memberScratch.Count - 1; i >= 0; i--)
            {
                Entity citizen = _memberScratch[i];
                if (_claimedCitizens.Contains(citizen) || citizen == Entity.Null ||
                    !EntityManager.Exists(citizen) || EntityManager.HasComponent<Deleted>(citizen))
                    continue;
                ulong localId, desiredHouseholdId;
                bool bound = TryGetBoundCitizenId(citizen, out localId);
                if (bound && TryGetDesiredHouseholdId(localId, out desiredHouseholdId)) continue;
                if (!bound && DeferUnboundRetirement(citizen, _unboundCitizenSince))
                {
                    ScheduleReapply(property);
                    continue;
                }
                UnbindCitizen(citizen);
                _unboundCitizenSince.Remove(citizen);
                EntityManager.AddComponent<Deleted>(citizen);
                _removedCitizens++;
            }
        }

        private Entity FindBootstrapCitizen(OccupancyCitizen wanted)
        {
            EnsureBootstrapIdentityIndex();
            List<Entity> globalCandidates;
            if (!_bootstrapCitizenIndex.TryGetValue(CitizenBootstrapKey(wanted),
                out globalCandidates)) return Entity.Null;
            Entity match = Entity.Null;
            for (int i = 0; i < globalCandidates.Count; i++)
            {
                Entity candidate = globalCandidates[i];
                if (_claimedCitizens.Contains(candidate) || candidate == Entity.Null ||
                    !EntityManager.Exists(candidate) || EntityManager.HasComponent<Deleted>(candidate))
                    continue;
                ulong alreadyBound;
                if (TryGetBoundCitizenId(candidate, out alreadyBound)) continue;
                if (!CitizenBootstrapMatches(candidate, wanted)) continue;
                if (match != Entity.Null && match != candidate) return Entity.Null;
                match = candidate;
            }
            return match;
        }

        private bool CitizenBelongsToHousehold(Entity citizen, Entity household)
        {
            if (!EntityManager.HasComponent<HouseholdMember>(citizen) ||
                EntityManager.GetComponentData<HouseholdMember>(citizen).m_Household != household ||
                !EntityManager.HasBuffer<HouseholdCitizen>(household)) return false;
            DynamicBuffer<HouseholdCitizen> members =
                EntityManager.GetBuffer<HouseholdCitizen>(household, true);
            for (int i = 0; i < members.Length; i++)
                if (members[i].m_Citizen == citizen) return true;
            return false;
        }

        private bool HasActiveDesiredCitizenStillLinked(Entity household)
        {
            if (household == Entity.Null || !EntityManager.Exists(household) ||
                !EntityManager.HasBuffer<HouseholdCitizen>(household)) return false;
            DynamicBuffer<HouseholdCitizen> members =
                EntityManager.GetBuffer<HouseholdCitizen>(household, true);
            for (int i = 0; i < members.Length; i++)
            {
                ulong citizenId, desiredHouseholdId;
                if (TryGetBoundCitizenId(members[i].m_Citizen, out citizenId) &&
                    TryGetDesiredHouseholdId(citizenId, out desiredHouseholdId)) return true;
            }
            return false;
        }

        private bool DeferUnboundRetirement(Entity entity, Dictionary<Entity, uint> observed)
        {
            uint now = _simulationSystem.frameIndex;
            uint since;
            if (!observed.TryGetValue(entity, out since))
            {
                observed[entity] = now;
                return true;
            }
            if (now - since < BootstrapRetirementGraceFrames) return true;
            return false;
        }

        private void MoveCitizenToHousehold(Entity citizen, Entity household)
        {
            if (EntityManager.HasComponent<HouseholdMember>(citizen))
            {
                Entity previous = EntityManager.GetComponentData<HouseholdMember>(citizen).m_Household;
                if (previous != Entity.Null && previous != household && EntityManager.Exists(previous) &&
                    EntityManager.HasBuffer<HouseholdCitizen>(previous))
                {
                    DynamicBuffer<HouseholdCitizen> oldMembers =
                        EntityManager.GetBuffer<HouseholdCitizen>(previous);
                    for (int i = oldMembers.Length - 1; i >= 0; i--)
                        if (oldMembers[i].m_Citizen == citizen) oldMembers.RemoveAt(i);
                }
            }
            SetOrAdd(citizen, new HouseholdMember { m_Household = household });
            // Household membership and physical location are separate native graphs. An existing
            // citizen may be at work, school, or in transit, so changing CurrentBuilding here would
            // leave its Occupant/path state pointing at a different place. Newly created citizens
            // receive their initial home location in CreateCitizen instead.
            LinkCitizen(household, citizen);
            DedupeCitizens(household);
        }

        /// <summary>
        /// The game's citizen initialization appends every newly created citizen to its household,
        /// including the ones this system already linked. Collapsing repeats here is cheaper and
        /// safer than trying to predict that append, and it also clears out members whose entity
        /// is gone.
        /// </summary>
        private void DedupeCitizens(Entity household)
        {
            DynamicBuffer<HouseholdCitizen> members =
                EntityManager.GetBuffer<HouseholdCitizen>(household);
            for (int i = members.Length - 1; i >= 0; i--)
            {
                Entity citizen = members[i].m_Citizen;
                bool duplicate = false;
                for (int j = 0; j < i; j++)
                {
                    if (members[j].m_Citizen != citizen) continue;
                    duplicate = true;
                    break;
                }
                bool wrongHousehold = citizen != Entity.Null && EntityManager.Exists(citizen) &&
                    EntityManager.HasComponent<HouseholdMember>(citizen) &&
                    EntityManager.GetComponentData<HouseholdMember>(citizen).m_Household != household;
                if (duplicate || citizen == Entity.Null || !EntityManager.Exists(citizen) ||
                    EntityManager.HasComponent<Deleted>(citizen) || wrongHousehold)
                    members.RemoveAt(i);
            }
        }

        private void DedupePets(Entity household)
        {
            if (!EntityManager.HasBuffer<HouseholdAnimal>(household)) return;
            DynamicBuffer<HouseholdAnimal> animals =
                EntityManager.GetBuffer<HouseholdAnimal>(household);
            for (int i = animals.Length - 1; i >= 0; i--)
            {
                Entity pet = animals[i].m_HouseholdPet;
                bool duplicate = false;
                for (int j = 0; j < i; j++)
                {
                    if (animals[j].m_HouseholdPet != pet) continue;
                    duplicate = true;
                    break;
                }
                if (duplicate || pet == Entity.Null || !EntityManager.Exists(pet))
                    animals.RemoveAt(i);
            }
        }

        /// <summary>
        /// Link an existing citizen moved between households immediately. Fresh citizens are left
        /// for the initialization pass to append exactly once; a duplicate here would inflate the
        /// household size used by the first-arrival population event.
        /// </summary>
        private void LinkCitizen(Entity household, Entity citizen)
        {
            if (!EntityManager.HasBuffer<HouseholdCitizen>(household)) return;
            EntityManager.GetBuffer<HouseholdCitizen>(household)
                .Add(new HouseholdCitizen { m_Citizen = citizen });
        }

        private void LinkPet(Entity household, Entity pet)
        {
            if (!EntityManager.HasBuffer<HouseholdAnimal>(household))
                EntityManager.AddBuffer<HouseholdAnimal>(household);
            EntityManager.GetBuffer<HouseholdAnimal>(household)
                .Add(new HouseholdAnimal { m_HouseholdPet = pet });
        }

        private void ApplyCitizen(Entity citizen, OccupancyCitizen wanted)
        {
            if (citizen == Entity.Null || !EntityManager.Exists(citizen) ||
                !EntityManager.HasComponent<Citizen>(citizen)) return;

            Citizen data = EntityManager.GetComponentData<Citizen>(citizen);
            var state = (CitizenFlags)(((short)data.m_State & ~HostOwnedCitizenFlags) |
                                       (wanted.State & HostOwnedCitizenFlags));
            if (data.m_State != state || data.m_PseudoRandom != wanted.PseudoRandom ||
                data.m_BirthDay != wanted.BirthDay || data.m_Health != wanted.Health ||
                data.m_WellBeing != wanted.WellBeing ||
                data.m_UnemploymentCounter != wanted.UnemploymentCounter)
            {
                data.m_State = state;
                data.m_PseudoRandom = wanted.PseudoRandom;
                data.m_BirthDay = wanted.BirthDay;
                data.m_Health = wanted.Health;
                data.m_WellBeing = wanted.WellBeing;
                data.m_UnemploymentCounter = wanted.UnemploymentCounter;
                EntityManager.SetComponentData(citizen, data);
                _rewrittenCitizens++;
            }

            Entity prefab;
            if (ResolveCitizenPrefab(wanted.PrefabName, out prefab) &&
                EntityManager.HasComponent<PrefabRef>(citizen) &&
                EntityManager.GetComponentData<PrefabRef>(citizen).m_Prefab != prefab)
                EntityManager.SetComponentData(citizen, new PrefabRef(prefab));

            ApplyNameIndices(citizen, wanted.NameIndices);
            ApplyWageLevel(citizen, wanted);
        }

        /// <summary>
        /// Keep the wage level coherent when both peers already have this citizen employed. The
        /// employment graph remains local because a valid Worker also requires a matching workplace
        /// Employee entry. Displayed household income is authoritative through SalaryLastDay on the
        /// household snapshot, so no invalid placeholder job is manufactured here.
        /// </summary>
        private void ApplyWageLevel(Entity citizen, OccupancyCitizen wanted)
        {
            if (!wanted.Employed || !EntityManager.HasComponent<Worker>(citizen)) return;
            Worker worker = EntityManager.GetComponentData<Worker>(citizen);
            Entity workplace = worker.m_Workplace;
            if (workplace == Entity.Null || !EntityManager.Exists(workplace) ||
                !EntityManager.HasBuffer<Employee>(workplace)) return;
            DynamicBuffer<Employee> employees = EntityManager.GetBuffer<Employee>(workplace);
            int employeeIndex = -1;
            for (int i = 0; i < employees.Length; i++)
            {
                if (employees[i].m_Worker != citizen) continue;
                employeeIndex = i;
                break;
            }
            // A Worker without its reverse Employee link is already inconsistent. Do not mutate
            // half of that graph; the local job systems own repairing or replacing the job.
            if (employeeIndex < 0) return;

            if (worker.m_Level != wanted.WorkerLevel)
            {
                worker.m_Level = wanted.WorkerLevel;
                EntityManager.SetComponentData(citizen, worker);
            }
            Employee employee = employees[employeeIndex];
            if (employee.m_Level != wanted.WorkerLevel)
            {
                employee.m_Level = wanted.WorkerLevel;
                employees[employeeIndex] = employee;
            }
        }

        private void ApplyPets(Entity household, Entity property, OccupancyHousehold wanted)
        {
            DedupePets(household);
            _memberScratch.Clear();
            if (EntityManager.HasBuffer<HouseholdAnimal>(household))
            {
                DynamicBuffer<HouseholdAnimal> animals =
                    EntityManager.GetBuffer<HouseholdAnimal>(household, true);
                for (int i = 0; i < animals.Length; i++) _memberScratch.Add(animals[i].m_HouseholdPet);
            }

            _claimedPets.Clear();
            _missingPetPrefabs.Clear();
            for (int i = 0; i < wanted.Pets.Length; i++)
            {
                Entity match = Entity.Null;
                for (int j = 0; j < _memberScratch.Count; j++)
                {
                    Entity candidate = _memberScratch[j];
                    if (_claimedPets.Contains(candidate) || candidate == Entity.Null ||
                        !EntityManager.Exists(candidate) ||
                        !EntityManager.HasComponent<PrefabRef>(candidate)) continue;
                    string localName = PrefabIndex.SafeName(_prefabSystem,
                        EntityManager.GetComponentData<PrefabRef>(candidate).m_Prefab);
                    if (!string.Equals(localName, wanted.Pets[i], StringComparison.Ordinal)) continue;
                    match = candidate;
                    break;
                }
                if (match == Entity.Null) _missingPetPrefabs.Add(wanted.Pets[i]);
                else _claimedPets.Add(match);
            }

            if (_claimedPets.Count == _memberScratch.Count && _missingPetPrefabs.Count == 0) return;
            if (IsSettling(household))
            {
                ScheduleReapply(property);
                return;
            }

            for (int i = _memberScratch.Count - 1; i >= 0; i--)
            {
                Entity pet = _memberScratch[i];
                if (_claimedPets.Contains(pet) || pet == Entity.Null || !EntityManager.Exists(pet) ||
                    EntityManager.HasComponent<Deleted>(pet)) continue;
                EntityManager.AddComponent<Deleted>(pet);
            }

            for (int i = 0; i < _missingPetPrefabs.Count; i++)
            {
                if (CreatePet(household, property, _missingPetPrefabs[i]) == Entity.Null) break;
                _createdPets++;
                MarkSettling(household);
                ScheduleReapply(property);
            }
        }

        // ---- Creation and retirement -------------------------------------------

        private static Entity CreateEntityFromArchetype(EntityManager em, EntityArchetype archetype)
        {
            using (var batch = new Unity.Collections.NativeArray<Entity>(1, Unity.Collections.Allocator.Temp))
            {
                em.CreateEntity(archetype, batch);
                return batch[0];
            }
        }

        private Entity CreateHousehold(Entity property, OccupancyHousehold wanted)
        {
            if (!CanEnqueueRentAction()) return Entity.Null;
            Entity prefab;
            EntityArchetype archetype;
            if (!ResolvePrefab<HouseholdData>(wanted.PrefabName, out prefab, out archetype))
                return Entity.Null;

            Entity household = CreateEntityFromArchetype(EntityManager, archetype);
            SetOrAdd(household, new PrefabRef(prefab));
            // No CurrentBuilding: that component is what asks the game to populate a household with
            // a randomly drawn family. The roster already says who lives here.
            Household data = EntityManager.GetComponentData<Household>(household);
            data.m_Flags = (HouseholdFlags)(wanted.Flags & HouseholdFlagMask);
            data.m_Resources = wanted.Savings;
            data.m_ConsumptionPerDay = wanted.ConsumptionPerDay;
            data.m_ShoppedValuePerDay = wanted.ShoppedValuePerDay;
            data.m_ShoppedValueLastDay = wanted.ShoppedValueLastDay;
            data.m_LastDayFrameIndex = wanted.LastDayFrameIndex;
            data.m_SalaryLastDay = wanted.SalaryLastDay;
            data.m_MoneySpendOnBuildingLevelingLastDay =
                wanted.MoneySpentOnBuildingLevelingLastDay;
            EntityManager.SetComponentData(household, data);
            if (!BindHousehold(wanted.HouseholdId, household))
            {
                EntityManager.AddComponent<Deleted>(household);
                return Entity.Null;
            }
            if (!EntityManager.HasBuffer<Resources>(household))
                EntityManager.AddBuffer<Resources>(household);
            EconomyUtils.SetResources(Resource.Money,
                EntityManager.GetBuffer<Resources>(household), wanted.Money);

            if (EntityManager.HasComponent<PropertySeeker>(household))
                EntityManager.SetComponentEnabled<PropertySeeker>(household, false);
            ApplyNameIndices(household, wanted.NameIndices);
            if (!EnqueueRentAction(property, household))
            {
                UnbindHousehold(household);
                if (!EntityManager.HasComponent<Deleted>(household))
                    EntityManager.AddComponent<Deleted>(household);
                return Entity.Null;
            }

            Entity arrivalSource = SelectArrivalSource(wanted.HouseholdId);
            if (arrivalSource != Entity.Null)
                _arrivalSources[household] = arrivalSource;
            else
            {
                arrivalSource = property;
                if (!_arrivalSourceWarned)
                {
                    _arrivalSourceWarned = true;
                    Mod.Verbose("[MP] Occupancy: no live road outside connection was " +
                                "available; new families will start at home.");
                }
            }

            // A new host household can be a split containing an already-known citizen. Funnel the
            // roster through the keyed reconciler so existing people move with their job/path state
            // instead of being cloned and rebound to a second local entity.
            CreateInitialOwnedVehicles(household, property, arrivalSource, wanted);
            ApplyCitizens(household, property, wanted);
            ApplyPets(household, property, wanted);
            MarkSettling(household);
            return household;
        }

        private Entity CreateCitizen(Entity household, Entity property, OccupancyCitizen wanted)
        {
            Entity prefab;
            EntityArchetype archetype;
            if (!TryGetCitizenCreationPrefab(out prefab, out archetype))
                return Entity.Null;

            Entity citizen = CreateEntityFromArchetype(EntityManager, archetype);
            SetOrAdd(citizen, new PrefabRef(prefab));
            SetOrAdd(citizen, new HouseholdMember { m_Household = household });
            SetOrAdd(citizen, new CurrentBuilding
            {
                m_CurrentBuilding = GetVehicleCreationSource(household, property),
            });
            // The game's citizen initialization reads 0..4 as an age class rather than a calendar
            // day, and turns it into a plausible birthday, education band and prefab. Seed the
            // class it expects; the next reconcile replaces the result with the host's own values.
            SetOrAdd(citizen, new Citizen
            {
                m_BirthDay = SeedAgeClass(wanted),
                m_State = (CitizenFlags)(wanted.State &
                    (short)(CitizenFlags.Tourist | CitizenFlags.Commuter)),
            });
            if (!BindCitizen(wanted.CitizenId, citizen))
            {
                EntityManager.AddComponent<Deleted>(citizen);
                return Entity.Null;
            }
            return citizen;
        }

        private static short SeedAgeClass(OccupancyCitizen wanted)
        {
            var state = (CitizenFlags)wanted.State;
            var probe = new Citizen { m_State = state };
            switch (probe.GetAge())
            {
                case CitizenAge.Adult: return 1;
                case CitizenAge.Elderly: return 3;
                default: return 2;
            }
        }

        private Entity CreatePet(Entity household, Entity property, string prefabName)
        {
            Entity prefab;
            EntityArchetype archetype;
            if (!ResolvePrefab<HouseholdPetData>(prefabName, out prefab, out archetype))
                return Entity.Null;

            Entity pet = CreateEntityFromArchetype(EntityManager, archetype);
            SetOrAdd(pet, new PrefabRef(prefab));
            SetOrAdd(pet, new HouseholdPet { m_Household = household });
            SetOrAdd(pet, new CurrentBuilding
            {
                m_CurrentBuilding = GetVehicleCreationSource(household, property),
            });
            LinkPet(household, pet);
            return pet;
        }

        private Entity CreateOwnedVehicle(Entity household, Entity source, ulong householdId,
            string prefabName, int ordinal)
        {
            Entity prefab;
            if (!_prefabIndex.TryResolve(prefabName,
                    candidate => EntityManager.HasComponent<PersonalCarData>(candidate) &&
                                 EntityManager.HasComponent<global::Game.Prefabs.CarData>(candidate) &&
                                 EntityManager.HasComponent<MovingObjectData>(candidate),
                    out prefab) ||
                !EntityManager.HasComponent<global::Game.Objects.Transform>(source))
                return Entity.Null;
            EntityArchetype archetype =
                EntityManager.GetComponentData<MovingObjectData>(prefab).m_StoppedArchetype;
            if (!archetype.Valid) return Entity.Null;

            Entity vehicle = CreateEntityFromArchetype(EntityManager, archetype);
            SetOrAdd(vehicle,
                EntityManager.GetComponentData<global::Game.Objects.Transform>(source));
            SetOrAdd(vehicle, new global::Game.Vehicles.PersonalCar(
                Entity.Null, default(PersonalCarFlags)));
            SetOrAdd(vehicle, new PrefabRef(prefab));
            ushort seed = unchecked((ushort)(householdId ^ (ulong)(ordinal + 1) * 40503UL));
            SetOrAdd(vehicle, new PseudoRandomSeed(seed == 0 ? (ushort)1 : seed));
            SetOrAdd(vehicle, new global::Game.Objects.TripSource(source));
            SetOrAdd(vehicle, default(global::Game.Objects.Unspawned));
            SetOrAdd(vehicle, new Owner(household));
            return vehicle;
        }

        /// <summary>
        /// Hand the move-in to the game's own renter pipeline instead of writing the link by hand.
        /// It is the code that clears the old property, adds PropertyRenter, appends to the renter
        /// list, drops HomelessHousehold and raises RentersUpdated — all in the order the rest of
        /// the simulation expects.
        /// </summary>
        private bool CanEnqueueRentAction() =>
            _propertyProcessing != null && _propertyProcessing.Enabled;

        private void TrackPendingMoveIn(OccupancyHousehold wanted, Entity household,
            Entity property, ulong revision, bool createdLocally)
        {
            PendingMoveIn pending;
            if (_pendingMoveIns.TryGetValue(wanted.HouseholdId, out pending))
            {
                if (revision < pending.Revision) return;
                pending.Household = household;
                pending.Property = property;
                pending.Rent = wanted.Rent;
                pending.Revision = revision;
                pending.CreatedLocally |= createdLocally;
                return;
            }

            while (_pendingMoveIns.Count >= MaxPendingMoveIns &&
                   _pendingMoveInOrder.TryDequeue(out ulong oldest))
                _pendingMoveIns.Remove(oldest);
            if (_pendingMoveIns.Count >= MaxPendingMoveIns) return;
            _pendingMoveIns[wanted.HouseholdId] = new PendingMoveIn
            {
                HouseholdId = wanted.HouseholdId,
                Household = household,
                Property = property,
                Rent = wanted.Rent,
                Revision = revision,
                CreatedLocally = createdLocally,
            };
            _pendingMoveInOrder.Enqueue(wanted.HouseholdId);
        }

        /// <summary>
        /// Runs immediately after PropertyProcessingSystem. A queued move-in is complete only when
        /// both native renter directions agree; at that point its host rent is installed before the
        /// later payment pass can consume the locally selected default.
        /// </summary>
        internal void FinalizeMoveIns()
        {
            MultiplayerService service = Mod.Service;
            if (service == null || !service.GameplaySyncReady ||
                service.Session.Role != SessionRole.Client || _pendingMoveIns.Count == 0) return;

            int remaining = math.min(MaxMoveInFinalizationsPerUpdate, _pendingMoveInOrder.Count);
            while (remaining-- > 0 && _pendingMoveInOrder.TryDequeue(out ulong householdId))
            {
                PendingMoveIn pending;
                if (!_pendingMoveIns.TryGetValue(householdId, out pending)) continue;
                Entity mapped;
                bool mappingValid = TryResolveHousehold(householdId, out mapped) &&
                                    mapped == pending.Household;
                if (!IsHouseholdDesiredHere(householdId, pending.Property) ||
                    !mappingValid || !IsLiveProperty(pending.Property))
                {
                    _pendingMoveIns.Remove(householdId);
                    PropertyRentIdentity desiredIdentity;
                    if (pending.CreatedLocally && mappingValid &&
                        !TryGetDesiredPropertyIdentity(householdId, out desiredIdentity))
                    {
                        if (!CleanupCancelledCreatedHousehold(pending.Household))
                        {
                            _pendingMoveIns[householdId] = pending;
                            _pendingMoveInOrder.Enqueue(householdId);
                        }
                    }
                    else
                    {
                        Entity destination;
                        if (TryGetDesiredProperty(householdId, out destination))
                            MarkDirty(destination);
                    }
                    continue;
                }
                if (!IsHouseholdAtProperty(pending.Household, pending.Property))
                {
                    _pendingMoveInOrder.Enqueue(householdId);
                    continue;
                }

                OccupancyHousehold newest;
                ulong newestRevision;
                if (TryGetCachedHousehold(pending.Property, householdId, out newest,
                    out newestRevision) && newestRevision >= pending.Revision)
                {
                    pending.Rent = newest.Rent;
                    pending.Revision = newestRevision;
                    CachedProperty cached;
                    if (_cache.TryGetValue(pending.Property, out cached))
                        NotePlacedHousehold(cached, newest, pending.Household);
                }
                CancelUnauthorizedDeparture(pending.Household);
                PropertyRenter rented =
                    EntityManager.GetComponentData<PropertyRenter>(pending.Household);
                if (rented.m_Rent != pending.Rent)
                {
                    rented.m_Rent = pending.Rent;
                    EntityManager.SetComponentData(pending.Household, rented);
                }
                _pendingMoveIns.Remove(householdId);
                _stagedTransfers.Remove(householdId);
                _stagedTransferCooldownUntil.Remove(householdId);
                MarkDirty(pending.Property);
                ScheduleReapply(pending.Property);
            }
        }

        /// <summary>
        /// Every person and vehicle the host listed for this family is linked here now, so it no
        /// longer counts as arriving: a car it buys later has to leave from the house rather than
        /// from the outside connection it came in by.
        /// </summary>
        private void NotePlacedHousehold(CachedProperty property, OccupancyHousehold household,
            Entity localHousehold)
        {
            int localPeople = CountRealizedCitizens(localHousehold, household);
            int wantedPeople = household.Citizens != null ? household.Citizens.Length : 0;
            if (localPeople != wantedPeople) return;
            int localVehicles = CountLiveOwnedVehicles(localHousehold);
            int wantedVehicles = household.OwnedVehicles != null
                ? household.OwnedVehicles.Length : 0;
            if (localVehicles < wantedVehicles) return;
            _arrivalSources.Remove(localHousehold);
            TracePlacedHousehold(property, household, localPeople, wantedPeople, localVehicles,
                wantedVehicles);
        }

        // Reached only once every exact citizen identity is linked, so a missing family member
        // shows up as RECEIVED-without-PLACED in a host/client log comparison.
        [Conditional(DevTrace.Symbol)]
        private void TracePlacedHousehold(CachedProperty property, OccupancyHousehold household,
            int localPeople, int wantedPeople, int localVehicles, int wantedVehicles)
        {
            PropertyRentIdentity previous;
            if (_tracePlacedHouseholds.TryGetValue(household.HouseholdId, out previous) &&
                previous.Equals(property.Identity)) return;
            _tracePlacedHouseholds[household.HouseholdId] = property.Identity;
            Mod.log.Info("[MP][OCC-DEV] PLACED house='" + property.Identity.PrefabName +
                         "' anchor=(" + property.Identity.AnchorX.ToString("F2") + ", " +
                         property.Identity.AnchorY.ToString("F2") + ", " +
                         property.Identity.AnchorZ.ToString("F2") + ") rev=" +
                         property.Revision + " family=0x" +
                         household.HouseholdId.ToString("X16") + " people=" + localPeople +
                         "/" + wantedPeople + " vehicles=" + localVehicles + "/" +
                         wantedVehicles + ".");
        }

        private int CountRealizedCitizens(Entity household, OccupancyHousehold wanted)
        {
            if (household == Entity.Null || !EntityManager.Exists(household) ||
                !EntityManager.HasBuffer<HouseholdCitizen>(household) || wanted.Citizens == null)
                return 0;
            DynamicBuffer<HouseholdCitizen> members =
                EntityManager.GetBuffer<HouseholdCitizen>(household, true);
            int liveMembers = 0;
            for (int i = 0; i < members.Length; i++)
            {
                Entity citizen = members[i].m_Citizen;
                if (citizen != Entity.Null && EntityManager.Exists(citizen) &&
                    !EntityManager.HasComponent<Deleted>(citizen) &&
                    EntityManager.HasComponent<HouseholdMember>(citizen) &&
                    EntityManager.GetComponentData<HouseholdMember>(citizen).m_Household == household)
                    liveMembers++;
            }
            if (liveMembers != wanted.Citizens.Length) return liveMembers;

            int matched = 0;
            for (int i = 0; i < wanted.Citizens.Length; i++)
            {
                Entity citizen;
                if (TryResolveCitizen(wanted.Citizens[i].CitizenId, out citizen) &&
                    CitizenBelongsToHousehold(citizen, household)) matched++;
            }
            return matched;
        }

        private int CountLiveOwnedVehicles(Entity household)
        {
            if (household == Entity.Null || !EntityManager.Exists(household) ||
                !EntityManager.HasBuffer<OwnedVehicle>(household)) return 0;
            DynamicBuffer<OwnedVehicle> owned = EntityManager.GetBuffer<OwnedVehicle>(household, true);
            int count = 0;
            for (int i = 0; i < owned.Length; i++)
            {
                Entity vehicle = owned[i].m_Vehicle;
                if (vehicle == Entity.Null || !EntityManager.Exists(vehicle) ||
                    EntityManager.HasComponent<Deleted>(vehicle) ||
                    !EntityManager.HasComponent<global::Game.Vehicles.PersonalCar>(vehicle) ||
                    !EntityManager.HasComponent<Owner>(vehicle) ||
                    EntityManager.GetComponentData<Owner>(vehicle).m_Owner != household) continue;
                count++;
            }
            return count;
        }

        private bool TryGetCachedHousehold(Entity property, ulong householdId,
            out OccupancyHousehold household, out ulong revision)
        {
            household = default(OccupancyHousehold);
            revision = 0;
            CachedProperty cached;
            if (!_cache.TryGetValue(property, out cached) || cached.Households == null) return false;
            for (int i = 0; i < cached.Households.Length; i++)
            {
                if (cached.Households[i].HouseholdId != householdId ||
                    cached.Households[i].Departing) continue;
                household = cached.Households[i];
                revision = cached.Revision;
                return true;
            }
            return false;
        }

        private bool CleanupCancelledCreatedHousehold(Entity household)
        {
            if (!IsLiveMappedHousehold(household)) return true;
            Entity rentedProperty = Entity.Null;
            if (EntityManager.HasComponent<PropertyRenter>(household))
            {
                rentedProperty = EntityManager.GetComponentData<PropertyRenter>(household)
                    .m_Property;
            }

            if (EntityManager.HasBuffer<HouseholdCitizen>(household))
            {
                _memberScratch.Clear();
                DynamicBuffer<HouseholdCitizen> members =
                    EntityManager.GetBuffer<HouseholdCitizen>(household, true);
                for (int i = 0; i < members.Length; i++)
                    _memberScratch.Add(members[i].m_Citizen);
                bool waitingForDestination = false;
                for (int i = 0; i < _memberScratch.Count; i++)
                {
                    Entity citizen = _memberScratch[i];
                    if (citizen == Entity.Null || !EntityManager.Exists(citizen)) continue;
                    ulong citizenId, desiredHouseholdId;
                    if (TryGetBoundCitizenId(citizen, out citizenId) &&
                        TryGetDesiredHouseholdId(citizenId, out desiredHouseholdId))
                    {
                        Entity destinationHousehold, destinationProperty;
                        if (TryResolveHousehold(desiredHouseholdId, out destinationHousehold) &&
                            TryGetDesiredProperty(desiredHouseholdId, out destinationProperty) &&
                            destinationHousehold != household)
                        {
                            MoveCitizenToHousehold(citizen, destinationHousehold);
                            MarkDirty(destinationProperty);
                        }
                        else
                        {
                            if (TryGetDesiredProperty(desiredHouseholdId, out destinationProperty))
                                MarkDirty(destinationProperty);
                            waitingForDestination = true;
                        }
                        continue;
                    }
                    UnbindCitizen(citizen);
                    if (!EntityManager.HasComponent<Deleted>(citizen))
                        EntityManager.AddComponent<Deleted>(citizen);
                }
                if (waitingForDestination)
                {
                    _memberScratch.Clear();
                    return false;
                }
            }
            // Keep the shell's native two-way rent link intact while any active resident still
            // waits for a resolvable destination. Once everyone is detached, the shell can no
            // longer strand a person and its slot is safe to release.
            if (rentedProperty != Entity.Null && EntityManager.Exists(rentedProperty))
                RemoveRenterReference(rentedProperty, household);
            if (EntityManager.HasBuffer<HouseholdAnimal>(household))
            {
                _memberScratch.Clear();
                DynamicBuffer<HouseholdAnimal> pets =
                    EntityManager.GetBuffer<HouseholdAnimal>(household, true);
                for (int i = 0; i < pets.Length; i++)
                    _memberScratch.Add(pets[i].m_HouseholdPet);
                for (int i = 0; i < _memberScratch.Count; i++)
                {
                    Entity pet = _memberScratch[i];
                    if (pet != Entity.Null && EntityManager.Exists(pet) &&
                        !EntityManager.HasComponent<Deleted>(pet))
                        EntityManager.AddComponent<Deleted>(pet);
                }
            }
            _memberScratch.Clear();
            UnbindHousehold(household);
            if (!EntityManager.HasComponent<Deleted>(household))
                EntityManager.AddComponent<Deleted>(household);
            return true;
        }

        private bool EnqueueRentAction(Entity property, Entity household)
        {
            if (!CanEnqueueRentAction() || property == Entity.Null || household == Entity.Null ||
                !EntityManager.Exists(property) || !EntityManager.Exists(household)) return false;
            JobHandle dependencies;
            NativeQueue<RentAction> queue = _propertyProcessing.GetRentActionQueue(out dependencies);
            dependencies.Complete();
            queue.Enqueue(new RentAction { m_Property = property, m_Renter = household });
            _rentActions++;
            return true;
        }

        /// <summary>
        /// Retire a household the host no longer houses here. MovingAway is the game's own
        /// emigration path: it frees the renter slot on the next rent pass, files the right
        /// statistics, and deletes the household once its people have left the city.
        /// </summary>
        private bool Retire(Entity household)
        {
            if (household == Entity.Null || !EntityManager.Exists(household)) return false;
            if (EntityManager.HasComponent<Deleted>(household)) return false;
            // The every-frame client lifecycle guard removes locally-authored MovingAway markers.
            // Mark this one first so the native executor can consume the host-requested retirement.
            _authorizedMoveAways.Add(household);
            if (!EntityManager.HasComponent<MovingAway>(household))
                EntityManager.AddComponentData(household,
                    new MovingAway { m_Reason = MoveAwayReason.NoSuitableProperty });
            _settling.Remove(household);
            return true;
        }

        private void UnbindDepartingHousehold(Entity household)
        {
            if (household != Entity.Null && EntityManager.Exists(household) &&
                EntityManager.HasBuffer<HouseholdCitizen>(household))
            {
                DynamicBuffer<HouseholdCitizen> members =
                    EntityManager.GetBuffer<HouseholdCitizen>(household, true);
                for (int i = 0; i < members.Length; i++)
                {
                    Entity citizen = members[i].m_Citizen;
                    ulong citizenId, desiredHouseholdId;
                    if (!TryGetBoundCitizenId(citizen, out citizenId) ||
                        TryGetDesiredHouseholdId(citizenId, out desiredHouseholdId)) continue;
                    UnbindCitizen(citizenId);
                }
            }
            UnbindHousehold(household);
        }

        // ---- Helpers -----------------------------------------------------------

        private void MarkSettling(Entity household)
        {
            _settling[household] = _simulationSystem.frameIndex + SettleFrames;
        }

        /// <summary>Ask for one more pass over this property on the next update.</summary>
        private void ScheduleReapply(Entity property)
        {
            if (_reapply.Count >= MaxDirtyProperties) return;
            _reapply.Add(property);
        }

        private bool IsSettling(Entity household)
        {
            uint until;
            if (!_settling.TryGetValue(household, out until)) return false;
            if (_simulationSystem.frameIndex >= until)
            {
                _settling.Remove(household);
                return false;
            }
            return true;
        }

        private void PruneSettling()
        {
            if (_settling.Count == 0) return;
            uint now = _simulationSystem.frameIndex;
            _settlingScratch.Clear();
            foreach (KeyValuePair<Entity, uint> pair in _settling)
                if (now >= pair.Value || !EntityManager.Exists(pair.Key))
                    _settlingScratch.Add(pair.Key);
            for (int i = 0; i < _settlingScratch.Count; i++) _settling.Remove(_settlingScratch[i]);
            _settlingScratch.Clear();
        }

        private bool TryGetCitizenCreationPrefab(out Entity prefab,
            out EntityArchetype archetype)
        {
            prefab = _citizenCreationPrefab;
            if (prefab != Entity.Null && EntityManager.Exists(prefab) &&
                EntityManager.HasComponent<CitizenData>(prefab) &&
                EntityManager.HasComponent<ArchetypeData>(prefab))
            {
                archetype = EntityManager.GetComponentData<ArchetypeData>(prefab).m_Archetype;
                if (archetype.Valid) return true;
            }

            _citizenCreationPrefab = Entity.Null;
            archetype = default(EntityArchetype);
            NativeArray<Entity> prefabs = _citizenCreationPrefabs.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < prefabs.Length; i++)
                {
                    Entity candidate = prefabs[i];
                    if (candidate == Entity.Null || !EntityManager.Exists(candidate) ||
                        !EntityManager.HasComponent<ArchetypeData>(candidate)) continue;
                    EntityArchetype candidateArchetype =
                        EntityManager.GetComponentData<ArchetypeData>(candidate).m_Archetype;
                    if (!candidateArchetype.Valid) continue;
                    _citizenCreationPrefab = candidate;
                    prefab = candidate;
                    archetype = candidateArchetype;
                    return true;
                }
            }
            finally
            {
                prefabs.Dispose();
            }
            prefab = Entity.Null;
            return false;
        }

        private bool ResolveCitizenPrefab(string name, out Entity prefab) =>
            _prefabIndex.TryResolve(name,
                candidate => EntityManager.HasComponent<CitizenData>(candidate), out prefab);

        private Entity SelectArrivalSource(ulong householdId)
        {
            NativeArray<Entity> candidates =
                _arrivalOutsideConnections.ToEntityArray(Allocator.Temp);
            try
            {
                int roadCount = 0;
                for (int i = 0; i < candidates.Length; i++)
                    if (IsRoadArrivalSource(candidates[i])) roadCount++;
                if (roadCount == 0) return Entity.Null;

                ulong mixed = householdId;
                mixed ^= mixed >> 33;
                mixed *= 0xff51afd7ed558ccdUL;
                mixed ^= mixed >> 33;
                int selected = (int)(mixed % (ulong)roadCount);
                for (int i = 0; i < candidates.Length; i++)
                {
                    Entity candidate = candidates[i];
                    if (!IsRoadArrivalSource(candidate)) continue;
                    if (selected-- == 0) return candidate;
                }
                return Entity.Null;
            }
            finally
            {
                candidates.Dispose();
            }
        }

        private bool IsRoadArrivalSource(Entity entity)
        {
            if (entity == Entity.Null || !EntityManager.Exists(entity) ||
                EntityManager.HasComponent<Deleted>(entity) ||
                EntityManager.HasComponent<global::Game.Tools.Temp>(entity) ||
                EntityManager.HasComponent<global::Game.Objects.ElectricityOutsideConnection>(
                    entity) ||
                EntityManager.HasComponent<global::Game.Objects.WaterPipeOutsideConnection>(
                    entity) ||
                !EntityManager.HasComponent<global::Game.Objects.OutsideConnection>(entity) ||
                !EntityManager.HasComponent<global::Game.Objects.Transform>(entity) ||
                !EntityManager.HasComponent<PrefabRef>(entity)) return false;
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab;
            if (prefab == Entity.Null || !EntityManager.Exists(prefab) ||
                !EntityManager.HasComponent<OutsideConnectionData>(prefab)) return false;
            OutsideConnectionData data =
                EntityManager.GetComponentData<OutsideConnectionData>(prefab);
            return (data.m_Type & OutsideConnectionTransferType.Road) !=
                   OutsideConnectionTransferType.None;
        }

        private Entity GetVehicleCreationSource(Entity household, Entity property)
        {
            Entity source;
            if (_arrivalSources.TryGetValue(household, out source))
            {
                if (IsRoadArrivalSource(source)) return source;
                _arrivalSources.Remove(household);
            }
            return property;
        }

        private string SafePrefabName(Entity entity)
        {
            if (entity == Entity.Null || !EntityManager.Exists(entity) ||
                !EntityManager.HasComponent<PrefabRef>(entity)) return "<none>";
            return PrefabIndex.SafeName(_prefabSystem,
                EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab);
        }

        private bool ResolvePrefab<T>(string name, out Entity prefab)
            where T : unmanaged, IComponentData =>
            _prefabIndex.TryResolve(name,
                candidate => EntityManager.HasComponent<T>(candidate) &&
                             EntityManager.HasComponent<ArchetypeData>(candidate), out prefab);

        private bool ResolvePrefab<T>(string name, out Entity prefab, out EntityArchetype archetype)
            where T : unmanaged, IComponentData
        {
            archetype = default(EntityArchetype);
            if (!ResolvePrefab<T>(name, out prefab)) return false;
            archetype = EntityManager.GetComponentData<ArchetypeData>(prefab).m_Archetype;
            return archetype.Valid;
        }

        private void SetOrAdd<T>(Entity entity, T value) where T : unmanaged, IComponentData
        {
            if (EntityManager.HasComponent<T>(entity)) EntityManager.SetComponentData(entity, value);
            else EntityManager.AddComponentData(entity, value);
        }
    }
}
