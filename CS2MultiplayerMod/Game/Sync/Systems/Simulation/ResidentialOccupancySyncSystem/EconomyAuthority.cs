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

        /// <summary>
        /// Households corrected in one simulation frame. See the ceilings on the rolling scans in
        /// <see cref="ResidentialOccupancySyncSystem"/>; this one matters most because it is the
        /// only occupancy pass that runs at full simulation rate rather than on the wide interval.
        /// </summary>
        private const int MaxHouseholdEconomyCorrectionsPerFrame = 512;

        private int _economyCursor;

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
        /// Restore host scalars for the household chunks changed by local daily-economy systems.
        /// The household's current property and the latest identity observation must agree before
        /// any value is written, so a delayed source-property page cannot affect a move destination.
        /// </summary>
        internal void CorrectHouseholdEconomyAfterLocalUpdate(NativeArray<Entity> households)
        {
            MultiplayerService service = Mod.Service;
            if (service == null || !service.GameplaySyncReady ||
                service.Session.Role != SessionRole.Client) return;

            // This is the one pass that runs every simulation frame over city-scale input: the
            // query hands back every household chunk a local economy writer touched, which in a
            // large city is thousands of families per frame. Correct a bounded window and carry
            // the cursor forward, wrapping, so coverage stays even instead of starving the tail
            // of a stable chunk order. A family that waits is only briefly showing its own
            // locally drifted money; the next roster page for its property restores it anyway.
            int length = households.Length;
            if (length == 0) return;
            int start = _economyCursor;
            if (start >= length) start = 0;
            int examine = length < MaxHouseholdEconomyCorrectionsPerFrame
                ? length : MaxHouseholdEconomyCorrectionsPerFrame;
            if (examine < length) _economyDeferred += length - examine;

            int cursor = start;
            for (int i = 0; i < examine; i++)
            {
                if (cursor >= length) cursor = 0;
                Entity household = households[cursor++];
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
            _economyCursor = cursor >= length ? 0 : cursor;
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
