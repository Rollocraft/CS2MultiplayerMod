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
            if (service == null || !service.GameplaySyncReady ||
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
            var identities = new HashSet<PropertyRentIdentity>();
            int estimatedBytes = AddPriorityEntries(snapshot, identities, 9);

            int index = _captureCursor;
            while (index < _hostSweepEntities.Length &&
                   snapshot.Entries.Count < CompanyStatsSnapshot.MaxEntries)
            {
                CompanyStatsEntry entry;
                if (!TryCaptureEntry(_hostSweepEntities[index], out entry))
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
                    // Keep this baseline entity for the next page. Validation caps one employer's
                    // roster so a single entry always fits an otherwise empty page.
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

        /// <summary>
        /// A city with no workplace building still has to close its sweep, or a client that
        /// bulldozed its last shop would keep the previous roster cached forever.
        /// </summary>
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
            HashSet<PropertyRentIdentity> identities, int estimatedBytes)
        {
            int added = 0;
            while (added < PriorityEntriesPerPage &&
                   snapshot.Entries.Count < CompanyStatsSnapshot.MaxEntries &&
                   _priorityOrder.Count > 0)
            {
                PropertyRentIdentity identity;
                if (!_priorityOrder.TryDequeue(out identity)) break;
                Entity property;
                if (!_priority.TryGetValue(identity, out property)) continue;
                // Recapture at send time: the queued signal says only "this changed", and a stale
                // copy could otherwise lose to a fresher baseline entry in the same page.
                CompanyStatsEntry entry;
                if (!TryCaptureEntry(property, out entry))
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
                // Priority entries may consume most of a page, but always leave enough room for
                // the baseline to advance. Keep the first entry that does not fit queued for the
                // following page. Continuing here used to drain and silently discard the whole
                // remaining priority queue once a dense employee roster filled the byte budget.
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
        /// The rolling change detector. It walks at most
        /// <see cref="MaxPropertiesObservedPerUpdate"/> buildings of one partition per update and
        /// resumes where it stopped, so its cost does not grow with the city. A partition counts
        /// as initialized only once the cursor has been all the way round it, so a fresh session
        /// does not flag every building in the city as changed at once.
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
                    CompanyStatsEntry entry;
                    if (!TryCaptureEntry(property, out entry)) continue;
                    int hash = Hash(entry);
                    int observed;
                    if (!_hostObserved.TryGetValue(property, out observed))
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

        private void Prioritize(Entity property, PropertyRentIdentity identity)
        {
            if (_priority.ContainsKey(identity))
            {
                _priority[identity] = property;
                return;
            }
            while (_priority.Count >= MaxPriorityEntries && _priorityOrder.Count > 0)
            {
                PropertyRentIdentity oldest;
                if (!_priorityOrder.TryDequeue(out oldest)) break;
                if (_priority.Remove(oldest)) _priorityDrops++;
            }
            if (_priority.Count >= MaxPriorityEntries)
            {
                _priorityDrops++;
                return;
            }
            _priority[identity] = property;
            _priorityOrder.Enqueue(identity);
            _priorityChanges++;
        }

        /// <summary>
        /// Fast path for company move-in/out. PropertyProcessing emits the same RentersUpdated
        /// event for business tenants as for households; using it avoids waiting for the workplace
        /// property's 2,048-frame rolling change rotation.
        /// </summary>
        internal void CaptureTenancyChanges()
        {
            MultiplayerService service = Mod.Service;
            if (service == null || !service.GameplaySyncReady ||
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
                        PropertyRentIdentity identity;
                        if (!TryGetWorkplaceIdentity(property, out identity)) continue;
                        // Recapture at page-send time, after company initialization has removed
                        // Created. Capturing the event frame itself can misreport a new tenant as
                        // vacancy while its native initialization is still in flight.
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

        /// <summary>
        /// Employee is a dynamic buffer, so hiring and firing do not emit a renter event. Called
        /// directly after FindJobSystem with only chunks whose buffer version changed.
        /// </summary>
        internal void CaptureEmployeeChanges(NativeArray<Entity> companies)
        {
            MultiplayerService service = Mod.Service;
            if (service == null || !service.GameplaySyncReady) return;

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
                    int previous;
                    if (_hostEmployeeObserved.TryGetValue(company, out previous) &&
                        previous == hash) continue;
                    _hostEmployeeObserved[company] = hash;
                    PropertyRentIdentity identity;
                    if (!TryGetWorkplaceIdentity(property, out identity)) continue;
                    Prioritize(property, identity);
                    _hostLifecycleSignals++;
                }
                else if (_cache.ContainsKey(property))
                {
                    MarkStateDirty(property);
                    _clientLifecycleRepairs++;
                }
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

        private bool TryGetWorkplaceIdentity(Entity property, out PropertyRentIdentity identity)
        {
            identity = default(PropertyRentIdentity);
            if (!IsLiveWorkplaceProperty(property)) return false;
            string prefabName = _prefabIndex.NameOf(
                EntityManager.GetComponentData<PrefabRef>(property).m_Prefab);
            if (string.IsNullOrEmpty(prefabName)) return false;
            global::Game.Objects.Transform transform =
                EntityManager.GetComponentData<global::Game.Objects.Transform>(property);
            identity = new PropertyRentIdentity(prefabName, transform.m_Position.x,
                transform.m_Position.y, transform.m_Position.z);
            return true;
        }

        /// <summary>
        /// One building's complete statement: its identity, and either the business renting it or
        /// nothing at all. A vacant entry is not a failure - it is the point of sweeping buildings
        /// rather than businesses.
        /// </summary>
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
                // An unnameable business is not the same statement as an empty building: reporting
                // it vacant would tell every client to close a shop that is trading fine. Fail the
                // whole entry and let the next sweep try again.
                if (string.IsNullOrEmpty(companyName)) return false;

                CompanyData companyData = EntityManager.GetComponentData<CompanyData>(company);
                string brandName = _prefabIndex.NameOf(companyData.m_Brand);
                if (string.IsNullOrEmpty(brandName) || companyData.m_RandomSeed.state == 0)
                    return false;

                string customName;
                if (!_nameSystem.TryGetCustomName(company, out customName))
                    customName = string.Empty;
                customName = WireGuard.SanitizeText(customName, WireGuard.MaxNameLength);

                CompanyStatisticData data =
                    EntityManager.GetComponentData<CompanyStatisticData>(company);
                entry.HasTenant = true;
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

            // Never let a broken local prefab or transform reach Write, where a throw would
            // suppress every other channel sharing this snapshot.
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
        /// Capture only regular residents, because occupancy is what gives those citizens a
        /// cross-machine identity. A commuter/tourist or a temporarily inconsistent native graph
        /// makes the roster partial: clients may add the residents listed here, but must not erase
        /// an unmatched local worker on the strength of an incomplete statement.
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

        /// <summary>
        /// Covers tenancy first, then the figures a player watches. Money drifts continuously by
        /// design and the baseline sweep carries it anyway; what this hash is for is getting a
        /// business opening or closing onto the wire in the next page rather than the next sweep.
        /// </summary>
        private static int Hash(CompanyStatsEntry entry)
        {
            unchecked
            {
                int hash = (int)2166136261;
                // Property level and construction state precede tenancy. Dense buildings depend
                // on the prefab's property capacity, so completion must be priority traffic even
                // when the business itself did not change.
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
