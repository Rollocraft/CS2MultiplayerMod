using System.Collections.Concurrent;
using System.Collections.Generic;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Sync.Commands;
using Game.Agents;
using Game.Buildings;
using Game.Citizens;
using Game.Economy;
using Unity.Collections;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    public partial class ResidentialOccupancySyncSystem
    {
        private struct DesiredHouseholdEconomy
        {
            public PropertyIdentity PropertyIdentity;
            public ulong Revision;
            public int Rent;
            public int Savings;
            public int Money;
            public bool HasTaxPayer;
            public int UntaxedIncome;
            public int AverageTaxRate;
            public int AverageTaxPaid;
            public short ConsumptionPerDay;
            public uint ShoppedValuePerDay;
            public uint ShoppedValueLastDay;
            public uint LastDayFrameIndex;
            public int Income;
            public int MoneySpentOnBuildingLevelingLastDay;

            public static DesiredHouseholdEconomy From(OccupancyHousehold household,
                PropertyIdentity propertyIdentity, ulong revision) =>
                new DesiredHouseholdEconomy
                {
                    PropertyIdentity = propertyIdentity,
                    Revision = revision,
                    Rent = household.Rent,
                    Savings = household.Savings,
                    Money = household.Money,
                    HasTaxPayer = household.HasTaxPayer,
                    UntaxedIncome = household.UntaxedIncome,
                    AverageTaxRate = household.AverageTaxRate,
                    AverageTaxPaid = household.AverageTaxPaid,
                    ConsumptionPerDay = household.ConsumptionPerDay,
                    ShoppedValuePerDay = household.ShoppedValuePerDay,
                    ShoppedValueLastDay = household.ShoppedValueLastDay,
                    LastDayFrameIndex = household.LastDayFrameIndex,
                    Income = household.Income,
                    MoneySpentOnBuildingLevelingLastDay =
                        household.MoneySpentOnBuildingLevelingLastDay,
                };
        }

        private readonly Dictionary<ulong, DesiredHouseholdEconomy> _desiredHouseholdEconomies =
            new Dictionary<ulong, DesiredHouseholdEconomy>();

        /// <summary>Households corrected per frame; this pass runs at full simulation rate.</summary>
        private const int MaxHouseholdEconomyCorrectionsPerFrame = 512;

        // Changed-version queries advance after one update, so every changed entity is retained until
        // its bounded correction runs.
        private readonly ConcurrentQueue<Entity> _economyCorrectionQueue =
            new ConcurrentQueue<Entity>();
        private readonly HashSet<Entity> _economyCorrectionMembers = new HashSet<Entity>();

        private void ObserveDesiredHouseholdEconomy(ulong householdId,
            PropertyIdentity propertyIdentity, ulong revision, OccupancyHousehold household)
        {
            if (householdId == 0 ||
                (_desiredHouseholdEconomies.TryGetValue(householdId, out DesiredHouseholdEconomy existing) &&
                 revision <= existing.Revision)) return;
            _desiredHouseholdEconomies[householdId] =
                DesiredHouseholdEconomy.From(household, propertyIdentity, revision);
        }

        private void ForgetDesiredHouseholdEconomy(ulong householdId, ulong revision)
        {
            if (householdId == 0 ||
                !_desiredHouseholdEconomies.TryGetValue(householdId, out DesiredHouseholdEconomy existing) ||
                revision <= existing.Revision) return;
            _desiredHouseholdEconomies.Remove(householdId);
        }

        /// <summary>Nothing to correct on a host or before the first roster.</summary>
        internal bool WantsHouseholdEconomyCorrection
        {
            get
            {
                MultiplayerService service = Mod.Service;
                return service != null && service.SimulationSyncReady &&
                       service.Session.Role == SessionRole.Client &&
                       _desiredHouseholdEconomies.Count != 0;
            }
        }

        /// <summary>Retain changed entities before the query advances; duplicates coalesce.</summary>
        internal void QueueHouseholdEconomyCorrections(NativeArray<Entity> households)
        {
            for (int i = 0; i < households.Length; i++)
            {
                Entity household = households[i];
                // Membership first: changed chunks can hold most of the city.
                if (_economyCorrectionMembers.Contains(household)) continue;
                if (!_hostIdsByHousehold.TryGetValue(household, out ulong householdId) ||
                    !_desiredHouseholdEconomies.ContainsKey(householdId)) continue;
                _economyCorrectionMembers.Add(household);
                _economyCorrectionQueue.Enqueue(household);
            }
        }

        /// <summary>
        /// Restores host scalars for a bounded set of households, only where current property and identity
        /// agree, so a delayed source page cannot touch a move destination.
        /// </summary>
        internal void CorrectHouseholdEconomyAfterLocalUpdate()
        {
            MultiplayerService service = Mod.Service;
            if (service == null || !service.SimulationSyncReady ||
                service.Session.Role != SessionRole.Client)
            {
                ClearHouseholdEconomyCorrections();
                return;
            }

            int examine = _economyCorrectionQueue.Count < MaxHouseholdEconomyCorrectionsPerFrame
                ? _economyCorrectionQueue.Count : MaxHouseholdEconomyCorrectionsPerFrame;
            for (int i = 0; i < examine; i++)
            {
                if (!_economyCorrectionQueue.TryDequeue(out Entity household)) break;
                _economyCorrectionMembers.Remove(household);
                if (!TryResolveCorrectionTarget(household, out DesiredHouseholdEconomy wanted,
                    out Entity property)) continue;

                if (ApplyHouseholdEconomy(household, property, wanted))
                    _economyCorrections++;
            }
            if (_economyCorrectionQueue.Count != 0)
                _economyDeferred += _economyCorrectionQueue.Count;
        }

        internal void ClearHouseholdEconomyCorrections()
        {
            while (_economyCorrectionQueue.TryDequeue(out Entity discarded)) { }
            _economyCorrectionMembers.Clear();
        }

        /// <summary>Still the host household the page described, still where the page put it.</summary>
        private bool TryResolveCorrectionTarget(Entity household,
            out DesiredHouseholdEconomy wanted, out Entity property)
        {
            wanted = default(DesiredHouseholdEconomy);
            property = Entity.Null;
            if (!TryGetBoundHouseholdId(household, out ulong householdId) ||
                !_desiredHouseholdEconomies.TryGetValue(householdId, out wanted) ||
                !EntityManager.HasComponent<PropertyRenter>(household)) return false;

            property = EntityManager.GetComponentData<PropertyRenter>(household).m_Property;
            return TryGetPropertyIdentity(property, out PropertyIdentity identity) &&
                   TryGetDesiredPropertyIdentity(householdId, out PropertyIdentity desired) &&
                   desired.Equals(identity) &&
                   wanted.PropertyIdentity.Equals(identity);
        }

        // Income has its own earlier boundary: wellbeing and GoodWealth read Household.m_Income in the
        // same frame it is recomputed. It runs a quarter as often and writes one field.
        private const int MaxHouseholdIncomeCorrectionsPerFrame =
            4 * MaxHouseholdEconomyCorrectionsPerFrame;

        private readonly ConcurrentQueue<Entity> _incomeCorrectionQueue =
            new ConcurrentQueue<Entity>();
        private readonly HashSet<Entity> _incomeCorrectionMembers = new HashSet<Entity>();

        /// <summary>Same pre-filter as the economy queue: two dictionary probes, no ECS access.</summary>
        internal void QueueHouseholdIncomeCorrections(NativeArray<Entity> households)
        {
            for (int i = 0; i < households.Length; i++)
            {
                Entity household = households[i];
                if (_incomeCorrectionMembers.Contains(household)) continue;
                if (!_hostIdsByHousehold.TryGetValue(household, out ulong householdId) ||
                    !_desiredHouseholdEconomies.ContainsKey(householdId)) continue;
                _incomeCorrectionMembers.Add(household);
                _incomeCorrectionQueue.Enqueue(household);
            }
        }

        /// <summary>Right after the local pass that recomputes income from this peer's employment graph.</summary>
        internal void CorrectHouseholdIncomeAfterLocalUpdate()
        {
            MultiplayerService service = Mod.Service;
            if (service == null || !service.SimulationSyncReady ||
                service.Session.Role != SessionRole.Client)
            {
                ClearHouseholdIncomeCorrections();
                return;
            }

            int examine = _incomeCorrectionQueue.Count < MaxHouseholdIncomeCorrectionsPerFrame
                ? _incomeCorrectionQueue.Count : MaxHouseholdIncomeCorrectionsPerFrame;
            for (int i = 0; i < examine; i++)
            {
                if (!_incomeCorrectionQueue.TryDequeue(out Entity household)) break;
                _incomeCorrectionMembers.Remove(household);
                if (!TryResolveCorrectionTarget(household, out DesiredHouseholdEconomy wanted,
                    out Entity property)) continue;

                Household data = EntityManager.GetComponentData<Household>(household);
                if (data.m_Income == wanted.Income) continue;
                data.m_Income = wanted.Income;
                EntityManager.SetComponentData(household, data);
                _incomeCorrections++;
            }
            if (_incomeCorrectionQueue.Count != 0)
                _incomeDeferred += _incomeCorrectionQueue.Count;
        }

        internal void ClearHouseholdIncomeCorrections()
        {
            while (_incomeCorrectionQueue.TryDequeue(out Entity discarded)) { }
            _incomeCorrectionMembers.Clear();
        }

        private bool ApplyHouseholdEconomy(Entity household, Entity property,
            DesiredHouseholdEconomy wanted)
        {
            bool changed = false;
            Household data = EntityManager.GetComponentData<Household>(household);
            if (data.m_Resources != wanted.Savings ||
                data.m_ConsumptionPerDay != wanted.ConsumptionPerDay ||
                data.m_ShoppedValuePerDay != wanted.ShoppedValuePerDay ||
                data.m_ShoppedValueLastDay != wanted.ShoppedValueLastDay ||
                data.m_LastDayFrameIndex != wanted.LastDayFrameIndex ||
                data.m_Income != wanted.Income ||
                data.m_MoneySpendOnBuildingLevelingLastDay !=
                wanted.MoneySpentOnBuildingLevelingLastDay)
            {
                data.m_Resources = wanted.Savings;
                data.m_ConsumptionPerDay = wanted.ConsumptionPerDay;
                data.m_ShoppedValuePerDay = wanted.ShoppedValuePerDay;
                data.m_ShoppedValueLastDay = wanted.ShoppedValueLastDay;
                data.m_LastDayFrameIndex = wanted.LastDayFrameIndex;
                data.m_Income = wanted.Income;
                data.m_MoneySpendOnBuildingLevelingLastDay =
                    wanted.MoneySpentOnBuildingLevelingLastDay;
                EntityManager.SetComponentData(household, data);
                changed = true;
            }

            if (EntityManager.HasBuffer<Resources>(household))
            {
                DynamicBuffer<Resources> resources = EntityManager.GetBuffer<Resources>(household, true);
                if (EconomyUtils.GetResources(Resource.Money, resources) != wanted.Money)
                {
                    EconomyUtils.SetResources(Resource.Money,
                        EntityManager.GetBuffer<Resources>(household), wanted.Money);
                    changed = true;
                }
            }

            bool hasTaxPayer = EntityManager.HasComponent<TaxPayer>(household);
            if (wanted.HasTaxPayer)
            {
                var taxPayer = new TaxPayer
                {
                    m_UntaxedIncome = wanted.UntaxedIncome,
                    m_AverageTaxRate = wanted.AverageTaxRate,
                    m_AverageTaxPaid = wanted.AverageTaxPaid,
                };
                if (!hasTaxPayer)
                {
                    EntityManager.AddComponentData(household, taxPayer);
                    changed = true;
                }
                else
                {
                    TaxPayer current = EntityManager.GetComponentData<TaxPayer>(household);
                    if (current.m_UntaxedIncome != taxPayer.m_UntaxedIncome ||
                        current.m_AverageTaxRate != taxPayer.m_AverageTaxRate ||
                        current.m_AverageTaxPaid != taxPayer.m_AverageTaxPaid)
                    {
                        EntityManager.SetComponentData(household, taxPayer);
                        changed = true;
                    }
                }
            }
            else if (hasTaxPayer)
            {
                EntityManager.RemoveComponent<TaxPayer>(household);
                changed = true;
            }

            if (EntityManager.HasComponent<PropertyRenter>(household))
            {
                PropertyRenter renter = EntityManager.GetComponentData<PropertyRenter>(household);
                if (renter.m_Property == property && renter.m_Rent != wanted.Rent)
                {
                    renter.m_Rent = wanted.Rent;
                    EntityManager.SetComponentData(household, renter);
                    changed = true;
                }
            }
            return changed;
        }
    }
}
