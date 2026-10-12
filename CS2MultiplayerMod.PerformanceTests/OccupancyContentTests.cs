using System;
using CS2MultiplayerMod.Game.Sync.Commands;

internal static class OccupancyContentTests
{
    internal static void Run(Action<bool, string> check)
    {
        var citizen = new OccupancyCitizen
        {
            CitizenId = 17, PrefabName = "Citizen", State = 3, PseudoRandom = 22,
            BirthDay = 9, Health = 52, WellBeing = 61, HealthProblem = 1,
            Employment = 17, UnemploymentCounter = 4, NameIndices = new[] { 5 },
        };
        var household = new OccupancyHousehold
        {
            HouseholdId = 8, PrefabName = "Family", Flags = 1, Rent = 12,
            Savings = 34, Money = 56, HasTaxPayer = true, UntaxedIncome = 7,
            AverageTaxRate = 8, AverageTaxPaid = 9, Income = 10,
            ConsumptionPerDay = 11, ShoppedValuePerDay = 12,
            ShoppedValueLastDay = 13, LastDayFrameIndex = 14,
            MoneySpentOnBuildingLevelingLastDay = 15, NameIndices = new[] { 2 },
            Citizens = new[] { citizen }, Pets = new[] { "Dog" },
            OwnedVehicles = new[] { "Car" },
        };
        var original = new OccupancyProperty
        {
            PrefabName = "House", AnchorX = 1, AnchorY = 2, AnchorZ = 3,
            Revision = 1, ConstructionSpeed = 2, HasElectricityConsumer = true,
            ElectricityFulfilledConsumption = 3, HasWaterConsumer = true,
            WaterFulfilledFresh = 4, WaterFulfilledSewage = 5,
            Households = new[] { household },
        };
        var same = original;
        same.Revision = 900;
        check(OccupancyContentComparer.Same(in original, in same),
            "a new wire revision alone must not cause an apply");

        same.AnchorZ++;
        check(!OccupancyContentComparer.Same(in original, in same),
            "a different house anchor must apply");
        same = original;
        same.WaterFulfilledFresh++;
        check(!OccupancyContentComparer.Same(in original, in same),
            "a changed utility input must apply");
        same = original;
        same.Households = new OccupancyHousehold[0];
        check(!OccupancyContentComparer.Same(in original, in same),
            "a departure must apply");
        same = original;
        same.Households = new[] { household };
        same.Households[0].Savings++;
        check(!OccupancyContentComparer.Same(in original, in same),
            "household economy must apply");
        same.Households[0] = household;
        same.Households[0].Pets = new[] { "Cat" };
        check(!OccupancyContentComparer.Same(in original, in same),
            "pet changes must apply");
        same.Households[0] = household;
        same.Households[0].Citizens = new[] { citizen };
        same.Households[0].Citizens[0].Health++;
        check(!OccupancyContentComparer.Same(in original, in same),
            "citizen health changes must apply");
        same.Households[0].Citizens[0] = citizen;
        same.Households[0].Citizens[0].NameIndices = new[] { 6 };
        check(!OccupancyContentComparer.Same(in original, in same),
            "citizen name changes must apply");

        for (int i = 0; i < 100; i++)
            OccupancyContentComparer.Same(in original, in original);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++)
            if (!OccupancyContentComparer.Same(in original, in original))
                throw new Exception("stable property changed during comparison");
        check(GC.GetAllocatedBytesForCurrentThread() == before,
            "unchanged-property comparisons must not allocate");
    }
}

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    // The production identity lives in the rent codec, which this focused harness does not need.
    public struct PropertyIdentity
    {
        public PropertyIdentity(string prefab, float x, float y, float z) { }
    }
}