using System.Collections.Generic;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Sync.Commands;
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
        /// Restore host scalars for the household chunks changed by local daily-economy systems.
        /// The household's current property and the latest identity observation must agree before
        /// any value is written, so a delayed source-property page cannot affect a move destination.
        /// </summary>
        internal void CorrectHouseholdEconomyAfterLocalUpdate(NativeArray<Entity> households)
        {
            MultiplayerService service = Mod.Service;
            if (service == null || !service.GameplaySyncReady ||
                service.Session.Role != SessionRole.Client) return;

            for (int i = 0; i < households.Length; i++)
            {
                Entity household = households[i];
                ulong householdId;
                DesiredHouseholdEconomy wanted;
                if (!TryGetBoundHouseholdId(household, out householdId) ||
                    !_desiredHouseholdEconomies.TryGetValue(householdId, out wanted) ||
                    !EntityManager.HasComponent<PropertyRenter>(household)) continue;

                PropertyRenter renter = EntityManager.GetComponentData<PropertyRenter>(household);
                Entity property = renter.m_Property;
                PropertyRentIdentity identity;
                if (!IsHouseholdDesiredHere(householdId, property) ||
                    !TryGetPropertyIdentity(property, out identity) ||
                    !wanted.PropertyIdentity.Equals(identity)) continue;

                if (ApplyHouseholdEconomy(household, property, wanted))
                    _economyCorrections++;
            }
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
