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
using Game.Objects;
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
                if (_clientSweepId != snapshot.SweepId)
                {
                    _clientSweepId = snapshot.SweepId;
                    _clientNextPage = 0;
                    _clientSweepIntact = true;
                }
                if (snapshot.PageIndex != _clientNextPage) _clientSweepIntact = false;
                if (snapshot.PageIndex >= _clientNextPage)
                    _clientNextPage = snapshot.PageIndex + 1;
                for (int i = 0; i < snapshot.Entries.Count; i++)
                    ResolveOrPend(snapshot.Entries[i], snapshot.SweepId, now, search, candidates);
                if (snapshot.EndOfSweep)
                {
                    // Pruning is only safe after every page in the absolute sweep arrived. A
                    // coalesced/dropped middle page says nothing about the buildings it carried.
                    if (_clientSweepIntact) PruneCacheAfterCompleteSweep(snapshot.SweepId);
                    _clientSweepId = 0;
                    _clientNextPage = 0;
                    _clientSweepIntact = false;
                }
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
            // Every arrived value is applied at the 16-frame state boundary. Waiting for this
            // company's statistics partition can otherwise take 2,048 frames, during which the
            // local economy repeatedly overwrites the host values.
            MarkStateDirty(property);
            if (entry.HasEfficiency) MarkEfficiencyDirty(property);
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

        /// <summary>
        /// New information arrived about this building - an arrived page, a renter event, a
        /// changed employee buffer. That clears the retry budget: the reason a previous attempt
        /// came back incomplete may be exactly what just turned up.
        /// </summary>
        private void MarkStateDirty(Entity property)
        {
            _stateRetries.Remove(property);
            EnqueueStateDirty(property);
        }

        /// <summary>
        /// Re-arm after an attempt that could not finish, but only for a few passes.
        ///
        /// <see cref="ApplyEmployees"/> reports incomplete whenever one employee's citizen id is
        /// not in this peer's occupancy map, and that does not become true by asking again on the
        /// next boundary - only a page that binds the citizen changes it, and that path marks the
        /// building dirty itself. Re-arming unconditionally pinned the queue at 1,501 of 1,560
        /// cached buildings, so the 128-per-boundary drain ran full forever on work that could
        /// never succeed. After the budget the bounded rolling walk still visits the building.
        /// </summary>
        private void RetryStateDirty(Entity property)
        {
            int attempts;
            _stateRetries.TryGetValue(property, out attempts);
            if (attempts >= MaxStateRetries) return;
            _stateRetries[property] = attempts + 1;
            EnqueueStateDirty(property);
        }

        private void EnqueueStateDirty(Entity property)
        {
            if (!_stateDirtyMembers.Add(property)) return;
            _stateDirty.Add(property);
            if (_stateDirty.Count <= MaxDirtyProperties) return;
            _stateDirtyMembers.Remove(_stateDirty[0]);
            _stateDirty.RemoveAt(0);
        }

        private void MarkEfficiencyDirty(Entity property)
        {
            if (!_efficiencyDirtyMembers.Add(property)) return;
            _efficiencyDirty.Add(property);
            if (_efficiencyDirty.Count <= MaxDirtyProperties) return;
            _efficiencyDirtyMembers.Remove(_efficiencyDirty[0]);
            _efficiencyDirty.RemoveAt(0);
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
                _stateDirtyMembers.Remove(property);
                _stateRetries.Remove(property);
                _efficiencyDirtyMembers.Remove(property);
                _clientEfficiencyObserved.Remove(property);
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
            _stateCursor = 0;
        }

        // ---- Fast state boundary --------------------------------------------------------------

        /// <summary>
        /// Called after the native job-matching cadence. Tenancy and newly arrived state are
        /// applied here so names, panel figures and real worker links settle within 16 frames.
        /// </summary>
        internal void ApplyClientStateBoundary()
        {
            MultiplayerService service = Mod.Service;
            if (service == null || !service.GameplaySyncReady ||
                service.Session.Role != SessionRole.Client) return;

            PumpIncoming();
            ApplyTenancy();
            ApplyChangedEfficiencies();
            ApplyChangedState();
        }

        /// <summary>
        /// Client only: the host already signals a changed efficiency buffer from the 16-frame
        /// state boundary, and doing it twice would only cost the host priority slots.
        /// </summary>
        internal bool WantsProductionBoundary
        {
            get
            {
                MultiplayerService service = Mod.Service;
                return service != null && service.GameplaySyncReady &&
                       service.Session.Role == SessionRole.Client && _cache.Count != 0;
            }
        }

        internal bool WantsExtractorProduceBoundary
        {
            get
            {
                MultiplayerService service = Mod.Service;
                return service != null && service.GameplaySyncReady;
            }
        }

        /// <summary>
        /// Runs directly after a native company production pass, over the properties that pass just
        /// wrote, and repairs them in the same frame: the selected-building panel recalculates
        /// production from these factors on every UI frame, so a repair one boundary later is a
        /// repair the panel has already read past.
        /// </summary>
        internal void ApplyProductionBoundary(NativeArray<Entity> properties)
        {
            if (!WantsProductionBoundary) return;
            if (properties.IsCreated) CaptureEfficiencyChanges(properties);
            ApplyChangedEfficiencies();
        }

        /// <summary>
        /// Puts the host's extraction figure back on a company the local extractor pass has just
        /// recalculated. Only that one field: the rest of the statistic block belongs to the
        /// slower accounting boundary, which owns its own change detection.
        /// </summary>
        private void ApplyExtractorProduce(Entity company, Entity property)
        {
            CachedEntry cached;
            if (!_cache.TryGetValue(property, out cached) || !cached.Entry.HasTenant) return;
            if (!TenantMatches(company, cached.Entry)) return;
            CompanyStatisticData data =
                EntityManager.GetComponentData<CompanyStatisticData>(company);
            if (data.m_LastUpdateProduce == cached.Entry.LastUpdateProduce) return;
            data.m_LastUpdateProduce = cached.Entry.LastUpdateProduce;
            EntityManager.SetComponentData(company, data);
            _correctedExtractorProduce++;
        }

        /// <summary>
        /// Efficiency changes are kept on their own cheap queue. Re-applying a complete company
        /// and resolving hundreds of employee identities merely because one utility factor moved
        /// would undo the CPU-load reduction in the state retry path.
        /// </summary>
        private void ApplyChangedEfficiencies()
        {
            int processed = _efficiencyDirty.Count < MaxEfficiencyDirtyPerBoundary
                ? _efficiencyDirty.Count : MaxEfficiencyDirtyPerBoundary;
            for (int i = 0; i < processed; i++)
            {
                Entity property = _efficiencyDirty[i];
                _efficiencyDirtyMembers.Remove(property);
                CachedEntry cached;
                if (!_cache.TryGetValue(property, out cached) ||
                    !IsLiveWorkplaceProperty(property) || !cached.Entry.HasEfficiency) continue;
                ApplyPropertyEfficiency(property, cached.Entry);
            }
            if (processed > 0) _efficiencyDirty.RemoveRange(0, processed);
        }

        /// <summary>
        /// Rebuilds the property's real native efficiency buffer. CompanySection does not display
        /// LastUpdateProduce for processing industry or offices: it multiplies these factors and
        /// feeds the result, together with the real Employee roster, into
        /// EconomyUtils.GetCompanyProductionPerDay.
        /// </summary>
        private bool ApplyPropertyEfficiency(Entity property, CompanyStatsEntry entry)
        {
            if (!entry.HasEfficiency) return true;
            if (!EntityManager.HasBuffer<Efficiency>(property)) return false;

            CompanyStatsEfficiency[] wanted = entry.Efficiencies;
            int wantedCount = wanted == null ? 0 : wanted.Length;
            DynamicBuffer<Efficiency> local = EntityManager.GetBuffer<Efficiency>(property);
            bool changed = local.Length != wantedCount;
            if (!changed)
            {
                for (int i = 0; i < wantedCount; i++)
                {
                    if ((byte)local[i].m_Factor == wanted[i].Factor &&
                        local[i].m_Efficiency == wanted[i].Value) continue;
                    changed = true;
                    break;
                }
            }

            if (changed)
            {
                local.Clear();
                for (int i = 0; i < wantedCount; i++)
                {
                    local.Add(new Efficiency
                    {
                        m_Factor = (EfficiencyFactor)wanted[i].Factor,
                        m_Efficiency = wanted[i].Value,
                    });
                }
                _correctedEfficiencies++;
            }

            // DynamicBuffer writes advance the chunk version. Remember the exact result so the
            // next changed-filter boundary recognises our own correction instead of re-queuing it.
            if (_clientEfficiencyObserved.Count > MaxObservedEfficiencyBuffers)
                _clientEfficiencyObserved.Clear();
            _clientEfficiencyObserved[property] = HashEfficiencyBuffer(property);
            return true;
        }

        private void ApplyChangedState()
        {
            if (_cache.Count == 0) return;
            using (Diagnostics.SyncProfiler.Measure("Companies.StateBoundary"))
            {
                _stateRetryScratch.Clear();
                int processed = _stateDirty.Count < MaxStateDirtyPerBoundary
                    ? _stateDirty.Count : MaxStateDirtyPerBoundary;
                for (int i = 0; i < processed; i++)
                {
                    Entity property = _stateDirty[i];
                    _stateDirtyMembers.Remove(property);
                    if (ApplyCachedCompany(property)) _stateRetries.Remove(property);
                    else if (_cache.ContainsKey(property)) _stateRetryScratch.Add(property);
                }
                if (processed > 0) _stateDirty.RemoveRange(0, processed);
                for (int i = 0; i < _stateRetryScratch.Count; i++)
                    RetryStateDirty(_stateRetryScratch[i]);
                _stateRetryScratch.Clear();

                int walked = 0;
                while (walked < MaxStateWalkedPerBoundary && _tenancyOrder.Count > 0)
                {
                    if (_stateCursor >= _tenancyOrder.Count) _stateCursor = 0;
                    Entity property = _tenancyOrder[_stateCursor++];
                    walked++;
                    if (!_cache.ContainsKey(property)) continue;
                    if (ApplyCachedCompany(property)) _stateRetries.Remove(property);
                    else RetryStateDirty(property);
                }
            }
        }

        private bool ApplyCachedCompany(Entity property)
        {
            CachedEntry cached;
            if (!_cache.TryGetValue(property, out cached) || !IsLiveWorkplaceProperty(property))
                return true;
            // The tenant and worker roster are meaningful only against the same property capacity
            // as the host. A dropped level command used to leave dense buildings on their old
            // prefab forever; the next absolute page now completes that level through the game's
            // own BuildingConstructionSystem before tenancy is touched.
            if (!EnsurePropertyPrefabConverged(property, cached.Entry)) return false;
            if (!cached.Entry.HasTenant) return true;

            Entity company = FindTenant(property);
            if (company == Entity.Null || !TenantMatches(company, cached.Entry) ||
                EntityManager.HasComponent<Created>(company)) return false;

            bool complete = ApplyCompany(company, cached.Entry);
            _appliedCompanies++;
            _zoneApplied[(int)ZoneOf(property)]++;
            return complete;
        }

        /// <summary>
        /// Converges a workplace's construction clock and, once the host is complete, its actual
        /// prefab. We deliberately install an already-complete UnderConstruction target rather
        /// than assigning PrefabRef: BuildingConstructionSystem then performs the native
        /// sub-object, area, net and renter-facing side effects of UpdatePrefab.
        /// </summary>
        private bool EnsurePropertyPrefabConverged(Entity property, CompanyStatsEntry entry)
        {
            bool localConstructing = EntityManager.HasComponent<UnderConstruction>(property);
            if (entry.ConstructionSpeed != 0)
            {
                // While the host is building, PrefabName is still the old prefab. The level
                // command owns the target; this absolute channel can safely align only its clock.
                if (localConstructing)
                {
                    UnderConstruction active =
                        EntityManager.GetComponentData<UnderConstruction>(property);
                    if (active.m_Speed != entry.ConstructionSpeed)
                    {
                        active.m_Speed = entry.ConstructionSpeed;
                        EntityManager.SetComponentData(property, active);
                        _alignedPropertyBuildRates++;
                    }
                }
                return true;
            }

            Entity currentPrefab = EntityManager.GetComponentData<PrefabRef>(property).m_Prefab;
            string currentName = _prefabIndex.NameOf(currentPrefab);
            if (!localConstructing &&
                string.Equals(currentName, entry.PrefabName, StringComparison.Ordinal)) return true;

            Entity hostPrefab;
            if (!_prefabIndex.TryResolve(entry.PrefabName,
                    candidate => EntityManager.HasComponent<BuildingPropertyData>(candidate) &&
                                 EntityManager.HasComponent<SpawnableBuildingData>(candidate) &&
                                 !EntityManager.HasComponent<SignatureBuildingData>(candidate),
                    out hostPrefab) || hostPrefab == Entity.Null)
            {
                // Non-growable workplaces share this channel but must never be rewritten by a
                // growable-level fallback. Their ordinary build/object channels remain authority.
                return true;
            }

            if (currentPrefab == hostPrefab && !localConstructing) return true;
            UnderConstruction completion = localConstructing
                ? EntityManager.GetComponentData<UnderConstruction>(property)
                : default(UnderConstruction);
            if (completion.m_NewPrefab == hostPrefab && completion.m_Progress >= 100)
                return false;

            completion.m_NewPrefab = hostPrefab;
            completion.m_Progress = byte.MaxValue;
            if (completion.m_Speed == 0) completion.m_Speed = 1;
            if (localConstructing) EntityManager.SetComponentData(property, completion);
            else EntityManager.AddComponentData(property, completion);
            EntityManager.AddComponent<Updated>(property);
            _correctedPropertyPrefabs++;
            return false;
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
                    if (ApplyCompany(company, cached.Entry)) _stateRetries.Remove(property);
                    else RetryStateDirty(property);
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
            // Office growables can also carry the broader industrial marker.
            if (EntityManager.HasComponent<OfficeProperty>(property)) return SyncZone.Office;
            if (EntityManager.HasComponent<CommercialProperty>(property)) return SyncZone.Commercial;
            if (EntityManager.HasComponent<IndustrialProperty>(property) ||
                EntityManager.HasComponent<StorageProperty>(property) ||
                EntityManager.HasComponent<ExtractorProperty>(property)) return SyncZone.Industrial;
            return SyncZone.None;
        }

        private bool TenantMatches(Entity company, CompanyStatsEntry entry)
        {
            if (!EntityManager.HasComponent<PrefabRef>(company)) return false;
            string local = _prefabIndex.NameOf(
                EntityManager.GetComponentData<PrefabRef>(company).m_Prefab);
            return string.Equals(local, entry.CompanyPrefabName, StringComparison.Ordinal);
        }

        private bool ApplyCompany(Entity company, CompanyStatsEntry entry)
        {
            if (!EntityManager.Exists(company) || EntityManager.HasComponent<Created>(company) ||
                !EntityManager.HasComponent<CompanyData>(company) ||
                !EntityManager.HasComponent<CompanyStatisticData>(company)) return false;

            bool complete = ApplyCompanyIdentity(company, entry);
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

            if (entry.HasServiceAvailable &&
                EntityManager.HasComponent<ServiceAvailable>(company))
            {
                ServiceAvailable service = EntityManager.GetComponentData<ServiceAvailable>(company);
                if (service.m_ServiceAvailable != entry.ServiceAvailable ||
                    service.m_MeanPriority != entry.ServiceMeanPriority)
                {
                    service.m_ServiceAvailable = entry.ServiceAvailable;
                    service.m_MeanPriority = entry.ServiceMeanPriority;
                    EntityManager.SetComponentData(company, service);
                    _correctedFields++;
                }
            }

            if (entry.HasLodgingProvider &&
                EntityManager.HasComponent<LodgingProvider>(company))
            {
                LodgingProvider lodging = EntityManager.GetComponentData<LodgingProvider>(company);
                if (lodging.m_FreeRooms != entry.FreeLodgingRooms ||
                    lodging.m_Price != entry.LodgingPrice)
                {
                    lodging.m_FreeRooms = entry.FreeLodgingRooms;
                    lodging.m_Price = entry.LodgingPrice;
                    EntityManager.SetComponentData(company, lodging);
                    _correctedFields++;
                }
            }

            if (entry.HasWorkProvider && EntityManager.HasComponent<WorkProvider>(company))
            {
                WorkProvider provider = EntityManager.GetComponentData<WorkProvider>(company);
                if (provider.m_MaxWorkers != entry.MaxWorkers)
                {
                    provider.m_MaxWorkers = entry.MaxWorkers;
                    EntityManager.SetComponentData(company, provider);
                    _correctedFields++;
                }
            }

            if (entry.HasTaxPayer && EntityManager.HasComponent<TaxPayer>(company))
            {
                TaxPayer tax = EntityManager.GetComponentData<TaxPayer>(company);
                if (tax.m_UntaxedIncome != entry.UntaxedIncome ||
                    tax.m_AverageTaxRate != entry.AverageTaxRate ||
                    tax.m_AverageTaxPaid != entry.AverageTaxPaid)
                {
                    tax.m_UntaxedIncome = entry.UntaxedIncome;
                    tax.m_AverageTaxRate = entry.AverageTaxRate;
                    tax.m_AverageTaxPaid = entry.AverageTaxPaid;
                    EntityManager.SetComponentData(company, tax);
                    _correctedFields++;
                }
            }

            ApplyResources(company, entry);
            ApplyTradeCosts(company, entry);
            if (!ApplyEmployees(company, entry)) complete = false;
            return complete;
        }

        private bool ApplyCompanyIdentity(Entity company, CompanyStatsEntry entry)
        {
            Entity brand;
            bool resolved = _prefabIndex.TryResolve(entry.BrandPrefabName,
                candidate => EntityManager.HasComponent<BrandData>(candidate), out brand) &&
                brand != Entity.Null;
            if (resolved)
            {
                CompanyData companyData = EntityManager.GetComponentData<CompanyData>(company);
                if (companyData.m_Brand != brand ||
                    companyData.m_RandomSeed.state != entry.CompanyRandomState)
                {
                    companyData.m_Brand = brand;
                    companyData.m_RandomSeed =
                        new Unity.Mathematics.Random(entry.CompanyRandomState);
                    EntityManager.SetComponentData(company, companyData);
                    _correctedCompanyData++;
                }
            }

            string wantedName = entry.CompanyCustomName ?? string.Empty;
            string currentName;
            bool hasCurrent = _nameSystem.TryGetCustomName(company, out currentName) &&
                              !string.IsNullOrEmpty(currentName);
            if ((wantedName.Length == 0 && hasCurrent) ||
                (wantedName.Length > 0 && (!hasCurrent || currentName != wantedName)))
            {
                _nameSystem.SetCustomName(company, wantedName);
                _correctedCompanyData++;
            }
            return resolved;
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

        private void ApplyTradeCosts(Entity company, CompanyStatsEntry entry)
        {
            if (!EntityManager.HasBuffer<TradeCost>(company)) return;
            CompanyStatsTradeCost[] wanted = entry.TradeCosts;
            int wantedCount = wanted == null ? 0 : wanted.Length;
            DynamicBuffer<TradeCost> costs = EntityManager.GetBuffer<TradeCost>(company, true);
            bool changed = costs.Length != wantedCount;
            if (!changed)
            {
                for (int i = 0; i < costs.Length; i++)
                {
                    TradeCost current = costs[i];
                    if (EconomyUtils.GetResourceIndex(current.m_Resource) == wanted[i].Index &&
                        current.m_BuyCost == wanted[i].BuyCost &&
                        current.m_SellCost == wanted[i].SellCost &&
                        current.m_LastTransferRequestTime == wanted[i].LastTransferRequestTime)
                        continue;
                    changed = true;
                    break;
                }
            }
            if (!changed) return;

            costs = EntityManager.GetBuffer<TradeCost>(company);
            costs.Clear();
            for (int i = 0; i < wantedCount; i++)
            {
                costs.Add(new TradeCost
                {
                    m_Resource = EconomyUtils.GetResource(wanted[i].Index),
                    m_BuyCost = wanted[i].BuyCost,
                    m_SellCost = wanted[i].SellCost,
                    m_LastTransferRequestTime = wanted[i].LastTransferRequestTime,
                });
            }
            _correctedTradeCosts++;
        }

        /// <summary>
        /// Rebuild the game's real two-sided employment graph. Each host id has already been
        /// associated with a local resident by occupancy; setting Worker on that citizen lets the
        /// native travel simulation send that same pedestrian to work. An incomplete host roster
        /// is additive only, protecting commuters and tourists for which no shared identity exists.
        /// </summary>
        private bool ApplyEmployees(Entity company, CompanyStatsEntry entry)
        {
            CompanyStatsEmployee[] wanted = entry.Employees;
            int wantedCount = wanted == null ? 0 : wanted.Length;
            if (!EntityManager.HasBuffer<Employee>(company)) return wantedCount == 0;

            _resolvedEmployeeScratch.Clear();
            _desiredEmployeeEntities.Clear();
            bool allResolved = true;
            for (int i = 0; i < wantedCount; i++)
            {
                Entity citizen;
                if (_occupancy == null ||
                    !_occupancy.TryResolveCompanyCitizen(wanted[i].CitizenId, out citizen) ||
                    citizen == Entity.Null || !EntityManager.Exists(citizen) ||
                    !EntityManager.HasComponent<Citizen>(citizen) ||
                    EntityManager.HasComponent<Deleted>(citizen))
                {
                    allResolved = false;
                    continue;
                }
                if (!_desiredEmployeeEntities.Add(citizen))
                {
                    allResolved = false;
                    continue;
                }
                _resolvedEmployeeScratch.Add(new ResolvedEmployee
                {
                    Citizen = citizen,
                    State = wanted[i],
                });
            }

            // Move a desired resident out of whichever local workplace had claimed them first.
            // Buffer mutations finish before Worker is added/set, so no live DynamicBuffer handle
            // crosses a structural component change.
            for (int i = 0; i < _resolvedEmployeeScratch.Count; i++)
            {
                Entity citizen = _resolvedEmployeeScratch[i].Citizen;
                if (!EntityManager.HasComponent<Worker>(citizen)) continue;
                Entity previous = EntityManager.GetComponentData<Worker>(citizen).m_Workplace;
                if (previous == Entity.Null || previous == company) continue;
                RemoveEmployeeReference(previous, citizen);
            }

            bool absolute = entry.EmployeeRosterComplete && allResolved;
            bool changed = ReconcileEmployeeBuffer(company, absolute);

            for (int i = 0; i < _resolvedEmployeeScratch.Count; i++)
            {
                ResolvedEmployee employee = _resolvedEmployeeScratch[i];
                if (SetDesiredWorker(company, employee.Citizen, employee.State)) changed = true;
                CancelJobSearch(employee.Citizen);
            }

            // Only a complete, fully resolved roster authorizes removals. These citizens stay real
            // residents; removing Worker merely makes the native job finder consider them again.
            if (absolute)
            {
                for (int i = 0; i < _employeeRemovalScratch.Count; i++)
                {
                    Entity citizen = _employeeRemovalScratch[i];
                    if (!EntityManager.Exists(citizen) ||
                        !EntityManager.HasComponent<Worker>(citizen)) continue;
                    Worker worker = EntityManager.GetComponentData<Worker>(citizen);
                    if (worker.m_Workplace != company) continue;
                    EntityManager.RemoveComponent<Worker>(citizen);
                    changed = true;
                }
            }

            RefreshFreeWorkplaces(company);
            if (changed)
            {
                _correctedEmployees++;
                // Remember the buffer we just wrote, so the changed-Employee boundary does not
                // read our own write back as a local change on its next pass.
                if (_clientEmployeeObserved.Count > MaxObservedEmployeeBuffers)
                    _clientEmployeeObserved.Clear();
                _clientEmployeeObserved[company] = HashEmployeeBuffer(company);
            }
            return allResolved;
        }

        private bool ReconcileEmployeeBuffer(Entity company, bool absolute)
        {
            _employeeRemovalScratch.Clear();
            DynamicBuffer<Employee> employees = EntityManager.GetBuffer<Employee>(company);
            bool changed = false;
            if (absolute)
            {
                for (int i = 0; i < employees.Length; i++)
                {
                    Entity citizen = employees[i].m_Worker;
                    if (_desiredEmployeeEntities.Contains(citizen) ||
                        _employeeRemovalScratch.Contains(citizen)) continue;
                    _employeeRemovalScratch.Add(citizen);
                }

                bool same = employees.Length == _resolvedEmployeeScratch.Count;
                if (same)
                {
                    for (int i = 0; i < employees.Length; i++)
                    {
                        if (employees[i].m_Worker == _resolvedEmployeeScratch[i].Citizen &&
                            employees[i].m_Level == _resolvedEmployeeScratch[i].State.Level)
                            continue;
                        same = false;
                        break;
                    }
                }
                if (same) return false;

                employees.Clear();
                for (int i = 0; i < _resolvedEmployeeScratch.Count; i++)
                {
                    employees.Add(new Employee
                    {
                        m_Worker = _resolvedEmployeeScratch[i].Citizen,
                        m_Level = _resolvedEmployeeScratch[i].State.Level,
                    });
                }
                return true;
            }

            // Partial roster: update/add only the residents explicitly named by the host.
            for (int i = 0; i < _resolvedEmployeeScratch.Count; i++)
            {
                ResolvedEmployee wanted = _resolvedEmployeeScratch[i];
                int first = -1;
                for (int e = 0; e < employees.Length; e++)
                {
                    if (employees[e].m_Worker != wanted.Citizen) continue;
                    first = e;
                    break;
                }
                if (first < 0)
                {
                    employees.Add(new Employee
                    {
                        m_Worker = wanted.Citizen,
                        m_Level = wanted.State.Level,
                    });
                    changed = true;
                    continue;
                }
                if (employees[first].m_Level != wanted.State.Level)
                {
                    employees[first] = new Employee
                    {
                        m_Worker = wanted.Citizen,
                        m_Level = wanted.State.Level,
                    };
                    changed = true;
                }
                for (int e = employees.Length - 1; e > first; e--)
                {
                    if (employees[e].m_Worker != wanted.Citizen) continue;
                    employees.RemoveAt(e);
                    changed = true;
                }
            }
            return changed;
        }

        private void RemoveEmployeeReference(Entity workplace, Entity citizen)
        {
            if (workplace == Entity.Null || !EntityManager.Exists(workplace) ||
                !EntityManager.HasBuffer<Employee>(workplace)) return;
            DynamicBuffer<Employee> employees = EntityManager.GetBuffer<Employee>(workplace);
            bool changed = false;
            for (int i = employees.Length - 1; i >= 0; i--)
            {
                if (employees[i].m_Worker != citizen) continue;
                employees.RemoveAt(i);
                changed = true;
            }
            if (changed) RefreshFreeWorkplaces(workplace);
        }

        private bool SetDesiredWorker(Entity company, Entity citizen,
            CompanyStatsEmployee wanted)
        {
            var worker = new Worker
            {
                m_Workplace = company,
                m_Level = wanted.Level,
                m_LastCommuteTime = wanted.LastCommuteTime,
                m_Shift = (Workshift)wanted.Shift,
            };
            if (!EntityManager.HasComponent<Worker>(citizen))
            {
                EntityManager.AddComponentData(citizen, worker);
                return true;
            }

            Worker current = EntityManager.GetComponentData<Worker>(citizen);
            if (current.m_Workplace == worker.m_Workplace && current.m_Level == worker.m_Level &&
                current.m_LastCommuteTime == worker.m_LastCommuteTime &&
                current.m_Shift == worker.m_Shift) return false;
            EntityManager.SetComponentData(citizen, worker);
            return true;
        }

        private void CancelJobSearch(Entity citizen)
        {
            if (!EntityManager.HasComponent<HasJobSeeker>(citizen)) return;
            HasJobSeeker state = EntityManager.GetComponentData<HasJobSeeker>(citizen);
            Entity seeker = state.m_Seeker;
            if (seeker != Entity.Null && EntityManager.Exists(seeker) &&
                !EntityManager.HasComponent<Deleted>(seeker))
                EntityManager.AddComponent<Deleted>(seeker);
            if (EntityManager.IsComponentEnabled<HasJobSeeker>(citizen))
                EntityManager.SetComponentEnabled<HasJobSeeker>(citizen, false);
        }

        private void RefreshFreeWorkplaces(Entity company)
        {
            if (company == Entity.Null || !EntityManager.Exists(company) ||
                !EntityManager.HasBuffer<Employee>(company) ||
                !EntityManager.HasComponent<FreeWorkplaces>(company) ||
                !EntityManager.HasComponent<WorkProvider>(company) ||
                !EntityManager.HasComponent<PrefabRef>(company)) return;

            Entity companyPrefab = EntityManager.GetComponentData<PrefabRef>(company).m_Prefab;
            if (companyPrefab == Entity.Null || !EntityManager.Exists(companyPrefab) ||
                !EntityManager.HasComponent<WorkplaceData>(companyPrefab)) return;

            int level = 1;
            if (EntityManager.HasComponent<PropertyRenter>(company))
            {
                Entity property = EntityManager.GetComponentData<PropertyRenter>(company).m_Property;
                if (property != Entity.Null && EntityManager.Exists(property) &&
                    EntityManager.HasComponent<PrefabRef>(property))
                {
                    Entity propertyPrefab = EntityManager.GetComponentData<PrefabRef>(property).m_Prefab;
                    if (propertyPrefab != Entity.Null && EntityManager.Exists(propertyPrefab) &&
                        EntityManager.HasComponent<SpawnableBuildingData>(propertyPrefab))
                        level = EntityManager.GetComponentData<SpawnableBuildingData>(propertyPrefab)
                            .m_Level;
                }
            }

            WorkProvider provider = EntityManager.GetComponentData<WorkProvider>(company);
            WorkplaceData workplace = EntityManager.GetComponentData<WorkplaceData>(companyPrefab);
            DynamicBuffer<Employee> employees = EntityManager.GetBuffer<Employee>(company);
            FreeWorkplaces free = EntityManager.GetComponentData<FreeWorkplaces>(company);
            free.Refresh(employees, provider.m_MaxWorkers, workplace.m_Complexity, level);
            EntityManager.SetComponentData(company, free);
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
            if (!EnsurePropertyPrefabConverged(property, cached.Entry))
            {
                _deferredActions++;
                return;
            }
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
