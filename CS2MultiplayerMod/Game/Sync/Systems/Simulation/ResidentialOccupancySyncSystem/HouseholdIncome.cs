using System.Reflection;
using System.Runtime.InteropServices;
using Game.Citizens;
using Unity.Collections.LowLevel.Unsafe;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    // The household income field carries a different name on game builds before 1.6.2. Naming either one
    // directly faults on the other build, so it is reached through a same-layout view; never read
    // Household.m_Income.
    internal static class HouseholdIncome
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct Layout
        {
            public byte Flags;
            public int Resources;
            public short ConsumptionPerDay;
            public uint ShoppedValuePerDay;
            public uint ShoppedValueLastDay;
            public uint LastDayFrameIndex;
            public int Income;
            public int MoneySpendOnBuildingLevelingLastDay;
        }

        private static readonly bool Available = Resolve();

        private static bool Resolve()
        {
            FieldInfo field = typeof(Household).GetField("m_Income") ??
                              typeof(Household).GetField("m_SalaryLastDay");
            return field != null && field.FieldType == typeof(int) &&
                   UnsafeUtility.SizeOf<Household>() == UnsafeUtility.SizeOf<Layout>() &&
                   UnsafeUtility.GetFieldOffset(field) ==
                   UnsafeUtility.GetFieldOffset(typeof(Layout).GetField(nameof(Layout.Income)));
        }

        public static int Get(ref Household household) =>
            Available ? UnsafeUtility.As<Household, Layout>(ref household).Income : 0;

        public static void Set(ref Household household, int value)
        {
            if (Available) UnsafeUtility.As<Household, Layout>(ref household).Income = value;
        }
    }
}
