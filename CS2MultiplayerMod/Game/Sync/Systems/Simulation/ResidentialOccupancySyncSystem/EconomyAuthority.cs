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
            public PropertyRentIdentity PropertyIdentity;
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
            public int SalaryLastDay;
            public int MoneySpentOnBuildingLevelingLastDay;

            public static DesiredHouseholdEconomy From(OccupancyHousehold household,
                PropertyRentIdentity propertyIdentity, ulong revision) =>
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
                    SalaryLastDay = household.SalaryLastDay,
                    MoneySpentOnBuildingLevelingLastDay =
                        household.MoneySpentOnBuildingLevelingLastDay,
                };
        }

        private readonly Dictionary<ulong, DesiredHouseholdEconomy> _desiredHouseholdEconomies =
            new Dictionary<ulong, DesiredHouseholdEconomy>();

        /// <summary>
        /// Households corrected in one simulation frame. See the ceilings on the rolling scans in
        /// <see cref="ResidentialOccupancySyncSystem"/>; this one matters most because it is the
        /// only occupancy pass that runs at full simulation rate rather than on the wide interval.
        /// </summary>
        private const int MaxHouseholdEconomyCorrectionsPerFrame = 512;

        // Changed-version queries advance their LastSystemVersion after one update. Keeping only a
        // cursor into that update's temporary array meant the unvisited suffix was never returned
        // again, which permanently starved families near the end of a large residential chunk.
        // Retain every changed entity until its bounded correction has actually run.
        private readonly ConcurrentQueue<Entity> _economyCorrectionQueue =
            new ConcurrentQueue<Entity>();
        private readonly HashSet<Entity> _economyCorrectionMembers = new HashSet<Entity>();

        private void ObserveDesiredHouseholdEconomy(ulong householdId,
            PropertyRentIdentity propertyIdentity, ulong revision, OccupancyHousehold household)
        {
            DesiredHouseholdEconomy existing;
            if (householdId == 0 ||
                (_desiredHouseholdEconomies.TryGetValue(householdId, out existing) &&
                 revision <= existing.Revision)) return;
            _desiredHouseholdEconomies[householdId] =
                DesiredHouseholdEconomy.From(household, propertyIdentity, revision);
        }

        private void ForgetDesiredHouseholdEconomy(ulong householdId, ulong revision)
        {
            DesiredHouseholdEconomy existing;
            if (householdId == 0 ||
                !_desiredHouseholdEconomies.TryGetValue(householdId, out existing) ||
                revision <= existing.Revision) return;
            _desiredHouseholdEconomies.Remove(householdId);
        }

        /// <summary>
        /// Asked before the correction boundary builds its array of changed households. That
        /// boundary runs every simulation frame; on a host, and on a client that has not received
        /// a roster yet, there is nothing for it to correct.
        /// </summary>
        internal bool WantsHouseholdEconomyCorrection
        {
            get
            {
                MultiplayerService service = Mod.Service;
                return service != null && service.GameplaySyncReady &&
                       service.Session.Role == SessionRole.Client &&
                       _desiredHouseholdEconomies.Count != 0;
            }
        }

        /// <summary>
        /// Retain every entity returned by a changed-version query before that query advances its
        /// version. Duplicate writes by multiple native systems coalesce while the entity waits.
        /// </summary>
        internal void QueueHouseholdEconomyCorrections(NativeArray<Entity> households)
        {
            for (int i = 0; i < households.Length; i++)
            {
                Entity household = households[i];
                // The filter is on Household and Resources, which every local economy writer
                // touches, so this array arrives holding most of the city's families - while only
                // the ones a host page actually named can be corrected. Dropping the rest here
                // rather than at the drain is what keeps the queue bounded: it stood at ~83,000
                // entries against a 512-per-frame drain, so a correction that was enqueued had
                // minutes of unrelated traffic in front of it. Two plain dictionary probes, no ECS
                // access - the drain still does the full liveness-checked binding lookup.
                ulong householdId;
                if (!_hostIdsByHousehold.TryGetValue(household, out householdId) ||
                    !_desiredHouseholdEconomies.ContainsKey(householdId)) continue;
                if (_economyCorrectionMembers.Add(household))
                    _economyCorrectionQueue.Enqueue(household);
            }
        }

        /// <summary>
        /// Restore host scalars for a bounded set of households touched by local economy systems.
        /// The household's current property and the latest identity observation must agree before
        /// any value is written, so a delayed source-property page cannot affect a move destination.
        /// </summary>
        internal void CorrectHouseholdEconomyAfterLocalUpdate()
        {
            MultiplayerService service = Mod.Service;
            if (service == null || !service.GameplaySyncReady ||
                service.Session.Role != SessionRole.Client)
            {
                ClearHouseholdEconomyCorrections();
                return;
            }

            int examine = _economyCorrectionQueue.Count < MaxHouseholdEconomyCorrectionsPerFrame
                ? _economyCorrectionQueue.Count : MaxHouseholdEconomyCorrectionsPerFrame;
            for (int i = 0; i < examine; i++)
            {
                Entity household;
                if (!_economyCorrectionQueue.TryDequeue(out household)) break;
                _economyCorrectionMembers.Remove(household);
                ulong householdId;
                DesiredHouseholdEconomy wanted;
                if (!TryGetBoundHouseholdId(household, out householdId) ||
                    !_desiredHouseholdEconomies.TryGetValue(householdId, out wanted) ||
                    !EntityManager.HasComponent<PropertyRenter>(household)) continue;

                PropertyRenter renter = EntityManager.GetComponentData<PropertyRenter>(household);
                Entity property = renter.m_Property;
                // One property-identity lookup, not two: the desired location and the page the
                // values came from are both compared against this same local identity.
                PropertyRentIdentity identity, desired;
                if (!TryGetPropertyIdentity(property, out identity) ||
                    !TryGetDesiredPropertyIdentity(householdId, out desired) ||
                    !desired.Equals(identity) ||
                    !wanted.PropertyIdentity.Equals(identity)) continue;

                if (ApplyHouseholdEconomy(household, property, wanted))
                    _economyCorrections++;
            }
            if (_economyCorrectionQueue.Count != 0)
                _economyDeferred += _economyCorrectionQueue.Count;
        }

        internal void ClearHouseholdEconomyCorrections()
        {
            Entity discarded;
            while (_economyCorrectionQueue.TryDequeue(out discarded)) { }
            _economyCorrectionMembers.Clear();
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
                data.m_SalaryLastDay != wanted.SalaryLastDay ||
                data.m_MoneySpendOnBuildingLevelingLastDay !=
                wanted.MoneySpentOnBuildingLevelingLastDay)
            {
                data.m_Resources = wanted.Savings;
                data.m_ConsumptionPerDay = wanted.ConsumptionPerDay;
                data.m_ShoppedValuePerDay = wanted.ShoppedValuePerDay;
                data.m_ShoppedValueLastDay = wanted.ShoppedValueLastDay;
                data.m_LastDayFrameIndex = wanted.LastDayFrameIndex;
                data.m_SalaryLastDay = wanted.SalaryLastDay;
                data.m_MoneySpendOnBuildingLevelingLastDay =
                    wanted.MoneySpentOnBuildingLevelingLastDay;
                EntityManager.SetComponentData(household, data);
                changed = true;
            }

            if (EntityManager.HasBuffer<Resources>(household))
            {
                DynamicBuffer<Resources> resources = EntityManager.GetBuffer<Resources>(household);
                if (EconomyUtils.GetResources(Resource.Money, resources) != wanted.Money)
                {
                    EconomyUtils.SetResources(Resource.Money, resources, wanted.Money);
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
