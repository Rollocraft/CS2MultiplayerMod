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

        /// <summary>Resolves arrived pages. Read-only against ECS; writes stay in OnUpdate.</summary>
        internal void PumpIncoming()
        {
            MultiplayerService service = Mod.Service;
            if (service == null || !service.SimulationSyncReady) return;
            if (service.Session.Role == SessionRole.Host)
            {
                _droppedPages += _propertyState.DropIncoming();
                return;
            }

            long now = service.NowMs;
            bool retryDue = _propertyState.RetryDue(now);
            if (_incoming.IsEmpty && !retryDue) return;

            using (var scope = new PropertySearchScope(_objectSearch))
            {
                DrainIncoming(now, scope.Batch, scope.Candidates, MaxPumpPages);
                if (!retryDue) return;
                RetryPending(now, scope.Batch, scope.Candidates);
                _propertyState.NextPendingPumpMs = now + ResolveRetryMs;
            }
        }

        private void DrainIncoming(long now, ObjectSearch.Batch search,
            NativeList<Entity> candidates, int maxPages)
        {
            _propertyState.PumpPages(maxPages, snapshot =>
            {
                _receivedPages++;
                _propertyState.NotePage(snapshot.SweepId, snapshot.PageIndex);
                for (int i = 0; i < snapshot.Entries.Count; i++)
                    ResolveOrPend(snapshot.Entries[i], snapshot.SweepId, now, search, candidates);
                if (snapshot.EndOfSweep)
                {
                    if (_propertyState.CompletesSweep(snapshot.SweepId, snapshot.PageIndex))
                        PruneCacheAfterCompleteSweep(snapshot.SweepId);
                    _propertyState.ResetSweep();
                }
            });
        }

        private void ResolveOrPend(CompanyStatsEntry entry, uint sweepId, long now,
            ObjectSearch.Batch search, NativeList<Entity> candidates)
        {
            Entity property = ResolveProperty(entry, search, candidates, out bool ambiguous);
            if (property != Entity.Null)
            {
                Cache(property, entry, sweepId);
                _pending.Remove(entry.Identity);
                _resolved++;
                return;
            }
            if (ambiguous) _ambiguous++;
            else _unresolved++;

            if (_pending.TryGetValue(entry.Identity, out PendingEntry pending))
            {
                pending.Entry = entry;
                pending.SweepId = sweepId;
            }
            else _propertyState.Hold(entry.Identity, new PendingEntry { Entry = entry }, sweepId, now,
                ResolveTimeoutMs);
        }

        private void RetryPending(long now, ObjectSearch.Batch search, NativeList<Entity> candidates)
        {
            _propertyState.RetryPending(now, MaxPendingRetriesPerUpdate,
                value =>
                {
                    Entity property = ResolveProperty(value.Entry, search, candidates, out bool ambiguous);
                    if (property == Entity.Null) return false;
                    Cache(property, value.Entry, value.SweepId);
                    _resolved++;
                    return true;
                }, () => _expired++);
        }

        /// <summary>
        /// Position is identity; the prefab only breaks a tie, because a building that levels swaps its
        /// prefab at a different moment on each peer. A tie stays unresolved.
        /// </summary>
        private Entity ResolveProperty(CompanyStatsEntry entry, ObjectSearch.Batch search,
            NativeList<Entity> candidates, out bool ambiguous)
        {
            ambiguous = false;
            _prefabIndex.TryResolve(entry.PrefabName,
                candidate => EntityManager.Exists(candidate) &&
                    EntityManager.HasComponent<BuildingPropertyData>(candidate), out Entity prefab);
            PropertyResolution result = PropertyEntityResolver.Resolve(EntityManager, search,
                candidates, new float3(entry.AnchorX, entry.AnchorY, entry.AnchorZ),
                AnchorSearchRadius, AnchorMatchDistance, AmbiguousDistanceEpsilon, prefab,
                IsLiveWorkplaceProperty, PropertyPrefabPreference.ExactFirst);
            ambiguous = result.Ambiguous;
            return result.Entity;
        }

        private void Cache(Entity property, CompanyStatsEntry entry, uint sweepId)
        {
            if (!_cache.TryGetValue(property, out CachedEntry cached))
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
            // Applied at the 16-frame boundary; the statistics rotation is 2,048 frames.
            MarkStateDirty(property);
            if (entry.HasEfficiency) MarkEfficiencyDirty(property);
            if (tenancyChanged) MarkDirty(property);
        }

        private void MarkDirty(Entity property) =>
            // Shedding the oldest is safe: the entry is still cached for the rolling walk.
            BoundedUniquePropertyList.Enqueue(_dirty, _dirtyMembers, property, MaxDirtyProperties);

        /// <summary>New information clears the retry budget.</summary>
        private void MarkStateDirty(Entity property)
        {
            _stateRetries.Remove(property);
            EnqueueStateDirty(property);
        }

        /// <summary>
        /// Re-arms an incomplete apply for a few passes only: an unbound employee does not bind by asking
        /// again, and the page that binds it marks the building dirty itself.
        /// </summary>
        private void RetryStateDirty(Entity property)
        {
            _stateRetries.TryGetValue(property, out int attempts);
            if (attempts >= MaxStateRetries) return;
            _stateRetries[property] = attempts + 1;
            EnqueueStateDirty(property);
        }

        private void EnqueueStateDirty(Entity property)
        {
            BoundedUniquePropertyList.Enqueue(
                _stateDirty, _stateDirtyMembers, property, MaxDirtyProperties);
        }

        private void MarkEfficiencyDirty(Entity property)
        {
            BoundedUniquePropertyList.Enqueue(
                _efficiencyDirty, _efficiencyDirtyMembers, property, MaxDirtyProperties);
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

        /// <summary>After native job matching, so names, figures and workers settle within 16 frames.</summary>
        internal void ApplyClientStateBoundary()
        {
            MultiplayerService service = Mod.Service;
            if (service == null || !service.SimulationSyncReady ||
                service.Session.Role != SessionRole.Client) return;

            PumpIncoming();
            ApplyTenancy();
            ApplyChangedEfficiencies();
            ApplyChangedState();
        }

        /// <summary>Client only; the host already signals efficiency changes from the state boundary.</summary>
        internal bool WantsProductionBoundary
        {
            get
            {
                MultiplayerService service = Mod.Service;
                return service != null && service.SimulationSyncReady &&
                       service.Session.Role == SessionRole.Client && _cache.Count != 0;
            }
        }

        internal bool WantsExtractorProduceBoundary
        {
            get
            {
                MultiplayerService service = Mod.Service;
                return service != null && service.SimulationSyncReady;
            }
        }

        /// <summary>
        /// Right after a native production pass: the panel recalculates production from these factors
        /// every UI frame, so the repair must land in the same frame.
        /// </summary>
        internal void ApplyProductionBoundary(NativeArray<Entity> properties)
        {
            if (!WantsProductionBoundary) return;
            if (properties.IsCreated) CaptureEfficiencyChanges(properties);
            ApplyChangedEfficiencies();
        }

        /// <summary>Puts the host's extraction figure back; only that field.</summary>
        private void ApplyExtractorProduce(Entity company, Entity property)
        {
            if (!_cache.TryGetValue(property, out CachedEntry cached) || !cached.Entry.HasTenant) return;
            if (!TenantMatches(company, cached.Entry)) return;
            CompanyStatisticData data =
                EntityManager.GetComponentData<CompanyStatisticData>(company);
            if (data.m_LastUpdateProduce == cached.Entry.LastUpdateProduce) return;
            data.m_LastUpdateProduce = cached.Entry.LastUpdateProduce;
            EntityManager.SetComponentData(company, data);
            _correctedExtractorProduce++;
        }

        /// <summary>Efficiency has its own cheap queue, so one factor moving does not re-apply a company.</summary>
        private void ApplyChangedEfficiencies()
        {
            int processed = _efficiencyDirty.Count < MaxEfficiencyDirtyPerBoundary
                ? _efficiencyDirty.Count : MaxEfficiencyDirtyPerBoundary;
            for (int i = 0; i < processed; i++)
            {
                Entity property = _efficiencyDirty[i];
                _efficiencyDirtyMembers.Remove(property);
                if (!_cache.TryGetValue(property, out CachedEntry cached) ||
                    !IsLiveWorkplaceProperty(property) || !cached.Entry.HasEfficiency) continue;
                ApplyPropertyEfficiency(property, cached.Entry);
            }
            if (processed > 0) _efficiencyDirty.RemoveRange(0, processed);
        }

        /// <summary>
        /// Rebuilds the native efficiency buffer, which the panel multiplies into
        /// EconomyUtils.GetCompanyProductionPerDay for processing industry and offices.
        /// </summary>
        private bool ApplyPropertyEfficiency(Entity property, CompanyStatsEntry entry)
        {
            if (!entry.HasEfficiency) return true;
            if (!EntityManager.HasBuffer<Efficiency>(property)) return false;

            CompanyStatsEfficiency[] wanted = entry.Efficiencies;
            int wantedCount = wanted == null ? 0 : wanted.Length;
            var local = new BufferEdit<Efficiency>(EntityManager, property);
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

            // Remember our write so the next changed-filter boundary does not re-queue it.
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
                _stateAppliedThisBoundary.Clear();
                int processed = _stateDirty.Count < MaxStateDirtyPerBoundary
                    ? _stateDirty.Count : MaxStateDirtyPerBoundary;
                for (int i = 0; i < processed; i++)
                {
                    Entity property = _stateDirty[i];
                    _stateDirtyMembers.Remove(property);
                    _stateAppliedThisBoundary.Add(property);
                    if (ApplyCachedCompany(property)) _stateRetries.Remove(property);
                    else if (_cache.ContainsKey(property)) _stateRetryScratch.Add(property);
                }
                if (processed > 0) _stateDirty.RemoveRange(0, processed);
                for (int i = 0; i < _stateRetryScratch.Count; i++)
                    RetryStateDirty(_stateRetryScratch[i]);
                _stateRetryScratch.Clear();

                // Only the fallback walk is speed-normalized.
                if (!_stateScanCadence.TryRun(_simulationSystem.selectedSpeed)) return;

                // A small cache must not wrap and apply the same company dozens of times.
                int walkLimit = Math.Min(MaxStateWalkedPerBoundary, _tenancyOrder.Count);
                int walked = 0;
                while (walked < walkLimit && _tenancyOrder.Count > 0)
                {
                    if (_stateCursor >= _tenancyOrder.Count) _stateCursor = 0;
                    Entity property = _tenancyOrder[_stateCursor++];
                    walked++;
                    if (!_stateAppliedThisBoundary.Add(property) ||
                        !_cache.ContainsKey(property)) continue;
                    if (ApplyCachedCompany(property)) _stateRetries.Remove(property);
                    else RetryStateDirty(property);
                }
            }
        }

        private bool ApplyCachedCompany(Entity property)
        {
            if (!_cache.TryGetValue(property, out CachedEntry cached) || !IsLiveWorkplaceProperty(property))
                return true;
            // Tenancy only means something against the host's property capacity: converge the prefab first.
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
        /// Converges construction and, once complete, the prefab through an already-complete
        /// UnderConstruction target, so BuildingConstructionSystem runs UpdatePrefab's side effects.
        /// </summary>
        private bool EnsurePropertyPrefabConverged(Entity property, CompanyStatsEntry entry)
        {
            bool localConstructing = EntityManager.HasComponent<UnderConstruction>(property);
            if (entry.ConstructionSpeed != 0)
            {
                // While the host builds, only the clock is aligned; the level command owns the target.
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

            if (!_prefabIndex.TryResolve(entry.PrefabName,
                    candidate => EntityManager.HasComponent<BuildingPropertyData>(candidate) &&
                                 EntityManager.HasComponent<SpawnableBuildingData>(candidate) &&
                                 !EntityManager.HasComponent<SignatureBuildingData>(candidate),
                    out Entity hostPrefab) || hostPrefab == Entity.Null)
            {
                // Non-growable workplaces are owned by the build/object channels.
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
        /// Uncapped over its partition: a ceiling would leave part of it showing the local values.
        /// </summary>
        private void ApplyFigures(uint updateFrame)
        {
            if (_cache.Count == 0) return;
            _companies.SetSharedComponentFilter(new UpdateFrame(updateFrame));
            NativeArray<Entity> companies = default(NativeArray<Entity>);
            try
            {
                companies = _companies.ToEntityArray(Allocator.Temp);

                // Split by zone so each is timed separately.
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
                    if (!_cache.TryGetValue(property, out CachedEntry cached)) continue;
                    if (!cached.Entry.HasTenant) continue;
                    // Tenancy resolves a mismatched business; it gets no figures.
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

            // Never fabricate a rating the sender did not have.
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
            bool resolved = _prefabIndex.TryResolve(entry.BrandPrefabName,
                candidate => EntityManager.HasComponent<BrandData>(candidate), out Entity brand) &&
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
            bool hasCurrent = _nameSystem.TryGetCustomName(company, out string currentName) &&
                              !string.IsNullOrEmpty(currentName);
            if ((wantedName.Length == 0 && hasCurrent) ||
                (wantedName.Length > 0 && (!hasCurrent || currentName != wantedName)))
            {
                _nameSystem.SetCustomName(company, wantedName);
                _correctedCompanyData++;
            }
            return resolved;
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
        /// Sets Worker on the local residents occupancy mapped to host ids, so native commuting follows.
        /// An incomplete roster is additive only.
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
                if (_occupancy == null ||
                    !_occupancy.TryResolveCompanyCitizen(wanted[i].CitizenId, out Entity citizen) ||
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

            // Buffer edits finish before Worker changes, so no buffer handle crosses a structural change.
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

            // Only a complete roster removes; removing Worker lets the job finder consider them again.
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
                if (_clientEmployeeObserved.Count > MaxObservedEmployeeBuffers)
                    _clientEmployeeObserved.Clear();
                _clientEmployeeObserved[company] = HashEmployeeBuffer(company);
            }
            return allResolved;
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
            DynamicBuffer<Employee> employees = EntityManager.GetBuffer<Employee>(company, true);
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

        /// <summary>Dirty buildings first, then a small rolling repair window.</summary>
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
            int dirtyLimit = Math.Min(MaxTenancyDirtyPerBoundary, _dirty.Count);
            int processed = 0;
            while (processed < dirtyLimit &&
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

            if (!_tenancyScanCadence.TryRun(_simulationSystem.selectedSpeed)) return;
            int walkLimit = Math.Min(MaxTenancyWalkedPerUpdate, _tenancyOrder.Count);
            int walked = 0;
            while (walked < walkLimit && _tenancyOrder.Count > 0 &&
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
            if (!_cache.TryGetValue(property, out CachedEntry cached)) return;
            if (!IsLiveWorkplaceProperty(property)) return;
            if (!EnsurePropertyPrefabConverged(property, cached.Entry))
            {
                _deferredActions++;
                return;
            }
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
                // Close now; the next pass opens the right one, keeping the two structural changes apart.
                if (retired >= MaxCompaniesRetiredPerUpdate) { _deferredActions++; return; }
                if (RetireCompany(local, property)) retired++;
                return;
            }

            if (created >= MaxCompaniesCreatedPerUpdate) { _deferredActions++; return; }
            if (CreateCompany(property, entry)) created++;
        }

        /// <summary>Closes through the game's emigration path so native systems unwind the renter link.</summary>
        private bool RetireCompany(Entity company, Entity property)
        {
            if (!EntityManager.Exists(company) ||
                EntityManager.HasComponent<Deleted>(company)) return false;
            if (!EntityManager.HasComponent<global::Game.Agents.MovingAway>(company))
                EntityManager.AddComponentData(company, default(global::Game.Agents.MovingAway));
            // Whitelisted so the lifecycle boundary does not cancel our own request.
            AuthorizeMoveAway(company);
            BeginSettling(property);
            _retiredCompanies++;
            _zoneClosed[(int)ZoneOf(property)]++;
            return true;
        }

        /// <summary>Opens a business as the game's spawner does, moving in through the rent-action queue.</summary>
        private bool CreateCompany(Entity property, CompanyStatsEntry entry)
        {
            if (!_prefabIndex.TryResolve(entry.CompanyPrefabName,
                    candidate => EntityManager.HasComponent<ArchetypeData>(candidate),
                    out Entity prefab) || prefab == Entity.Null)
                return false;
            if (!EntityManager.HasComponent<ArchetypeData>(prefab)) return false;

            EntityArchetype archetype =
                EntityManager.GetComponentData<ArchetypeData>(prefab).m_Archetype;
            if (archetype == default(EntityArchetype)) return false;

            // The native transaction does the move-in; without it the company would have nowhere to live.
            if (_propertyProcessing == null || !_propertyProcessing.Enabled) return false;

            Entity company = EntityManager.CreateEntity(archetype);
            EntityManager.SetComponentData(company, new PrefabRef { m_Prefab = prefab });

            NativeQueue<RentAction> queue =
                _propertyProcessing.GetRentActionQueue(out Unity.Jobs.JobHandle dependencies);
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
            if (!_settling.TryGetValue(property, out uint until)) return false;
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
