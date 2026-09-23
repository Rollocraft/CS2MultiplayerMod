using System.Collections.Generic;
using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Sync.Commands;
using Game.Agents;
using Game.Buildings;
using Game.Citizens;
using Game.Companies;
using Game.Economy;
using Game.Prefabs;
using Game.Simulation;
using Unity.Collections;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    public partial class CompanyStatsSyncSystem
    {
        /// <summary>Called once per CityState snapshot on the host.</summary>
        internal bool Capture(NetworkWriter writer)
        {
            if (writer == null) return false;
            MultiplayerService service = Mod.Service;
            if (service == null || !service.SimulationSyncReady ||
                service.Session.Role != SessionRole.Host) return false;

            if (_hostSweepEntities == null && !BeginHostSweep()) return WriteEmptySweep(writer);
            if (_captureCursor < 0 || _captureCursor >= _hostSweepEntities.Length)
            {
                _hostSweepEntities = null;
                _captureCursor = 0;
                AdvanceHostSweep();
                if (!BeginHostSweep()) return WriteEmptySweep(writer);
            }

            var snapshot = new CompanyStatsSnapshot
            {
                SweepId = _captureSweepId,
                PageIndex = _capturePageIndex,
            };
            var identities = new HashSet<PropertyIdentity>();
            int estimatedBytes = AddPriorityEntries(snapshot, identities, 9);

            int index = _captureCursor;
            while (index < _hostSweepEntities.Length &&
                   snapshot.Entries.Count < CompanyStatsSnapshot.MaxEntries)
            {
                if (!TryCaptureEntry(_hostSweepEntities[index], out CompanyStatsEntry entry))
                {
                    _captureSkips++;
                    index++;
                    continue;
                }
                if (identities.Contains(entry.Identity))
                {
                    index++;
                    continue;
                }

                int entryBytes = CompanyStatsSnapshot.EstimateEncodedBytes(entry);
                if (estimatedBytes + entryBytes > PageByteBudget)
                {
                    // Validation caps a roster so one entry always fits an empty page.
                    if (snapshot.Entries.Count > 0) break;
                    _captureSkips++;
                    index++;
                    continue;
                }

                identities.Add(entry.Identity);
                snapshot.Entries.Add(entry);
                estimatedBytes += entryBytes;
                index++;
            }

            snapshot.EndOfSweep = index >= _hostSweepEntities.Length ||
                                  _capturePageIndex + 1 >= CompanyStatsSnapshot.MaxPagesPerSweep;
            if (snapshot.Entries.Count == 0 && !snapshot.EndOfSweep) return false;

            byte[] encoded;
            try
            {
                encoded = snapshot.Encode();
            }
            catch (ProtocolException)
            {
                // Never consume the baseline suffix for a page that could not be encoded.
                _captureSkips++;
                return false;
            }

            if (snapshot.EndOfSweep)
            {
                _hostSweepEntities = null;
                _captureCursor = 0;
                AdvanceHostSweep();
            }
            else
            {
                _captureCursor = index;
                _capturePageIndex++;
            }

            int before = writer.Length;
            writer.WriteBytes(encoded, 0, encoded.Length);
            _sentBytes += writer.Length - before;
            _sentPages++;
            _sentEntries += snapshot.Entries.Count;
            return true;
        }

        private bool BeginHostSweep()
        {
            NativeArray<Entity> properties = _properties.ToEntityArray(Allocator.Temp);
            try
            {
                if (properties.Length == 0) return false;
                _hostSweepEntities = new Entity[properties.Length];
                for (int i = 0; i < properties.Length; i++) _hostSweepEntities[i] = properties[i];
                return true;
            }
            finally { properties.Dispose(); }
        }

        /// <summary>A city with no workplace still closes its sweep, so clients prune their cache.</summary>
        private bool WriteEmptySweep(NetworkWriter writer)
        {
            var empty = new CompanyStatsSnapshot
            {
                SweepId = _captureSweepId,
                PageIndex = 0,
                EndOfSweep = true,
            };
            int before = writer.Length;
            empty.Write(writer);
            _sentBytes += writer.Length - before;
            _sentPages++;
            AdvanceHostSweep();
            return true;
        }

        private int AddPriorityEntries(CompanyStatsSnapshot snapshot,
            HashSet<PropertyIdentity> identities, int estimatedBytes)
        {
            int added = 0;
            while (added < PriorityEntriesPerPage &&
                   snapshot.Entries.Count < CompanyStatsSnapshot.MaxEntries &&
                   _priorityOrder.Count > 0)
            {
                if (!_priorityOrder.TryDequeue(out PropertyIdentity identity)) break;
                if (!_priority.TryGetValue(identity, out Entity property)) continue;
                // Recapture at send time so a stale copy cannot lose to a fresher baseline entry.
                if (!TryCaptureEntry(property, out CompanyStatsEntry entry))
                {
                    _priority.Remove(identity);
                    continue;
                }
                if (!identities.Add(entry.Identity))
                {
                    _priority.Remove(identity);
                    continue;
                }
                int entryBytes = CompanyStatsSnapshot.EstimateEncodedBytes(entry);
                // Leave room for the baseline to advance; the first entry that does not fit waits for the next page.
                if (estimatedBytes + entryBytes > PriorityByteBudget)
                {
                    identities.Remove(entry.Identity);
                    _priorityOrder.Enqueue(identity);
                    break;
                }
                _priority.Remove(identity);
                snapshot.Entries.Add(entry);
                estimatedBytes += entryBytes;
                added++;
            }
            return estimatedBytes;
        }

        /// <summary>
        /// Rolling change detector: at most <see cref="MaxPropertiesObservedPerUpdate"/> buildings per
        /// update, resuming where it stopped. A partition is initialized only after one full lap.
        /// </summary>
        private void ScanHostChanges(int partition)
        {
            _properties.SetSharedComponentFilter(new UpdateFrame((uint)partition));
            NativeArray<Entity> properties = default(NativeArray<Entity>);
            try
            {
                properties = _properties.ToEntityArray(Allocator.Temp);
                bool initialized = _hostPartitionInitialized[partition];
                int cursor = _hostPartitionCursor[partition];
                bool wrapped = false;
                if (cursor >= properties.Length) { cursor = 0; wrapped = true; }
                int examine = properties.Length < MaxPropertiesObservedPerUpdate
                    ? properties.Length : MaxPropertiesObservedPerUpdate;
                for (int i = 0; i < examine; i++)
                {
                    if (cursor >= properties.Length) { cursor = 0; wrapped = true; }
                    Entity property = properties[cursor++];
                    if (!TryCaptureEntry(property, out CompanyStatsEntry entry)) continue;
                    int hash = Hash(entry);
                    if (!_hostObserved.TryGetValue(property, out int observed))
                    {
                        _hostObserved[property] = hash;
                        if (initialized) Prioritize(property, entry.Identity);
                    }
                    else if (observed != hash)
                    {
                        _hostObserved[property] = hash;
                        if (initialized) Prioritize(property, entry.Identity);
                    }
                }
                if (cursor >= properties.Length) { cursor = 0; wrapped = true; }
                _hostPartitionCursor[partition] = cursor;
                if (wrapped) _hostPartitionInitialized[partition] = true;
            }
            finally
            {
                if (properties.IsCreated) properties.Dispose();
                _properties.ResetFilter();
            }
        }

        private void Prioritize(Entity property, PropertyIdentity identity)
        {
            if (_propertyState.Prioritize(identity, property, MaxPriorityEntries, out int dropped)) _priorityChanges++;
            _priorityDrops += dropped;
        }

        /// <summary>Fast path for move-in/out via RentersUpdated, instead of the 2,048-frame rotation.</summary>
        internal void CaptureTenancyChanges()
        {
            MultiplayerService service = Mod.Service;
            if (service == null || !service.SimulationSyncReady ||
                _renterUpdates.IsEmptyIgnoreFilter) return;

            NativeArray<RentersUpdated> updates = default(NativeArray<RentersUpdated>);
            try
            {
                updates = _renterUpdates.ToComponentDataArray<RentersUpdated>(Allocator.Temp);
                for (int i = 0; i < updates.Length; i++)
                {
                    Entity property = updates[i].m_Property;
                    if (!IsLiveWorkplaceProperty(property)) continue;
                    if (service.Session.Role == SessionRole.Host)
                    {
                        if (!TryGetWorkplaceIdentity(property, out PropertyIdentity identity)) continue;
                        // Recaptured at send time: on the event frame a new tenant can still look vacant.
                        Prioritize(property, identity);
                        _hostLifecycleSignals++;
                    }
                    else if (_cache.ContainsKey(property))
                    {
                        MarkDirty(property);
                        MarkStateDirty(property);
                        _clientLifecycleRepairs++;
                    }
                }
            }
            finally
            {
                if (updates.IsCreated) updates.Dispose();
            }
        }

        /// <summary>Employee is a buffer, so hiring emits no renter event. Runs after FindJobSystem.</summary>
        internal void CaptureEmployeeChanges(NativeArray<Entity> companies)
        {
            MultiplayerService service = Mod.Service;
            if (service == null || !service.SimulationSyncReady) return;

            for (int i = 0; i < companies.Length; i++)
            {
                Entity company = companies[i];
                if (company == Entity.Null || !EntityManager.Exists(company) ||
                    !EntityManager.HasComponent<CompanyData>(company) ||
                    !EntityManager.HasComponent<PropertyRenter>(company) ||
                    EntityManager.HasComponent<global::Game.Common.Created>(company) ||
                    EntityManager.HasComponent<global::Game.Common.Deleted>(company) ||
                    EntityManager.HasComponent<global::Game.Tools.Temp>(company)) continue;
                Entity property = EntityManager.GetComponentData<PropertyRenter>(company).m_Property;
                if (!IsLiveWorkplaceProperty(property)) continue;

                if (service.Session.Role == SessionRole.Host)
                {
                    int hash = HashEmployeeBuffer(company);
                    if (_hostEmployeeObserved.TryGetValue(company, out int previous) &&
                        previous == hash) continue;
                    _hostEmployeeObserved[company] = hash;
                    if (!TryGetWorkplaceIdentity(property, out PropertyIdentity identity)) continue;
                    Prioritize(property, identity);
                    _hostLifecycleSignals++;
                }
                else if (_cache.ContainsKey(property))
                {
                    // The changed filter also fires on our own writes, at chunk granularity; compare with what we
                    // last left the buffer at.
                    int hash = HashEmployeeBuffer(company);
                    if (_clientEmployeeObserved.TryGetValue(company, out int previous) &&
                        previous == hash) continue;
                    _clientEmployeeObserved[company] = hash;
                    MarkStateDirty(property);
                    _clientLifecycleRepairs++;
                }
            }
        }

        /// <summary>
        /// Efficiency feeds the panel's production rate for processing industry and offices. Exact hashes
        /// suppress chunk neighbours and the echo of client corrections.
        /// </summary>
        internal void CaptureEfficiencyChanges(NativeArray<Entity> properties)
        {
            MultiplayerService service = Mod.Service;
            if (service == null || !service.SimulationSyncReady) return;

            bool host = service.Session.Role == SessionRole.Host;
            Dictionary<Entity, int> observed = host
                ? _hostEfficiencyObserved : _clientEfficiencyObserved;
            if (observed.Count > MaxObservedEfficiencyBuffers) observed.Clear();
            bool initialized = host
                ? _hostEfficiencyObservationInitialized
                : _clientEfficiencyObservationInitialized;

            for (int i = 0; i < properties.Length; i++)
            {
                Entity property = properties[i];
                if (!IsLiveWorkplaceProperty(property) ||
                    !EntityManager.HasBuffer<Efficiency>(property)) continue;
                int hash = HashEfficiencyBuffer(property);
                if (observed.TryGetValue(property, out int previous) && previous == hash) continue;
                observed[property] = hash;

                // The first observation reports every chunk: record it as baseline, not priority traffic.
                if (!initialized) continue;

                if (host)
                {
                    if (!TryGetWorkplaceIdentity(property, out PropertyIdentity identity)) continue;
                    Prioritize(property, identity);
                    _hostEfficiencySignals++;
                }
                else if (_cache.ContainsKey(property))
                {
                    MarkEfficiencyDirty(property);
                    _clientEfficiencyRepairs++;
                }
            }

            if (host) _hostEfficiencyObservationInitialized = true;
            else _clientEfficiencyObservationInitialized = true;
        }

        /// <summary>
        /// Extraction is read straight off the company and rewritten from local depletion and a random
        /// draw, so the host ships its figure right after the extractor pass.
        /// </summary>
        internal void CaptureExtractorProduceChanges(NativeArray<Entity> companies)
        {
            MultiplayerService service = Mod.Service;
            if (service == null || !service.SimulationSyncReady) return;

            bool host = service.Session.Role == SessionRole.Host;
            int signalled = 0;
            for (int i = 0; i < companies.Length; i++)
            {
                Entity company = companies[i];
                if (company == Entity.Null || !EntityManager.Exists(company) ||
                    !EntityManager.HasComponent<CompanyStatisticData>(company) ||
                    !EntityManager.HasComponent<PropertyRenter>(company)) continue;
                Entity property = EntityManager.GetComponentData<PropertyRenter>(company).m_Property;
                if (!IsLiveWorkplaceProperty(property)) continue;

                if (host)
                {
                    int produce = EntityManager
                        .GetComponentData<CompanyStatisticData>(company).m_LastUpdateProduce;
                    if (_hostExtractorProduce.TryGetValue(company, out int previous) &&
                        previous == produce) continue;
                    if (_hostExtractorProduce.Count > MaxObservedExtractorCompanies)
                        _hostExtractorProduce.Clear();
                    _hostExtractorProduce[company] = produce;
                    // Bounded so a farming region cannot own the priority queue; the rolling detector covers the rest.
                    if (signalled >= MaxExtractorSignalsPerBoundary) continue;
                    if (!TryGetWorkplaceIdentity(property, out PropertyIdentity identity)) continue;
                    Prioritize(property, identity);
                    signalled++;
                    _hostExtractorSignals++;
                }
                else ApplyExtractorProduce(company, property);
            }
        }

        private int HashEfficiencyBuffer(Entity property)
        {
            unchecked
            {
                if (!EntityManager.HasBuffer<Efficiency>(property)) return 0;
                DynamicBuffer<Efficiency> factors =
                    EntityManager.GetBuffer<Efficiency>(property, true);
                int hash = ((int)2166136261 ^ factors.Length) * 16777619;
                for (int i = 0; i < factors.Length; i++)
                {
                    Efficiency factor = factors[i];
                    hash = (hash ^ (byte)factor.m_Factor) * 16777619;
                    hash = (hash ^ factor.m_Efficiency.GetHashCode()) * 16777619;
                }
                return hash;
            }
        }

        private int HashEmployeeBuffer(Entity company)
        {
            unchecked
            {
                if (!EntityManager.HasBuffer<Employee>(company)) return 0;
                DynamicBuffer<Employee> employees = EntityManager.GetBuffer<Employee>(company, true);
                int hash = ((int)2166136261 ^ employees.Length) * 16777619;
                for (int i = 0; i < employees.Length; i++)
                {
                    Employee employee = employees[i];
                    hash = (hash ^ employee.m_Worker.Index) * 16777619;
                    hash = (hash ^ employee.m_Worker.Version) * 16777619;
                    hash = (hash ^ employee.m_Level) * 16777619;
                }
                return hash;
            }
        }

        private bool TryGetWorkplaceIdentity(Entity property, out PropertyIdentity identity)
        {
            identity = default(PropertyIdentity);
            if (!IsLiveWorkplaceProperty(property)) return false;
            string prefabName = _prefabIndex.NameOf(
                EntityManager.GetComponentData<PrefabRef>(property).m_Prefab);
            if (string.IsNullOrEmpty(prefabName)) return false;
            global::Game.Objects.Transform transform =
                EntityManager.GetComponentData<global::Game.Objects.Transform>(property);
            identity = new PropertyIdentity(prefabName, transform.m_Position.x,
                transform.m_Position.y, transform.m_Position.z);
            return true;
        }

        /// <summary>A building's identity and its business, or none: vacancy is a valid statement.</summary>
        private bool TryCaptureEntry(Entity property, out CompanyStatsEntry entry)
        {
            entry = default(CompanyStatsEntry);
            if (!IsLiveWorkplaceProperty(property)) return false;

            string prefabName = _prefabIndex.NameOf(
                EntityManager.GetComponentData<PrefabRef>(property).m_Prefab);
            if (string.IsNullOrEmpty(prefabName)) return false;

            global::Game.Objects.Transform transform =
                EntityManager.GetComponentData<global::Game.Objects.Transform>(property);
            byte constructionSpeed = 0;
            if (EntityManager.HasComponent<global::Game.Objects.UnderConstruction>(property))
            {
                byte speed = EntityManager
                    .GetComponentData<global::Game.Objects.UnderConstruction>(property).m_Speed;
                // Zero on the wire is reserved for the authoritative completed state.
                constructionSpeed = speed == 0 ? (byte)1 : speed;
            }
            entry = new CompanyStatsEntry
            {
                PrefabName = prefabName,
                AnchorX = transform.m_Position.x,
                AnchorY = transform.m_Position.y,
                AnchorZ = transform.m_Position.z,
                ConstructionSpeed = constructionSpeed,
                CompanyPrefabName = string.Empty,
            };

            Entity company = FindTenant(property);
            if (company != Entity.Null &&
                EntityManager.HasComponent<CompanyStatisticData>(company) &&
                EntityManager.HasComponent<PrefabRef>(company) &&
                EntityManager.HasComponent<CompanyData>(company) &&
                !EntityManager.HasComponent<global::Game.Common.Created>(company))
            {
                string companyName = _prefabIndex.NameOf(
                    EntityManager.GetComponentData<PrefabRef>(company).m_Prefab);
                // Unnameable is not vacant: fail the entry rather than tell clients to close the shop.
                if (string.IsNullOrEmpty(companyName)) return false;

                CompanyData companyData = EntityManager.GetComponentData<CompanyData>(company);
                string brandName = _prefabIndex.NameOf(companyData.m_Brand);
                if (string.IsNullOrEmpty(brandName) || companyData.m_RandomSeed.state == 0)
                    return false;

                if (!_nameSystem.TryGetCustomName(company, out string customName))
                    customName = string.Empty;
                customName = WireGuard.SanitizeText(customName, WireGuard.MaxNameLength);

                CompanyStatisticData data =
                    EntityManager.GetComponentData<CompanyStatisticData>(company);
                if (!TryCaptureEfficiencies(property, out bool hasEfficiency,
                    out CompanyStatsEfficiency[] efficiencies))
                    return false;
                entry.HasTenant = true;
                entry.HasEfficiency = hasEfficiency;
                entry.Efficiencies = efficiencies;
                entry.CompanyPrefabName = companyName;
                entry.BrandPrefabName = brandName;
                entry.CompanyCustomName = customName;
                entry.CompanyRandomState = companyData.m_RandomSeed.state;
                entry.MaxNumberOfCustomers = data.m_MaxNumberOfCustomers;
                entry.MonthlyCustomerCount = data.m_MonthlyCustomerCount;
                entry.MonthlyCostBuyingResources = data.m_MonthlyCostBuyingResources;
                entry.CurrentNumberOfCustomers = data.m_CurrentNumberOfCustomers;
                entry.CurrentCostOfBuyingResources = data.m_CurrentCostOfBuyingResources;
                entry.Income = data.m_Income;
                entry.Worth = data.m_Worth;
                entry.Profit = data.m_Profit;
                entry.WagePaid = data.m_WagePaid;
                entry.RentPaid = data.m_RentPaid;
                entry.ElectricityPaid = data.m_ElectricityPaid;
                entry.WaterPaid = data.m_WaterPaid;
                entry.SewagePaid = data.m_SewagePaid;
                entry.GarbagePaid = data.m_GarbagePaid;
                entry.TaxPaid = data.m_TaxPaid;
                entry.CostBuyResource = data.m_CostBuyResource;
                entry.LastUpdateWorth = data.m_LastUpdateWorth;
                entry.LastUpdateProduce = data.m_LastUpdateProduce;
                entry.LastFrameLowIncome = data.m_LastFrameLowIncome;
                entry.Resources = CaptureResources(company);
                entry.TradeCosts = CaptureTradeCosts(company);
                entry.Employees = CaptureEmployees(company, out entry.EmployeeRosterComplete);

                if (EntityManager.HasComponent<Profitability>(company))
                {
                    Profitability profitability =
                        EntityManager.GetComponentData<Profitability>(company);
                    entry.HasProfitability = true;
                    entry.Profitability = profitability.m_Profitability;
                    entry.LastTotalWorth = profitability.m_LastTotalWorth;
                }

                if (EntityManager.HasComponent<ServiceAvailable>(company))
                {
                    ServiceAvailable service =
                        EntityManager.GetComponentData<ServiceAvailable>(company);
                    entry.HasServiceAvailable = true;
                    entry.ServiceAvailable = service.m_ServiceAvailable;
                    entry.ServiceMeanPriority = service.m_MeanPriority;
                }

                if (EntityManager.HasComponent<LodgingProvider>(company))
                {
                    LodgingProvider lodging =
                        EntityManager.GetComponentData<LodgingProvider>(company);
                    entry.HasLodgingProvider = true;
                    entry.FreeLodgingRooms = lodging.m_FreeRooms;
                    entry.LodgingPrice = lodging.m_Price;
                }

                if (EntityManager.HasComponent<WorkProvider>(company))
                {
                    entry.HasWorkProvider = true;
                    entry.MaxWorkers =
                        EntityManager.GetComponentData<WorkProvider>(company).m_MaxWorkers;
                }

                if (EntityManager.HasComponent<TaxPayer>(company))
                {
                    TaxPayer tax = EntityManager.GetComponentData<TaxPayer>(company);
                    entry.HasTaxPayer = true;
                    entry.UntaxedIncome = tax.m_UntaxedIncome;
                    entry.AverageTaxRate = tax.m_AverageTaxRate;
                    entry.AverageTaxPaid = tax.m_AverageTaxPaid;
                }
            }

            // A throw in Write would suppress every channel sharing the snapshot.
            return CompanyStatsSnapshot.IsValidEntry(entry);
        }

        private CompanyStatsResource[] CaptureResources(Entity company)
        {
            if (!EntityManager.HasBuffer<global::Game.Economy.Resources>(company)) return null;
            DynamicBuffer<global::Game.Economy.Resources> resources =
                EntityManager.GetBuffer<global::Game.Economy.Resources>(company, true);
            _resourceScratch.Clear();
            int count = EconomyUtils.ResourceCount;
            if (count > CompanyStatsSnapshot.MaxResourceSlots)
                count = CompanyStatsSnapshot.MaxResourceSlots;
            for (int i = 0; i < count; i++)
            {
                int amount = EconomyUtils.GetResources(EconomyUtils.GetResource(i), resources);
                // Zero is the default on the receiver, so an empty slot costs nothing to omit.
                if (amount == 0) continue;
                _resourceScratch.Add(new CompanyStatsResource { Index = i, Amount = amount });
            }
            if (_resourceScratch.Count == 0) return null;
            return _resourceScratch.ToArray();
        }

        private bool TryCaptureEfficiencies(Entity property, out bool present,
            out CompanyStatsEfficiency[] result)
        {
            present = EntityManager.HasBuffer<Efficiency>(property);
            result = null;
            if (!present) return true;

            DynamicBuffer<Efficiency> factors = EntityManager.GetBuffer<Efficiency>(property, true);
            if (factors.Length > CompanyStatsSnapshot.MaxEfficiencySlots) return false;
            _efficiencyScratch.Clear();
            for (int i = 0; i < factors.Length; i++)
            {
                Efficiency factor = factors[i];
                _efficiencyScratch.Add(new CompanyStatsEfficiency
                {
                    Factor = (byte)factor.m_Factor,
                    Value = factor.m_Efficiency,
                });
            }
            if (_efficiencyScratch.Count > 0) result = _efficiencyScratch.ToArray();
            return true;
        }

        private CompanyStatsTradeCost[] CaptureTradeCosts(Entity company)
        {
            if (!EntityManager.HasBuffer<TradeCost>(company)) return null;
            DynamicBuffer<TradeCost> costs = EntityManager.GetBuffer<TradeCost>(company, true);
            _tradeCostScratch.Clear();
            int count = costs.Length;
            if (count > CompanyStatsSnapshot.MaxTradeCostSlots)
                count = CompanyStatsSnapshot.MaxTradeCostSlots;
            for (int i = 0; i < count; i++)
            {
                TradeCost cost = costs[i];
                int index = EconomyUtils.GetResourceIndex(cost.m_Resource);
                if (index < 0 || index >= CompanyStatsSnapshot.MaxResourceSlots) continue;
                _tradeCostScratch.Add(new CompanyStatsTradeCost
                {
                    Index = index,
                    BuyCost = cost.m_BuyCost,
                    SellCost = cost.m_SellCost,
                    LastTransferRequestTime = cost.m_LastTransferRequestTime,
                });
            }
            return _tradeCostScratch.Count == 0 ? null : _tradeCostScratch.ToArray();
        }

        /// <summary>
        /// Regular residents only (occupancy gives them identity). A partial roster lets clients add
        /// workers but never remove an unmatched local one.
        /// </summary>
        private CompanyStatsEmployee[] CaptureEmployees(Entity company, out bool complete)
        {
            complete = EntityManager.HasBuffer<Employee>(company);
            if (!complete) return null;

            DynamicBuffer<Employee> employees = EntityManager.GetBuffer<Employee>(company, true);
            _employeeScratch.Clear();
            _employeeIdScratch.Clear();
            if (employees.Length > CompanyStatsSnapshot.MaxEmployeeSlots) complete = false;
            int count = employees.Length;
            if (count > CompanyStatsSnapshot.MaxEmployeeSlots)
                count = CompanyStatsSnapshot.MaxEmployeeSlots;

            for (int i = 0; i < count; i++)
            {
                Employee employee = employees[i];
                Entity citizen = employee.m_Worker;
                if (citizen == Entity.Null || !EntityManager.Exists(citizen) ||
                    !EntityManager.HasComponent<Citizen>(citizen) ||
                    !EntityManager.HasComponent<HouseholdMember>(citizen) ||
                    !EntityManager.HasComponent<Worker>(citizen) ||
                    EntityManager.HasComponent<global::Game.Common.Deleted>(citizen) ||
                    EntityManager.HasComponent<global::Game.Tools.Temp>(citizen))
                {
                    complete = false;
                    continue;
                }

                Entity household = EntityManager.GetComponentData<HouseholdMember>(citizen)
                    .m_Household;
                if (household == Entity.Null || !EntityManager.Exists(household) ||
                    !EntityManager.HasComponent<Household>(household) ||
                    EntityManager.HasComponent<TouristHousehold>(household) ||
                    EntityManager.HasComponent<CommuterHousehold>(household))
                {
                    complete = false;
                    continue;
                }

                Worker worker = EntityManager.GetComponentData<Worker>(citizen);
                if (worker.m_Workplace != company || employee.m_Level > 4 ||
                    (byte)worker.m_Shift > 2 || worker.m_LastCommuteTime < 0f ||
                    float.IsNaN(worker.m_LastCommuteTime) ||
                    float.IsInfinity(worker.m_LastCommuteTime))
                {
                    complete = false;
                    continue;
                }

                ulong citizenId = ResidentialOccupancySyncSystem.PackNetworkCitizenId(citizen);
                if (citizenId == 0 || !_employeeIdScratch.Add(citizenId))
                {
                    complete = false;
                    continue;
                }
                _employeeScratch.Add(new CompanyStatsEmployee
                {
                    CitizenId = citizenId,
                    Level = employee.m_Level,
                    LastCommuteTime = worker.m_LastCommuteTime,
                    Shift = (byte)worker.m_Shift,
                });
            }

            return _employeeScratch.Count == 0 ? null : _employeeScratch.ToArray();
        }

        /// <summary>Tenancy and watched figures, so an opening or closing goes out on the next page.</summary>
        private static int Hash(CompanyStatsEntry entry)
        {
            unchecked
            {
                int hash = (int)2166136261;
                // Level and construction state first: property capacity depends on the prefab.
                hash = (hash ^ (entry.PrefabName == null
                    ? 0 : entry.PrefabName.GetHashCode())) * 16777619;
                hash = (hash ^ entry.ConstructionSpeed) * 16777619;
                hash = (hash ^ (entry.HasTenant ? 1 : 0)) * 16777619;
                hash = (hash ^ (entry.CompanyPrefabName == null
                    ? 0 : entry.CompanyPrefabName.GetHashCode())) * 16777619;
                if (!entry.HasTenant) return hash;
                hash = (hash ^ (entry.BrandPrefabName == null
                    ? 0 : entry.BrandPrefabName.GetHashCode())) * 16777619;
                hash = (hash ^ (entry.CompanyCustomName == null
                    ? 0 : entry.CompanyCustomName.GetHashCode())) * 16777619;
                hash = (hash ^ unchecked((int)entry.CompanyRandomState)) * 16777619;
                hash = (hash ^ entry.MaxNumberOfCustomers) * 16777619;
                hash = (hash ^ entry.MonthlyCostBuyingResources) * 16777619;
                hash = (hash ^ entry.CurrentCostOfBuyingResources) * 16777619;
                hash = (hash ^ entry.Income) * 16777619;
                hash = (hash ^ entry.Worth) * 16777619;
                hash = (hash ^ entry.Profit) * 16777619;
                hash = (hash ^ entry.WagePaid) * 16777619;
                hash = (hash ^ entry.RentPaid) * 16777619;
                hash = (hash ^ entry.TaxPaid) * 16777619;
                hash = (hash ^ entry.CurrentNumberOfCustomers) * 16777619;
                hash = (hash ^ entry.MonthlyCustomerCount) * 16777619;
                hash = (hash ^ entry.LastUpdateProduce) * 16777619;
                hash = (hash ^ entry.CostBuyResource) * 16777619;
                hash = (hash ^ (entry.HasProfitability ? entry.Profitability : 0)) * 16777619;
                hash = (hash ^ (entry.HasServiceAvailable ? entry.ServiceAvailable : 0)) * 16777619;
                hash = (hash ^ (entry.HasServiceAvailable
                    ? entry.ServiceMeanPriority.GetHashCode() : 0)) * 16777619;
                hash = (hash ^ (entry.HasWorkProvider ? entry.MaxWorkers : 0)) * 16777619;
                hash = (hash ^ (entry.HasTaxPayer ? entry.UntaxedIncome : 0)) * 16777619;
                hash = (hash ^ (entry.HasTaxPayer ? entry.AverageTaxRate : 0)) * 16777619;
                hash = (hash ^ (entry.HasTaxPayer ? entry.AverageTaxPaid : 0)) * 16777619;
                CompanyStatsEfficiency[] efficiencies = entry.Efficiencies;
                hash = (hash ^ (entry.HasEfficiency ? 1 : 0)) * 16777619;
                hash = (hash ^ (efficiencies == null ? 0 : efficiencies.Length)) * 16777619;
                if (efficiencies != null)
                    for (int i = 0; i < efficiencies.Length; i++)
                        hash = (hash ^ efficiencies[i].Factor ^
                                efficiencies[i].Value.GetHashCode()) * 16777619;
                CompanyStatsResource[] resources = entry.Resources;
                hash = (hash ^ (resources == null ? 0 : resources.Length)) * 16777619;
                if (resources != null)
                    for (int i = 0; i < resources.Length; i++)
                        hash = (hash ^ resources[i].Index ^ resources[i].Amount) * 16777619;
                CompanyStatsTradeCost[] costs = entry.TradeCosts;
                hash = (hash ^ (costs == null ? 0 : costs.Length)) * 16777619;
                if (costs != null)
                    for (int i = 0; i < costs.Length; i++)
                    {
                        hash = (hash ^ costs[i].Index ^ costs[i].BuyCost.GetHashCode() ^
                                costs[i].SellCost.GetHashCode()) * 16777619;
                        hash = (hash ^ costs[i].LastTransferRequestTime.GetHashCode()) * 16777619;
                    }
                CompanyStatsEmployee[] employees = entry.Employees;
                hash = (hash ^ (entry.EmployeeRosterComplete ? 1 : 0)) * 16777619;
                hash = (hash ^ (employees == null ? 0 : employees.Length)) * 16777619;
                if (employees != null)
                    for (int i = 0; i < employees.Length; i++)
                    {
                        hash = (hash ^ employees[i].CitizenId.GetHashCode() ^ employees[i].Level ^
                                employees[i].Shift ^ employees[i].LastCommuteTime.GetHashCode()) *
                               16777619;
                    }
                return hash;
            }
        }

        private void AdvanceHostSweep()
        {
            _capturePageIndex = 0;
            _captureSweepId = unchecked(_captureSweepId + 1);
            if (_captureSweepId == 0) _captureSweepId = 1;
        }
    }
}
