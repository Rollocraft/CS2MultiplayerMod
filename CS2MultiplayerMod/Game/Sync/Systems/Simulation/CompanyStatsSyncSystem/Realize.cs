using System;
using System.Collections.Generic;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.Infrastructure;
using Game.Buildings;
using Game.Common;
using Game.Companies;
using Game.Economy;
using Game.Prefabs;
using Game.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Game.Diagnostics;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    public partial class CompanyStatsSyncSystem
    {
        // ---- Resolve arrived pages (read-only, runs from the city-state pump) -----------------

        /// <summary>
        /// Turn arrived pages into resolved cache entries. Read-only against ECS, so it is safe
        /// and cheap to run every frame; every write stays in <see cref="OnUpdate"/>.
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
            CompanyStatsSnapshot snapshot;
            int pages = 0;
            while (pages < maxPages && _incoming.TryDequeue(out snapshot))
            {
                pages++;
                _receivedPages++;
                _clientSweepId = snapshot.SweepId;
                for (int i = 0; i < snapshot.Entries.Count; i++)
                    ResolveOrPend(snapshot.Entries[i], snapshot.SweepId, now, search, candidates);
                if (snapshot.EndOfSweep) PruneCacheAfterCompleteSweep(snapshot.SweepId);
            }
        }

        private void ResolveOrPend(CompanyStatsEntry entry, uint sweepId, long now,
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
            PendingEntry pending;
            if (_pending.TryGetValue(identity, out pending))
            {
                pending.Entry = entry;
                pending.SweepId = sweepId;
                return;
            }
            if (_pending.Count >= MaxPendingIdentities) return;
            _pending[identity] = new PendingEntry
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

        private void RetryPending(long now, ObjectSearch.Batch search, NativeList<Entity> candidates)
        {
            if (_pending.Count == 0) return;
            int examined = 0;
            PropertyRentIdentity identity;
            while (examined++ < MaxPendingRetriesPerUpdate && _pendingOrder.TryDequeue(out identity))
            {
                PendingEntry pending;
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
                    continue;
                }
                pending.NextAttemptMs = now + ResolveRetryMs;
                _pendingOrder.Enqueue(identity);
            }
        }

        /// <summary>
        /// Find the local building an entry describes, with the same rule the rent channel uses:
        /// position is the identity, and the prefab only breaks a same-distance tie, because a
        /// workplace that levels up keeps its spot and swaps its prefab and the two peers do not
        /// level at the same moment. Two equally good candidates stay unresolved rather than
        /// risking one business landing permanently in its neighbour's building.
        /// </summary>
        private Entity ResolveProperty(CompanyStatsEntry entry, ObjectSearch.Batch search,
            NativeList<Entity> candidates, out bool ambiguous)
        {
            ambiguous = false;
            Entity prefab;
            _prefabIndex.TryResolve(entry.PrefabName,
                candidate => EntityManager.HasComponent<BuildingPropertyData>(candidate),
                out prefab);

            var anchor = new float3(entry.AnchorX, entry.AnchorY, entry.AnchorZ);
            search.CollectNear(anchor, AnchorSearchRadius, candidates);
            Entity exact = Entity.Null, nearest = Entity.Null;
            float exactDistance = 0f, nearestDistance = 0f;
            bool exactAmbiguous = false, nearestAmbiguous = false;
            for (int i = 0; i < candidates.Length; i++)
            {
                Entity candidate = candidates[i];
                if (!IsLiveWorkplaceProperty(candidate)) continue;
                float distance = math.distancesq(
                    EntityManager.GetComponentData<global::Game.Objects.Transform>(candidate)
                        .m_Position.xz, anchor.xz);
                if (distance > AnchorMatchDistance * AnchorMatchDistance) continue;
                if (prefab != Entity.Null &&
                    EntityManager.GetComponentData<PrefabRef>(candidate).m_Prefab == prefab)
                    ConsiderCandidate(candidate, distance, ref exact, ref exactDistance,
                        ref exactAmbiguous);
                ConsiderCandidate(candidate, distance, ref nearest, ref nearestDistance,
                    ref nearestAmbiguous);
            }
            if (exact != Entity.Null && !exactAmbiguous) return exact;
            ambiguous = exact != Entity.Null ? exactAmbiguous : nearestAmbiguous;
            return nearest != Entity.Null && !nearestAmbiguous ? nearest : Entity.Null;
        }

        private static void ConsiderCandidate(Entity candidate, float distance, ref Entity best,
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

        private void Cache(Entity property, CompanyStatsEntry entry, uint sweepId)
        {
            CachedEntry cached;
            if (!_cache.TryGetValue(property, out cached))
            {
                if (_cache.Count >= MaxCachedProperties) return;
                cached = new CachedEntry();
                _cache[property] = cached;
                _tenancyOrder.Add(property);
            }
            bool tenancyChanged = cached.Entry.HasTenant != entry.HasTenant ||
                                  !string.Equals(cached.Entry.CompanyPrefabName,
                                      entry.CompanyPrefabName, StringComparison.Ordinal);
            cached.Entry = entry;
            cached.LastSeenSweep = sweepId;
            // Only a tenancy difference needs the structural pass. A page that merely moves money
            // is picked up by the figure correction, which costs a comparison.
            if (tenancyChanged) MarkDirty(property);
        }

        private void MarkDirty(Entity property)
        {
            if (!_dirtyMembers.Add(property)) return;
            _dirty.Add(property);
            // Shedding the oldest is safe: the entry is still cached, so the rolling walk repairs
            // it rather than losing it.
            if (_dirty.Count <= MaxDirtyProperties) return;
            _dirtyMembers.Remove(_dirty[0]);
            _dirty.RemoveAt(0);
        }

        private void PruneCacheAfterCompleteSweep(uint sweepId)
        {
            _cacheScratch.Clear();
            foreach (KeyValuePair<Entity, CachedEntry> pair in _cache)
                if (pair.Value.LastSeenSweep != sweepId || !IsLiveWorkplaceProperty(pair.Key))
                    _cacheScratch.Add(pair.Key);
            for (int i = 0; i < _cacheScratch.Count; i++)
            {
                Entity property = _cacheScratch[i];
                _cache.Remove(property);
                _dirtyMembers.Remove(property);
                _settling.Remove(property);
            }
            if (_cacheScratch.Count > 0) RebuildTenancyOrder();
            _cacheScratch.Clear();
        }

        private void RebuildTenancyOrder()
        {
            _tenancyOrder.Clear();
            foreach (KeyValuePair<Entity, CachedEntry> pair in _cache) _tenancyOrder.Add(pair.Key);
            _tenancyCursor = 0;
        }

        // ---- Figures: correct the partition the game just recomputed --------------------------

        /// <summary>
        /// Deliberately uncapped over its partition. A ceiling here would leave part of the
        /// partition holding the values the game wrote microseconds ago, which is exactly the
        /// flicker this design exists to remove; the per-company cost is a dictionary lookup and a
        /// field comparison, and the partition is already a sixteenth of the city.
        /// </summary>
        private void ApplyFigures(uint updateFrame)
        {
            if (_cache.Count == 0) return;
            _companies.SetSharedComponentFilter(new UpdateFrame(updateFrame));
            NativeArray<Entity> companies = default(NativeArray<Entity>);
            try
            {
                companies = _companies.ToEntityArray(Allocator.Temp);

                // Sort the partition into its three zones first, then correct each zone under its
                // own timer. One channel serves all three, so a single scope could only ever say
                // "companies cost this much"; splitting it is what makes "commercial is the
                // expensive one" a measurement instead of a hunch. The classification is one
                // component test per business, and it replaces the per-business property lookup
                // the correction loop would have done anyway.
                _commercialBucket.Clear();
                _industrialBucket.Clear();
                _officeBucket.Clear();
                for (int i = 0; i < companies.Length; i++)
                {
                    Entity company = companies[i];
                    Entity property =
                        EntityManager.GetComponentData<PropertyRenter>(company).m_Property;
                    if (property == Entity.Null) continue;
                    switch (ZoneOf(property))
                    {
                        case SyncZone.Commercial: _commercialBucket.Add(company); break;
                        case SyncZone.Industrial: _industrialBucket.Add(company); break;
                        case SyncZone.Office: _officeBucket.Add(company); break;
                    }
                }

                ApplyZoneFigures(_commercialBucket, SyncZone.Commercial, "Companies.Commercial");
                ApplyZoneFigures(_industrialBucket, SyncZone.Industrial, "Companies.Industrial");
                ApplyZoneFigures(_officeBucket, SyncZone.Office, "Companies.Office");
            }
            finally
            {
                if (companies.IsCreated) companies.Dispose();
                _companies.ResetFilter();
            }
        }

        private void ApplyZoneFigures(List<Entity> companies, SyncZone zone, string scope)
        {
            if (companies.Count == 0) return;
            using (Diagnostics.SyncProfiler.Measure(scope, zone))
            {
                int applied = 0;
                for (int i = 0; i < companies.Count; i++)
                {
                    Entity company = companies[i];
                    Entity property =
                        EntityManager.GetComponentData<PropertyRenter>(company).m_Property;
                    CachedEntry cached;
                    if (!_cache.TryGetValue(property, out cached)) continue;
                    if (!cached.Entry.HasTenant) continue;
                    // A business the host does not have in this building gets no figures; the
                    // tenancy pass is what resolves that difference.
                    if (!TenantMatches(company, cached.Entry)) continue;
                    ApplyCompany(company, cached.Entry);
                    applied++;
                }
                _appliedCompanies += applied;
                _zoneApplied[(int)zone] += applied;
            }
        }

        /// <summary>Which of the three workplace zones a building belongs to.</summary>
        private SyncZone ZoneOf(Entity property)
        {
            if (property == Entity.Null || !EntityManager.Exists(property)) return SyncZone.None;
            if (EntityManager.HasComponent<CommercialProperty>(property)) return SyncZone.Commercial;
            if (EntityManager.HasComponent<IndustrialProperty>(property)) return SyncZone.Industrial;
            if (EntityManager.HasComponent<OfficeProperty>(property)) return SyncZone.Office;
            return SyncZone.None;
        }

        private bool TenantMatches(Entity company, CompanyStatsEntry entry)
        {
            if (!EntityManager.HasComponent<PrefabRef>(company)) return false;
            string local = _prefabIndex.NameOf(
                EntityManager.GetComponentData<PrefabRef>(company).m_Prefab);
            return string.Equals(local, entry.CompanyPrefabName, StringComparison.Ordinal);
        }

        private void ApplyCompany(Entity company, CompanyStatsEntry entry)
        {
            CompanyStatisticData data = EntityManager.GetComponentData<CompanyStatisticData>(company);
            CompanyStatisticData wanted = data;
            wanted.m_MaxNumberOfCustomers = entry.MaxNumberOfCustomers;
            wanted.m_MonthlyCustomerCount = entry.MonthlyCustomerCount;
            wanted.m_MonthlyCostBuyingResources = entry.MonthlyCostBuyingResources;
            wanted.m_CurrentNumberOfCustomers = entry.CurrentNumberOfCustomers;
            wanted.m_CurrentCostOfBuyingResources = entry.CurrentCostOfBuyingResources;
            wanted.m_Income = entry.Income;
            wanted.m_Worth = entry.Worth;
            wanted.m_Profit = entry.Profit;
            wanted.m_WagePaid = entry.WagePaid;
            wanted.m_RentPaid = entry.RentPaid;
            wanted.m_ElectricityPaid = entry.ElectricityPaid;
            wanted.m_WaterPaid = entry.WaterPaid;
            wanted.m_SewagePaid = entry.SewagePaid;
            wanted.m_GarbagePaid = entry.GarbagePaid;
            wanted.m_TaxPaid = entry.TaxPaid;
            wanted.m_CostBuyResource = entry.CostBuyResource;
            wanted.m_LastUpdateWorth = entry.LastUpdateWorth;
            wanted.m_LastUpdateProduce = entry.LastUpdateProduce;
            wanted.m_LastFrameLowIncome = entry.LastFrameLowIncome;
            if (!SameStatistics(data, wanted))
            {
                EntityManager.SetComponentData(company, wanted);
                _correctedFields++;
            }

            // Only when the sender actually had the component. A company without a rating must not
            // be given a fabricated one.
            if (entry.HasProfitability && EntityManager.HasComponent<Profitability>(company))
            {
                Profitability profitability = EntityManager.GetComponentData<Profitability>(company);
                if (profitability.m_Profitability != entry.Profitability ||
                    profitability.m_LastTotalWorth != entry.LastTotalWorth)
                {
                    profitability.m_Profitability = entry.Profitability;
                    profitability.m_LastTotalWorth = entry.LastTotalWorth;
                    EntityManager.SetComponentData(company, profitability);
                    _correctedFields++;
                }
            }

            ApplyResources(company, entry);
        }

        /// <summary>
        /// The goods on the shelves. This is the one part of the block that is not purely
        /// displayed - what a business holds feeds its own selling, producing and delivery - so it
        /// is written as an absolute statement and every resource the host did not report is
        /// cleared, exactly as an absolute roster clears an absent household.
        /// </summary>
        private void ApplyResources(Entity company, CompanyStatsEntry entry)
        {
            if (!EntityManager.HasBuffer<global::Game.Economy.Resources>(company)) return;
            CompanyStatsResource[] wanted = entry.Resources;
            DynamicBuffer<global::Game.Economy.Resources> resources =
                EntityManager.GetBuffer<global::Game.Economy.Resources>(company);

            bool changed = false;
            for (int i = 0; i < EconomyUtils.ResourceCount; i++)
            {
                Resource resource = EconomyUtils.GetResource(i);
                int desired = 0;
                if (wanted != null)
                {
                    for (int w = 0; w < wanted.Length; w++)
                    {
                        if (wanted[w].Index != i) continue;
                        desired = wanted[w].Amount;
                        break;
                    }
                }
                if (EconomyUtils.GetResources(resource, resources) == desired) continue;
                EconomyUtils.SetResources(resource, resources, desired);
                changed = true;
            }
            if (changed) _correctedResources++;
        }

        private static bool SameStatistics(CompanyStatisticData first, CompanyStatisticData second) =>
            first.m_MaxNumberOfCustomers == second.m_MaxNumberOfCustomers &&
            first.m_MonthlyCustomerCount == second.m_MonthlyCustomerCount &&
            first.m_MonthlyCostBuyingResources == second.m_MonthlyCostBuyingResources &&
            first.m_CurrentNumberOfCustomers == second.m_CurrentNumberOfCustomers &&
            first.m_CurrentCostOfBuyingResources == second.m_CurrentCostOfBuyingResources &&
            first.m_Income == second.m_Income && first.m_Worth == second.m_Worth &&
            first.m_Profit == second.m_Profit && first.m_WagePaid == second.m_WagePaid &&
            first.m_RentPaid == second.m_RentPaid &&
            first.m_ElectricityPaid == second.m_ElectricityPaid &&
            first.m_WaterPaid == second.m_WaterPaid && first.m_SewagePaid == second.m_SewagePaid &&
            first.m_GarbagePaid == second.m_GarbagePaid && first.m_TaxPaid == second.m_TaxPaid &&
            first.m_CostBuyResource == second.m_CostBuyResource &&
            first.m_LastUpdateWorth == second.m_LastUpdateWorth &&
            first.m_LastUpdateProduce == second.m_LastUpdateProduce &&
            first.m_LastFrameLowIncome == second.m_LastFrameLowIncome;

        // ---- Tenancy: make the right business occupy the right building -----------------------

        /// <summary>
        /// Buildings whose tenancy a page just changed are handled first; whatever budget is left
        /// goes to a small rolling window, which repairs drift the host never reported. In a
        /// settled city both are empty or cheap: a building whose tenant already matches costs one
        /// buffer read and a string comparison, and nothing structural happens at all.
        /// </summary>
        private void ApplyTenancy()
        {
            if (_cache.Count == 0) return;
            using (Diagnostics.SyncProfiler.Measure("Companies.Tenancy"))
            {
                ApplyTenancyCore();
            }
        }

        private void ApplyTenancyCore()
        {
            PruneSettling();

            int created = 0, retired = 0;
            int processed = 0;
            while (processed < _dirty.Count &&
                   (created < MaxCompaniesCreatedPerUpdate ||
                    retired < MaxCompaniesRetiredPerUpdate))
            {
                Entity property = _dirty[processed++];
                _dirtyMembers.Remove(property);
                ReconcileTenancy(property, ref created, ref retired);
            }
            if (processed > 0) _dirty.RemoveRange(0, processed);

            if (created >= MaxCompaniesCreatedPerUpdate && retired >= MaxCompaniesRetiredPerUpdate)
                return;

            int walked = 0;
            while (walked < MaxTenancyWalkedPerUpdate && _tenancyOrder.Count > 0 &&
                   (created < MaxCompaniesCreatedPerUpdate ||
                    retired < MaxCompaniesRetiredPerUpdate))
            {
                if (_tenancyCursor >= _tenancyOrder.Count) _tenancyCursor = 0;
                Entity property = _tenancyOrder[_tenancyCursor++];
                walked++;
                if (!_cache.ContainsKey(property))
                {
                    _tenancyOrder.RemoveAt(--_tenancyCursor);
                    continue;
                }
                ReconcileTenancy(property, ref created, ref retired);
            }
        }

        private void ReconcileTenancy(Entity property, ref int created, ref int retired)
        {
            CachedEntry cached;
            if (!_cache.TryGetValue(property, out cached)) return;
            if (!IsLiveWorkplaceProperty(property)) return;
            // The move-in this building asked for is still in the native queue. Acting again
            // before it drains opens a second business or undoes the first.
            if (IsSettling(property)) { _deferredActions++; return; }

            Entity local = FindTenant(property);
            CompanyStatsEntry entry = cached.Entry;

            if (!entry.HasTenant)
            {
                if (local == Entity.Null) return;
                if (retired >= MaxCompaniesRetiredPerUpdate) { _deferredActions++; return; }
                if (RetireCompany(local, property)) retired++;
                return;
            }

            if (local != Entity.Null)
            {
                if (TenantMatches(local, entry)) return;
                // A different business than the host has. Close this one now; the next pass sees
                // an empty building and opens the right one, which keeps the two structural
                // changes in separate frames.
                if (retired >= MaxCompaniesRetiredPerUpdate) { _deferredActions++; return; }
                if (RetireCompany(local, property)) retired++;
                return;
            }

            if (created >= MaxCompaniesCreatedPerUpdate) { _deferredActions++; return; }
            if (CreateCompany(property, entry)) created++;
        }

        /// <summary>
        /// Closes a business through the game's own emigration path rather than deleting it, so
        /// the native systems unwind its renter link, put the building back on the market and
        /// raise the renter event the rest of the simulation is waiting for.
        /// </summary>
        private bool RetireCompany(Entity company, Entity property)
        {
            if (!EntityManager.Exists(company) ||
                EntityManager.HasComponent<Deleted>(company)) return false;
            if (!EntityManager.HasComponent<global::Game.Agents.MovingAway>(company))
                EntityManager.AddComponentData(company, default(global::Game.Agents.MovingAway));
            // Whitelisted so the every-update boundary does not immediately cancel our own
            // request along with the local proposals it is there to strip.
            AuthorizeMoveAway(company);
            BeginSettling(property);
            _retiredCompanies++;
            _zoneClosed[(int)ZoneOf(property)]++;
            return true;
        }

        /// <summary>
        /// Opens the business the host reports, the same way the game's own spawner does: create
        /// the entity from the company prefab's archetype, point it at that prefab, and hand the
        /// move-in to the native rent-action queue so the whole transaction runs as the game
        /// intends rather than being hand-written.
        /// </summary>
        private bool CreateCompany(Entity property, CompanyStatsEntry entry)
        {
            Entity prefab;
            if (!_prefabIndex.TryResolve(entry.CompanyPrefabName,
                    candidate => EntityManager.HasComponent<ArchetypeData>(candidate),
                    out prefab) || prefab == Entity.Null)
                return false;
            if (!EntityManager.HasComponent<ArchetypeData>(prefab)) return false;

            EntityArchetype archetype =
                EntityManager.GetComponentData<ArchetypeData>(prefab).m_Archetype;
            if (archetype == default(EntityArchetype)) return false;

            // The native transaction is what actually moves the business in. Without a consumer
            // for it there would be a company entity with nowhere to live, so check first.
            if (_propertyProcessing == null || !_propertyProcessing.Enabled) return false;

            Entity company = EntityManager.CreateEntity(archetype);
            EntityManager.SetComponentData(company, new PrefabRef { m_Prefab = prefab });

            Unity.Jobs.JobHandle dependencies;
            NativeQueue<RentAction> queue =
                _propertyProcessing.GetRentActionQueue(out dependencies);
            dependencies.Complete();
            queue.Enqueue(new RentAction { m_Property = property, m_Renter = company });

            BeginSettling(property);
            _createdCompanies++;
            _zoneOpened[(int)ZoneOf(property)]++;
            return true;
        }

        private void BeginSettling(Entity property) =>
            _settling[property] = _simulationSystem.frameIndex + SettleFrames;

        private bool IsSettling(Entity property)
        {
            uint until;
            if (!_settling.TryGetValue(property, out until)) return false;
            if (FramePrecedes(_simulationSystem.frameIndex, until)) return true;
            _settling.Remove(property);
            return false;
        }

        private void PruneSettling()
        {
            if (_settling.Count == 0) return;
            uint now = _simulationSystem.frameIndex;
            _settlingScratch.Clear();
            foreach (KeyValuePair<Entity, uint> pair in _settling)
                if (!FramePrecedes(now, pair.Value)) _settlingScratch.Add(pair.Key);
            for (int i = 0; i < _settlingScratch.Count; i++)
                _settling.Remove(_settlingScratch[i]);
            _settlingScratch.Clear();
        }

        /// <summary>Wrap-safe "now is still before the deadline".</summary>
        private static bool FramePrecedes(uint frame, uint deadline) =>
            unchecked((int)(deadline - frame)) > 0;
    }
}
