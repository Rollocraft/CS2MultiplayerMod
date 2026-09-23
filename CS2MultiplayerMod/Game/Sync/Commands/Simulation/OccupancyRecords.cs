namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>One residential property and everyone the host has living in it.</summary>
    public struct OccupancyProperty
    {
        public string PrefabName;
        public float AnchorX;
        public float AnchorY;
        public float AnchorZ;

        /// <summary>Host-monotonic roster version; only used to reject an older roster.</summary>
        public ulong Revision;

        /// <summary>
        /// Zero when finished, else the host's build rate. The rate is drawn per machine, so without it peers
        /// finish the same house at different times.
        /// </summary>
        public byte ConstructionSpeed;

        /// <summary>
        /// Fulfilled utility quantities behind the household fees; each machine's utility graph can be a
        /// frame or rounding step apart. Connectivity and demand stay native.
        /// </summary>
        public bool HasElectricityConsumer;
        public int ElectricityFulfilledConsumption;
        public bool HasWaterConsumer;
        public int WaterFulfilledFresh;
        public int WaterFulfilledSewage;

        public OccupancyHousehold[] Households;

        /// <summary>The portable property identity shared with rent and growable realization.</summary>
        public PropertyIdentity Identity =>
            new PropertyIdentity(PrefabName, AnchorX, AnchorY, AnchorZ);
    }

    /// <summary>One household in a property, identified by a host-issued world-scoped id.</summary>
    public struct OccupancyHousehold
    {
        public ulong HouseholdId;
        public string PrefabName;
        public byte Flags;

        /// <summary>
        /// Explicit host departure. Absence from a page is not one: the destination page may be dropped or
        /// unresolved.
        /// </summary>
        public bool Departing;

        public int Rent;

        /// <summary><see cref="Game.Citizens.Household.m_Resources"/>: accumulated savings.</summary>
        public int Savings;

        /// <summary>The money resource in the household's own resource buffer.</summary>
        public int Money;

        /// <summary>Rolling tax state; the native tax pass accumulates per family from local history.</summary>
        public bool HasTaxPayer;
        public int UntaxedIncome;
        public int AverageTaxRate;
        public int AverageTaxPaid;

        /// <summary>Daily household income from the host's household pass (formerly SalaryLastDay, same slot).</summary>
        public int Income;

        /// <summary>Consumption target produced by the host's household behavior pass.</summary>
        public short ConsumptionPerDay;

        public uint ShoppedValuePerDay;
        public uint ShoppedValueLastDay;
        public uint LastDayFrameIndex;

        /// <summary>Last day's signed expenditure on building leveling.</summary>
        public int MoneySpentOnBuildingLevelingLastDay;

        /// <summary>Random name slots; the first is the family surname.</summary>
        public int[] NameIndices;

        public OccupancyCitizen[] Citizens;
        public string[] Pets;

        /// <summary>
        /// Live personal vehicles. Synced households skip the random-arrival initializer that would create
        /// them, so they are realized explicitly.
        /// </summary>
        public string[] OwnedVehicles;
    }

    /// <summary>
    /// One resident; the stable id stops a same-sized roster from reusing the wrong citizen. Age,
    /// education and gender live in the flag word.
    /// </summary>
    public struct OccupancyCitizen
    {
        public ulong CitizenId;
        public string PrefabName;
        public short State;
        public ushort PseudoRandom;
        public short BirthDay;
        public byte Health;
        public byte WellBeing;

        /// <summary>
        /// Bit 7: <c>HealthProblem</c> present; bits 0-6: its flags. Deaths come from a per-world random
        /// stream and cannot be inferred.
        /// </summary>
        public byte HealthProblem;

        /// <summary>Bit 0: holds a job. Bits 4-7: wage level.</summary>
        public byte Employment;

        /// <summary>Frames of unemployment used by the benefit branch of household income.</summary>
        public int UnemploymentCounter;

        /// <summary>Random name slots; the first is this person's first name.</summary>
        public int[] NameIndices;

        public bool Employed => (Employment & 1) != 0;
        public byte WorkerLevel => (byte)((Employment >> 4) & 0xF);
        public bool HasHealthProblem => (HealthProblem & 0x80) != 0;
        public byte HealthProblemFlags => (byte)(HealthProblem & 0x7F);

        public static byte PackEmployment(bool employed, byte level) =>
            (byte)((employed ? 1 : 0) | ((level & 0xF) << 4));

        public static byte PackHealthProblem(bool present, byte flags) =>
            (byte)((present ? 0x80 : 0) | (flags & 0x7F));
    }

    /// <summary>A repeated, revisioned departure tombstone, independent of any property roster.</summary>
    public struct OccupancyDeparture
    {
        public ulong HouseholdId;
        public ulong Revision;

        /// <summary>No property now: the client drops the renter link but keeps the family for a later destination.</summary>
        public bool Unhoused;
    }

    /// <summary>
    /// A person's death or emigration tombstone; a later, higher-revision location still wins when the
    /// citizen actually moved.
    /// </summary>
    public struct OccupancyCitizenDeparture
    {
        public ulong CitizenId;
        public ulong Revision;
    }
}
