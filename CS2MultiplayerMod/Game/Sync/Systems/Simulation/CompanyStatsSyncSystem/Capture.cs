using System.Collections.Generic;
using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Sync.Commands;
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
            AddPriorityEntries(snapshot, identities);

            int index = _captureCursor;
            while (index < _hostSweepEntities.Length &&
                   snapshot.Entries.Count < CompanyStatsSnapshot.MaxEntries)
            {
                CompanyStatsEntry entry;
                if (TryCaptureEntry(_hostSweepEntities[index], out entry) &&
                    identities.Add(entry.Identity))
                    snapshot.Entries.Add(entry);
                else _captureSkips++;
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

        private void AddPriorityEntries(CompanyStatsSnapshot snapshot,
            HashSet<PropertyRentIdentity> identities)
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
                _priority.Remove(identity);
                // Recapture at send time: the queued signal says only "this changed", and a stale
                // copy could otherwise lose to a fresher baseline entry in the same page.
                CompanyStatsEntry entry;
                if (!TryCaptureEntry(property, out entry)) continue;
                if (!identities.Add(entry.Identity)) continue;
                snapshot.Entries.Add(entry);
                added++;
            }
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
            entry = new CompanyStatsEntry
            {
                PrefabName = prefabName,
                AnchorX = transform.m_Position.x,
                AnchorY = transform.m_Position.y,
                AnchorZ = transform.m_Position.z,
                CompanyPrefabName = string.Empty,
            };

            Entity company = FindTenant(property);
            if (company != Entity.Null &&
                EntityManager.HasComponent<CompanyStatisticData>(company) &&
                EntityManager.HasComponent<PrefabRef>(company))
            {
                string companyName = _prefabIndex.NameOf(
                    EntityManager.GetComponentData<PrefabRef>(company).m_Prefab);
                // An unnameable business is not the same statement as an empty building: reporting
                // it vacant would tell every client to close a shop that is trading fine. Fail the
                // whole entry and let the next sweep try again.
                if (string.IsNullOrEmpty(companyName)) return false;

                CompanyStatisticData data =
                    EntityManager.GetComponentData<CompanyStatisticData>(company);
                entry.HasTenant = true;
                entry.CompanyPrefabName = companyName;
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

                if (EntityManager.HasComponent<Profitability>(company))
                {
                    Profitability profitability =
                        EntityManager.GetComponentData<Profitability>(company);
                    entry.HasProfitability = true;
                    entry.Profitability = profitability.m_Profitability;
                    entry.LastTotalWorth = profitability.m_LastTotalWorth;
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
                hash = (hash ^ (entry.HasTenant ? 1 : 0)) * 16777619;
                hash = (hash ^ (entry.CompanyPrefabName == null
                    ? 0 : entry.CompanyPrefabName.GetHashCode())) * 16777619;
                if (!entry.HasTenant) return hash;
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
                CompanyStatsResource[] resources = entry.Resources;
                hash = (hash ^ (resources == null ? 0 : resources.Length)) * 16777619;
                if (resources != null)
                    for (int i = 0; i < resources.Length; i++)
                        hash = (hash ^ resources[i].Index ^ resources[i].Amount) * 16777619;
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
