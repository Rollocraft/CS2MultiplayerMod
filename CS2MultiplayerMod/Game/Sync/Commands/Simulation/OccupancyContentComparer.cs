using System;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>Compares the host's absolute property data without its transport revision.</summary>
    internal static class OccupancyContentComparer
    {
        internal static bool Same(in OccupancyProperty left, in OccupancyProperty right)
        {
            if (!String.Equals(left.PrefabName, right.PrefabName, StringComparison.Ordinal) ||
                left.AnchorX != right.AnchorX || left.AnchorY != right.AnchorY ||
                left.AnchorZ != right.AnchorZ ||
                left.ConstructionSpeed != right.ConstructionSpeed ||
                left.HasElectricityConsumer != right.HasElectricityConsumer ||
                left.ElectricityFulfilledConsumption != right.ElectricityFulfilledConsumption ||
                left.HasWaterConsumer != right.HasWaterConsumer ||
                left.WaterFulfilledFresh != right.WaterFulfilledFresh ||
                left.WaterFulfilledSewage != right.WaterFulfilledSewage ||
                !SameLength(left.Households, right.Households)) return false;

            for (int i = 0; i < left.Households.Length; i++)
                if (!Same(in left.Households[i], in right.Households[i])) return false;
            return true;
        }

        private static bool Same(in OccupancyHousehold left, in OccupancyHousehold right)
        {
            if (left.HouseholdId != right.HouseholdId ||
                !String.Equals(left.PrefabName, right.PrefabName, StringComparison.Ordinal) ||
                left.Flags != right.Flags || left.Departing != right.Departing ||
                left.Rent != right.Rent || left.Savings != right.Savings ||
                left.Money != right.Money || left.HasTaxPayer != right.HasTaxPayer ||
                left.UntaxedIncome != right.UntaxedIncome ||
                left.AverageTaxRate != right.AverageTaxRate ||
                left.AverageTaxPaid != right.AverageTaxPaid ||
                left.Income != right.Income ||
                left.ConsumptionPerDay != right.ConsumptionPerDay ||
                left.ShoppedValuePerDay != right.ShoppedValuePerDay ||
                left.ShoppedValueLastDay != right.ShoppedValueLastDay ||
                left.LastDayFrameIndex != right.LastDayFrameIndex ||
                left.MoneySpentOnBuildingLevelingLastDay !=
                    right.MoneySpentOnBuildingLevelingLastDay ||
                !Same(left.NameIndices, right.NameIndices) ||
                !Same(left.Pets, right.Pets) ||
                !Same(left.OwnedVehicles, right.OwnedVehicles) ||
                !SameLength(left.Citizens, right.Citizens)) return false;

            for (int i = 0; i < left.Citizens.Length; i++)
                if (!Same(in left.Citizens[i], in right.Citizens[i])) return false;
            return true;
        }

        private static bool Same(in OccupancyCitizen left, in OccupancyCitizen right) =>
            left.CitizenId == right.CitizenId &&
            String.Equals(left.PrefabName, right.PrefabName, StringComparison.Ordinal) &&
            left.State == right.State && left.PseudoRandom == right.PseudoRandom &&
            left.BirthDay == right.BirthDay && left.Health == right.Health &&
            left.WellBeing == right.WellBeing &&
            left.HealthProblem == right.HealthProblem &&
            left.Employment == right.Employment &&
            left.UnemploymentCounter == right.UnemploymentCounter &&
            Same(left.NameIndices, right.NameIndices);

        /// <summary>
        /// What a full household reconcile acts on, minus the economy and resident vitals a client writes
        /// directly; equal hashes let it skip the rest.
        /// </summary>
        internal static int StructureHash(in OccupancyHousehold household)
        {
            unchecked
            {
                int hash = Mix((int)2166136261, household.HouseholdId.GetHashCode());
                hash = Mix(hash, Text(household.PrefabName));
                hash = Mix(hash, household.Flags);
                hash = Mix(hash, household.Departing ? 1 : 0);
                hash = Mix(hash, household.NameIndices);
                hash = Mix(hash, household.Pets);
                hash = Mix(hash, household.OwnedVehicles);
                OccupancyCitizen[] citizens = household.Citizens;
                int count = citizens == null ? -1 : citizens.Length;
                hash = Mix(hash, count);
                for (int i = 0; i < count; i++)
                {
                    hash = Mix(hash, citizens[i].CitizenId.GetHashCode());
                    hash = Mix(hash, Text(citizens[i].PrefabName));
                    hash = Mix(hash, citizens[i].State);
                    hash = Mix(hash, citizens[i].PseudoRandom);
                    hash = Mix(hash, citizens[i].BirthDay);
                    hash = Mix(hash, citizens[i].HealthProblem);
                    hash = Mix(hash, citizens[i].Employment);
                    hash = Mix(hash, citizens[i].NameIndices);
                }
                return hash;
            }
        }

        private static int Mix(int hash, int value) => unchecked((hash ^ value) * 16777619);

        private static int Text(string value) => value == null ? -1 : value.GetHashCode();

        private static int Mix(int hash, int[] values)
        {
            if (values == null) return Mix(hash, -1);
            hash = Mix(hash, values.Length);
            for (int i = 0; i < values.Length; i++) hash = Mix(hash, values[i]);
            return hash;
        }

        private static int Mix(int hash, string[] values)
        {
            if (values == null) return Mix(hash, -1);
            hash = Mix(hash, values.Length);
            for (int i = 0; i < values.Length; i++) hash = Mix(hash, Text(values[i]));
            return hash;
        }

        private static bool Same(int[] left, int[] right)
        {
            if (!SameLength(left, right)) return false;
            for (int i = 0; i < left.Length; i++)
                if (left[i] != right[i]) return false;
            return true;
        }

        private static bool Same(string[] left, string[] right)
        {
            if (!SameLength(left, right)) return false;
            for (int i = 0; i < left.Length; i++)
                if (!String.Equals(left[i], right[i], StringComparison.Ordinal)) return false;
            return true;
        }

        private static bool SameLength<T>(T[] left, T[] right) =>
            left != null && right != null && left.Length == right.Length;
    }
}
