using System;
using System.Collections.Generic;
using CS2MultiplayerMod.Core.Session;
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

        private const byte HouseholdFlagMask =
            (byte)(HouseholdFlags.Tourist | HouseholdFlags.Commuter | HouseholdFlags.MovedIn);

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

        private const int MaxUnreachableRetiredPerUpdate = 8;

        private readonly Dictionary<Entity, uint> _settling = new Dictionary<Entity, uint>();
        private readonly Dictionary<Entity, uint> _unreachableSince = new Dictionary<Entity, uint>();
        private readonly List<Entity> _localHouseholds = new List<Entity>();
        private readonly List<Entity> _memberScratch = new List<Entity>();
        private readonly List<Entity> _settlingScratch = new List<Entity>();
        private readonly HashSet<Entity> _appliedThisUpdate = new HashSet<Entity>();
        private readonly HashSet<Entity> _unreachableSeen = new HashSet<Entity>();
        private readonly List<Entity> _reapply = new List<Entity>();
        private readonly Budget _budget = new Budget();
        private bool _applyWarned;

        private sealed class Budget
        {
            public int Properties;
            public int HouseholdsCreated;
            public int CitizensCreated;
            public int HouseholdsRetired;

            public void Reset()
            {
                Properties = 0;
                HouseholdsCreated = 0;
                CitizensCreated = 0;
                HouseholdsRetired = 0;
            }

            public bool Exhausted =>
                Properties >= MaxPropertiesAppliedPerUpdate ||
                HouseholdsCreated >= MaxHouseholdsCreatedPerUpdate ||
                CitizensCreated >= MaxCitizensCreatedPerUpdate ||
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
                for (int i = 0; i < snapshot.Properties.Count; i++)
                    ResolveOrPend(snapshot.Properties[i], now, search, candidates);
            }
        }

        private void ResolveOrPend(OccupancyProperty wanted, long now, ObjectSearch.Batch search,
            NativeList<Entity> candidates)
        {
            bool ambiguous;
            Entity property = ResolveProperty(wanted, search, candidates, out ambiguous);
            if (property != Entity.Null)
            {
                Cache(property, wanted);
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
                pending.Property = wanted;
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
                if (property != Entity.Null)
                {
                    Cache(property, pending.Property);
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
        /// Find the local building a roster entry describes. An exact prefab match is preferred,
        /// but a residential building standing on the same spot is accepted when the prefab differs.
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
                    Consider(candidate, distance, ref exact, ref exactDistance, ref exactAmbiguous);
                Consider(candidate, distance, ref nearest, ref nearestDistance, ref nearestAmbiguous);
            }

            if (exact != Entity.Null && !exactAmbiguous) return exact;
            ambiguous = exact != Entity.Null ? exactAmbiguous : nearestAmbiguous;
            return nearest != Entity.Null && !nearestAmbiguous ? nearest : Entity.Null;
        }

        private static void Consider(Entity candidate, float distance, ref Entity best,
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

        private void Cache(Entity property, OccupancyProperty wanted)
        {
            int bucket = (int)(EntityManager.GetSharedComponent<UpdateFrame>(property).m_Index %
                               UpdatePartitions);
            CachedProperty cached;
            if (!_cache.TryGetValue(property, out cached))
            {
                if (_cache.Count >= MaxCachedProperties)
                {
                    _cacheDrops++;
                    return;
                }
                cached = new CachedProperty();
                _cache[property] = cached;
            }
            cached.Identity = wanted.Identity;
            cached.Prefab = EntityManager.GetComponentData<PrefabRef>(property).m_Prefab;
            cached.ConstructionSpeed = wanted.ConstructionSpeed;
            cached.Households = wanted.Households;
            cached.Bucket = bucket;
            AddToCacheBucket(bucket, property);
            MarkDirty(property);
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
                    uint since;
                    if (!_unreachableSince.TryGetValue(household, out since))
                    {
                        _unreachableSince[household] = now;
                        continue;
                    }
                    if (now - since < UnreachableGraceFrames) continue;
                    if (retired >= MaxUnreachableRetiredPerUpdate) break;
                    if (!Retire(household)) continue;
                    _unreachableSince.Remove(household);
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
                _cache.Remove(property);
                return;
            }
            try
            {
                ApplyProperty(property, cached);
            }
            catch (Exception ex)
            {
                // One malformed property must not take the whole reconcile down. Drop its cache so
                // the next page re-resolves it from scratch.
                _cache.Remove(property);
                if (!_applyWarned)
                {
                    _applyWarned = true;
                    Mod.log.Warn("[MP] Occupancy: reconcile failed for one property; dropped it " +
                                 "until the next page (logged once): " + ex.Message);
                }
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
            float3 position = EntityManager
                .GetComponentData<global::Game.Objects.Transform>(property).m_Position;
            var anchor = new float2(cached.Identity.AnchorX, cached.Identity.AnchorZ);
            if (math.distancesq(position.xz, anchor) >
                AnchorMatchDistance * AnchorMatchDistance) return false;
            cached.Prefab = EntityManager.GetComponentData<PrefabRef>(property).m_Prefab;
            return true;
        }

        private void ApplyProperty(Entity property, CachedProperty cached)
        {
            OccupancyHousehold[] wanted = cached.Households;
            bool localUnderConstruction = ApplyConstruction(property, cached.ConstructionSpeed);

            CollectLocalHouseholds(property);
            int local = _localHouseholds.Count;

            int matched = math.min(local, wanted.Length);
            for (int i = 0; i < matched; i++)
                ApplyHousehold(_localHouseholds[i], property, wanted[i]);

            // A move-in this system asked for is only in the renter list once the game's rent
            // pipeline has run. Counting the building's families before that would ask for the
            // same family again.
            if (IsSettling(property)) return;

            // Do not move a family into a building this peer is still putting up when the host's
            // is already finished: the two are describing different things, and the completion
            // just forced above lands on the next update anyway. Retirement and the numbers on
            // families already living here are unaffected.
            bool hostFinished = cached.ConstructionSpeed == 0;
            if (localUnderConstruction && hostFinished)
            {
                _deferredForConstruction++;
                ScheduleReapply(property);
                if (local <= wanted.Length) return;
            }

            if (local > wanted.Length)
            {
                for (int i = wanted.Length; i < local; i++)
                {
                    if (_budget.HouseholdsRetired >= MaxHouseholdsRetiredPerUpdate) break;
                    if (!Retire(_localHouseholds[i])) continue;
                    _budget.HouseholdsRetired++;
                    _retiredHouseholds++;
                    MarkSettling(property);
                }
                return;
            }

            int free = FreeResidentialSlots(property);
            for (int i = local; i < wanted.Length; i++)
            {
                if (_budget.HouseholdsCreated >= MaxHouseholdsCreatedPerUpdate ||
                    _budget.CitizensCreated >= MaxCitizensCreatedPerUpdate) break;
                if (wanted[i].Citizens.Length == 0) continue;
                if (free <= 0)
                {
                    // The local building has fewer homes than the host's - normally a level change
                    // that has not reached this peer yet. Retried on the next pass.
                    _refusedMoveIns++;
                    break;
                }
                if (CreateHousehold(property, wanted[i]) == Entity.Null) break;
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
            DynamicBuffer<Renter> renters = EntityManager.GetBuffer<Renter>(property, true);
            for (int i = 0; i < renters.Length; i++)
            {
                Entity renter = renters[i].m_Renter;
                if (renter == Entity.Null || !EntityManager.Exists(renter)) continue;
                if (!EntityManager.HasComponent<Household>(renter)) continue;
                if (EntityManager.HasComponent<Deleted>(renter) ||
                    EntityManager.HasComponent<MovingAway>(renter)) continue;
                if (EntityManager.HasComponent<TouristHousehold>(renter) ||
                    EntityManager.HasComponent<CommuterHousehold>(renter)) continue;
                _localHouseholds.Add(renter);
            }
        }

        private int FreeResidentialSlots(Entity property)
        {
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(property).m_Prefab;
            if (!EntityManager.HasComponent<BuildingPropertyData>(prefab)) return 0;
            int capacity = EntityManager.GetComponentData<BuildingPropertyData>(prefab)
                .CountProperties(global::Game.Zones.AreaType.Residential);
            DynamicBuffer<Renter> renters = EntityManager.GetBuffer<Renter>(property, true);
            for (int i = 0; i < renters.Length; i++)
                if (EntityManager.HasComponent<Household>(renters[i].m_Renter)) capacity--;
            return capacity;
        }

        private void ApplyHousehold(Entity household, Entity property, OccupancyHousehold wanted)
        {
            Household data = EntityManager.GetComponentData<Household>(household);
            var flags = (HouseholdFlags)(wanted.Flags & HouseholdFlagMask);
            if (data.m_Flags != flags || data.m_Resources != wanted.Savings)
            {
                data.m_Flags = flags;
                data.m_Resources = wanted.Savings;
                EntityManager.SetComponentData(household, data);
            }

            if (EntityManager.HasBuffer<Resources>(household))
            {
                DynamicBuffer<Resources> resources = EntityManager.GetBuffer<Resources>(household);
                if (EconomyUtils.GetResources(Resource.Money, resources) != wanted.Money)
                    EconomyUtils.SetResources(Resource.Money, resources, wanted.Money);
            }

            if (EntityManager.HasComponent<PropertyRenter>(household))
            {
                PropertyRenter rented = EntityManager.GetComponentData<PropertyRenter>(household);
                if (rented.m_Property == property && rented.m_Rent != wanted.Rent)
                {
                    rented.m_Rent = wanted.Rent;
                    EntityManager.SetComponentData(household, rented);
                }
            }

            ApplyNameIndices(household, wanted.NameIndices);
            ApplyCitizens(household, property, wanted);
            ApplyPets(household, property, wanted);
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

            int matched = math.min(_memberScratch.Count, wanted.Citizens.Length);
            for (int i = 0; i < matched; i++)
                ApplyCitizen(_memberScratch[i], wanted.Citizens[i]);

            if (IsSettling(household)) return;

            if (_memberScratch.Count > wanted.Citizens.Length)
            {
                // Never empty a household this way: the game retires a household whose last
                // resident is deleted, and the roster still wants this one to exist.
                int keep = math.max(1, wanted.Citizens.Length);
                for (int i = _memberScratch.Count - 1; i >= keep; i--)
                {
                    Entity citizen = _memberScratch[i];
                    if (citizen == Entity.Null || !EntityManager.Exists(citizen) ||
                        EntityManager.HasComponent<Deleted>(citizen)) continue;
                    // Deleting the citizen is the game's own removal path: it unlinks the person
                    // from the household, cancels their job seeking and disposes their bicycle.
                    EntityManager.AddComponent<Deleted>(citizen);
                    _removedCitizens++;
                }
                return;
            }

            for (int i = _memberScratch.Count; i < wanted.Citizens.Length; i++)
            {
                if (_budget.CitizensCreated >= MaxCitizensCreatedPerUpdate) break;
                if (CreateCitizen(household, property, wanted.Citizens[i]) == Entity.Null) break;
                _budget.CitizensCreated++;
                _createdCitizens++;
                MarkSettling(household);
                ScheduleReapply(property);
            }
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
                if (duplicate || citizen == Entity.Null || !EntityManager.Exists(citizen))
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
        /// Link a citizen or pet this system created into its household immediately. The game's
        /// initialization pass will append it a second time next frame; the dedupe pass above
        /// collapses that. Linking now rather than waiting keeps the member count honest, so the
        /// next reconcile cannot mistake a pending resident for a missing one.
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
                data.m_WellBeing != wanted.WellBeing)
            {
                data.m_State = state;
                data.m_PseudoRandom = wanted.PseudoRandom;
                data.m_BirthDay = wanted.BirthDay;
                data.m_Health = wanted.Health;
                data.m_WellBeing = wanted.WellBeing;
                EntityManager.SetComponentData(citizen, data);
                _rewrittenCitizens++;
            }

            Entity prefab;
            if (ResolvePrefab<CitizenData>(wanted.PrefabName, out prefab) &&
                EntityManager.HasComponent<PrefabRef>(citizen) &&
                EntityManager.GetComponentData<PrefabRef>(citizen).m_Prefab != prefab)
                EntityManager.SetComponentData(citizen, new PrefabRef(prefab));

            ApplyNameIndices(citizen, wanted.NameIndices);
            ApplyWageLevel(citizen, wanted);
        }

        /// <summary>
        /// Household income is the sum of each resident's wage bracket, so aligning the bracket is
        /// what makes the panel's income figure agree. Which company hired them is not on the wire
        /// and stays a local decision: when only one machine found this person a job, the income
        /// figures differ until the other one does too.
        /// </summary>
        private void ApplyWageLevel(Entity citizen, OccupancyCitizen wanted)
        {
            if (!wanted.Employed || !EntityManager.HasComponent<Worker>(citizen)) return;
            Worker worker = EntityManager.GetComponentData<Worker>(citizen);
            if (worker.m_Level == wanted.WorkerLevel) return;
            worker.m_Level = wanted.WorkerLevel;
            EntityManager.SetComponentData(citizen, worker);

            // The workplace keeps its own copy of the level for company output; leaving that behind
            // would make the two disagree about the same job.
            Entity workplace = worker.m_Workplace;
            if (workplace == Entity.Null || !EntityManager.Exists(workplace) ||
                !EntityManager.HasBuffer<Employee>(workplace)) return;
            DynamicBuffer<Employee> employees = EntityManager.GetBuffer<Employee>(workplace);
            for (int i = 0; i < employees.Length; i++)
            {
                if (employees[i].m_Worker != citizen) continue;
                Employee employee = employees[i];
                employee.m_Level = wanted.WorkerLevel;
                employees[i] = employee;
                break;
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
            if (_memberScratch.Count == wanted.Pets.Length) return;
            if (IsSettling(household)) return;

            if (_memberScratch.Count > wanted.Pets.Length)
            {
                for (int i = _memberScratch.Count - 1; i >= wanted.Pets.Length; i--)
                {
                    Entity pet = _memberScratch[i];
                    if (pet == Entity.Null || !EntityManager.Exists(pet) ||
                        EntityManager.HasComponent<Deleted>(pet)) continue;
                    EntityManager.AddComponent<Deleted>(pet);
                }
                return;
            }

            for (int i = _memberScratch.Count; i < wanted.Pets.Length; i++)
            {
                if (CreatePet(household, property, wanted.Pets[i]) == Entity.Null) break;
                _createdPets++;
                MarkSettling(household);
                ScheduleReapply(property);
            }
        }

        // ---- Creation and retirement -------------------------------------------

        private Entity CreateHousehold(Entity property, OccupancyHousehold wanted)
        {
            Entity prefab;
            EntityArchetype archetype;
            if (!ResolvePrefab<HouseholdData>(wanted.PrefabName, out prefab, out archetype))
                return Entity.Null;

            Entity household = EntityManager.CreateEntity(archetype);
            SetOrAdd(household, new PrefabRef(prefab));
            // No CurrentBuilding: that component is what asks the game to populate a household with
            // a randomly drawn family. The roster already says who lives here.
            Household data = EntityManager.GetComponentData<Household>(household);
            data.m_Flags = (HouseholdFlags)(wanted.Flags & HouseholdFlagMask);
            data.m_Resources = wanted.Savings;
            EntityManager.SetComponentData(household, data);
            if (!EntityManager.HasBuffer<Resources>(household))
                EntityManager.AddBuffer<Resources>(household);
            EconomyUtils.SetResources(Resource.Money,
                EntityManager.GetBuffer<Resources>(household), wanted.Money);

            for (int i = 0; i < wanted.Citizens.Length; i++)
            {
                if (CreateCitizen(household, property, wanted.Citizens[i]) == Entity.Null) break;
                _budget.CitizensCreated++;
                _createdCitizens++;
            }
            for (int i = 0; i < wanted.Pets.Length; i++)
            {
                if (CreatePet(household, property, wanted.Pets[i]) == Entity.Null) break;
                _createdPets++;
            }
            MarkSettling(household);

            if (EntityManager.HasComponent<PropertySeeker>(household))
                EntityManager.SetComponentEnabled<PropertySeeker>(household, false);
            EnqueueRentAction(property, household);
            return household;
        }

        private Entity CreateCitizen(Entity household, Entity property, OccupancyCitizen wanted)
        {
            Entity prefab;
            EntityArchetype archetype;
            if (!ResolvePrefab<CitizenData>(wanted.PrefabName, out prefab, out archetype))
                return Entity.Null;

            Entity citizen = EntityManager.CreateEntity(archetype);
            SetOrAdd(citizen, new PrefabRef(prefab));
            SetOrAdd(citizen, new HouseholdMember { m_Household = household });
            SetOrAdd(citizen, new CurrentBuilding { m_CurrentBuilding = property });
            // The game's citizen initialization reads 0..4 as an age class rather than a calendar
            // day, and turns it into a plausible birthday, education band and prefab. Seed the
            // class it expects; the next reconcile replaces the result with the host's own values.
            SetOrAdd(citizen, new Citizen { m_BirthDay = SeedAgeClass(wanted) });
            LinkCitizen(household, citizen);
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

            Entity pet = EntityManager.CreateEntity(archetype);
            SetOrAdd(pet, new PrefabRef(prefab));
            SetOrAdd(pet, new HouseholdPet { m_Household = household });
            SetOrAdd(pet, new CurrentBuilding { m_CurrentBuilding = property });
            LinkPet(household, pet);
            return pet;
        }

        /// <summary>
        /// Hand the move-in to the game's own renter pipeline instead of writing the link by hand.
        /// It is the code that clears the old property, adds PropertyRenter, appends to the renter
        /// list, drops HomelessHousehold and raises RentersUpdated — all in the order the rest of
        /// the simulation expects.
        /// </summary>
        private void EnqueueRentAction(Entity property, Entity household)
        {
            if (_propertyProcessing == null || !_propertyProcessing.Enabled) return;
            JobHandle dependencies;
            NativeQueue<RentAction> queue = _propertyProcessing.GetRentActionQueue(out dependencies);
            dependencies.Complete();
            queue.Enqueue(new RentAction { m_Property = property, m_Renter = household });
            _rentActions++;
        }

        /// <summary>
        /// Retire a household the host no longer houses here. MovingAway is the game's own
        /// emigration path: it frees the renter slot on the next rent pass, files the right
        /// statistics, and deletes the household once its people have left the city.
        /// </summary>
        private bool Retire(Entity household)
        {
            if (household == Entity.Null || !EntityManager.Exists(household)) return false;
            if (EntityManager.HasComponent<MovingAway>(household) ||
                EntityManager.HasComponent<Deleted>(household)) return false;
            EntityManager.AddComponentData(household,
                new MovingAway { m_Reason = MoveAwayReason.NoSuitableProperty });
            _settling.Remove(household);
            return true;
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
