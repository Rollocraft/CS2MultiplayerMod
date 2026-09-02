using System;
using System.Collections.Generic;
using System.Text;
using CS2MultiplayerMod.Core.Protocol;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    // What a page has to satisfy before any of it is applied. Every bound is checked on read, on a
    // body a peer supplied, so a malformed or hostile page is refused at the decoder rather than
    // part-applied - and every name index is checked against the table it claims to index.
    //
    // The page-local name table lives here too: prefab names repeat heavily inside a page, so they
    // are interned once and referenced by index.
    public sealed partial class ResidentialOccupancySnapshot
    {
        /// <summary>
        /// Shared validation for the codec and for host capture. Capture calls it before an entry
        /// reaches a page, so one broken local prefab or transform is skipped rather than making
        /// <see cref="Write"/> throw — city-state capture is shared, and a throw there would
        /// suppress money, clock, demand and every other channel in the same snapshot.
        /// </summary>
        public static bool IsValidProperty(OccupancyProperty property)
        {
            if (!IsValidName(property.PrefabName)) return false;
            if (property.Revision == 0) return false;
            if (!IsValidCoordinate(property.AnchorX) || !IsValidCoordinate(property.AnchorY) ||
                !IsValidCoordinate(property.AnchorZ)) return false;
            if (property.ElectricityFulfilledConsumption < 0 ||
                property.ElectricityFulfilledConsumption > MaxUtilityConsumption ||
                (!property.HasElectricityConsumer &&
                 property.ElectricityFulfilledConsumption != 0)) return false;
            if (property.WaterFulfilledFresh < 0 ||
                property.WaterFulfilledFresh > MaxUtilityConsumption ||
                property.WaterFulfilledSewage < 0 ||
                property.WaterFulfilledSewage > MaxUtilityConsumption ||
                (!property.HasWaterConsumer &&
                 (property.WaterFulfilledFresh != 0 ||
                  property.WaterFulfilledSewage != 0))) return false;
            if (property.Households == null ||
                property.Households.Length > MaxHouseholdsPerProperty) return false;
            var householdIds = new HashSet<ulong>();
            var citizenIds = new HashSet<ulong>();
            for (int h = 0; h < property.Households.Length; h++)
            {
                OccupancyHousehold household = property.Households[h];
                if (household.HouseholdId == 0 || !householdIds.Add(household.HouseholdId))
                    return false;
                if (!IsValidName(household.PrefabName)) return false;
                if (!IsValidNameIndices(household.NameIndices)) return false;
                if (household.Rent < 0 || household.Rent > MaxRent) return false;
                if (household.Savings < -MaxMoney || household.Savings > MaxMoney) return false;
                if (household.Money < -MaxMoney || household.Money > MaxMoney) return false;
                if (household.UntaxedIncome < -MaxMoney ||
                    household.UntaxedIncome > MaxMoney ||
                    household.AverageTaxRate < -MaxMoney ||
                    household.AverageTaxRate > MaxMoney ||
                    household.AverageTaxPaid < -MaxMoney ||
                    household.AverageTaxPaid > MaxMoney ||
                    (!household.HasTaxPayer &&
                     (household.UntaxedIncome != 0 || household.AverageTaxRate != 0 ||
                      household.AverageTaxPaid != 0))) return false;
                if (household.SalaryLastDay < -MaxMoney ||
                    household.SalaryLastDay > MaxMoney) return false;
                if (household.MoneySpentOnBuildingLevelingLastDay < -MaxMoney ||
                    household.MoneySpentOnBuildingLevelingLastDay > MaxMoney) return false;
                if (household.Citizens == null ||
                    household.Citizens.Length > MaxCitizensPerHousehold) return false;
                for (int c = 0; c < household.Citizens.Length; c++)
                {
                    OccupancyCitizen citizen = household.Citizens[c];
                    if (citizen.CitizenId == 0 || !citizenIds.Add(citizen.CitizenId)) return false;
                    if (!IsValidName(citizen.PrefabName)) return false;
                    if (!IsValidNameIndices(citizen.NameIndices)) return false;
                    if (!citizen.HasHealthProblem &&
                        (citizen.HealthProblemFlags != 0)) return false;
                    if (citizen.WorkerLevel > MaxWorkerLevel) return false;
                    if (citizen.UnemploymentCounter < 0 ||
                        citizen.UnemploymentCounter > MaxMoney) return false;
                }
                if (household.Pets == null || household.Pets.Length > MaxPetsPerHousehold)
                    return false;
                for (int p = 0; p < household.Pets.Length; p++)
                    if (!IsValidName(household.Pets[p])) return false;
                if (household.OwnedVehicles == null ||
                    household.OwnedVehicles.Length > MaxVehiclesPerHousehold) return false;
                for (int v = 0; v < household.OwnedVehicles.Length; v++)
                    if (!IsValidName(household.OwnedVehicles[v])) return false;
            }
            return true;
        }

        private static void Validate(OccupancyProperty property)
        {
            if (!IsValidProperty(property))
                throw new ProtocolException("Invalid occupancy property entry.");
        }

        private static void Intern(NameTable names, OccupancyProperty property)
        {
            names.Add(property.PrefabName);
            if (property.Households == null) return;
            for (int h = 0; h < property.Households.Length; h++)
            {
                OccupancyHousehold household = property.Households[h];
                names.Add(household.PrefabName);
                if (household.Citizens != null)
                    for (int c = 0; c < household.Citizens.Length; c++)
                        names.Add(household.Citizens[c].PrefabName);
                if (household.Pets != null)
                    for (int p = 0; p < household.Pets.Length; p++) names.Add(household.Pets[p]);
                if (household.OwnedVehicles != null)
                    for (int v = 0; v < household.OwnedVehicles.Length; v++)
                        names.Add(household.OwnedVehicles[v]);
            }
        }

        /// <summary>
        /// A random name slot is a plain index into a localized name list, or -1 for "this prefab
        /// has no list". Both are drawn per machine from its own clock, which is why they have to
        /// travel: without them the same family has a different surname on every peer.
        /// </summary>
        private static bool IsValidNameIndices(int[] indices)
        {
            if (indices == null || indices.Length > MaxNameIndices) return false;
            for (int i = 0; i < indices.Length; i++)
                if (indices[i] < -1) return false;
            return true;
        }

        private static void WriteNameIndices(NetworkWriter writer, int[] indices)
        {
            writer.WriteByte((byte)indices.Length);
            for (int i = 0; i < indices.Length; i++) writer.WriteInt(indices[i]);
        }

        private static int[] ReadNameIndices(NetworkReader reader)
        {
            int count = reader.ReadByte();
            if (count > MaxNameIndices)
                throw new ProtocolException("Occupancy name-index count exceeds its cap.");
            if ((long)count * 4 > reader.Remaining)
                throw new ProtocolException("Occupancy name indices do not fit the payload.");
            var indices = new int[count];
            for (int i = 0; i < count; i++)
            {
                indices[i] = reader.ReadInt();
                if (indices[i] < -1)
                    throw new ProtocolException("Occupancy name index is below its floor.");
            }
            return indices;
        }

        private static int ReadNameIndex(NetworkReader reader, int nameCount)
        {
            int index = reader.ReadShort();
            if (index < 0 || index >= nameCount)
                throw new ProtocolException("Occupancy name index outside the page's table.");
            return index;
        }

        private static bool ReadStrictBool(NetworkReader reader)
        {
            byte value = reader.ReadByte();
            if (value > 1) throw new ProtocolException("Invalid occupancy page flag.");
            return value != 0;
        }

        private static bool IsValidName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > WireGuard.MaxNameLength) return false;
            for (int i = 0; i < name.Length; i++)
                if (char.IsControl(name[i])) return false;
            return true;
        }

        private static bool IsValidCoordinate(float value) =>
            !float.IsNaN(value) && !float.IsInfinity(value) &&
            value >= -WireGuard.MaxCoordinate && value <= WireGuard.MaxCoordinate;

        private sealed class NameTable
        {
            private readonly Dictionary<string, int> _index = new Dictionary<string, int>(
                StringComparer.Ordinal);
            public readonly List<string> Ordered = new List<string>();

            public int Count => Ordered.Count;

            public void Add(string name)
            {
                if (string.IsNullOrEmpty(name) || _index.ContainsKey(name)) return;
                _index.Add(name, Ordered.Count);
                Ordered.Add(name);
            }

            public int IndexOf(string name)
            {
                int index;
                if (!_index.TryGetValue(name, out index))
                    throw new ProtocolException("Occupancy name missing from its own page table.");
                return index;
            }
        }
    }
}
