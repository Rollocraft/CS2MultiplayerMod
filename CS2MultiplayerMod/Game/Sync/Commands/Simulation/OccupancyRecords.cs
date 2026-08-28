using System;
using System.Collections.Generic;
using System.Text;
using CS2MultiplayerMod.Core.Protocol;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    // The records one occupancy page is made of: a property and the households, people and
    // departures it carries. Plain data - the page that carries them, and the validation each must
    // pass, are in ResidentialOccupancySnapshot.cs.

    /// <summary>One residential property and everyone the host has living in it.</summary>
    public struct OccupancyProperty
    {
        public string PrefabName;
        public float AnchorX;
        public float AnchorY;
        public float AnchorZ;

        /// <summary>
        /// Host-monotonic version of this property's absolute roster. It is opaque to the client
        /// except for rejecting an older roster after a newer one has already been applied.
        /// </summary>
        public ulong Revision;

        /// <summary>
        /// Zero when the host's building is finished; otherwise the build rate its site was given.
        /// That rate is drawn independently on each machine, so without it two peers building the
        /// same house finish it at different times - and a roster that describes a finished
        /// building keeps arriving at a peer that is still a construction site.
        /// </summary>
        public byte ConstructionSpeed;

        public OccupancyHousehold[] Households;

        /// <summary>
        /// The same portable property identity the rent channel and growable realization use:
        /// building entity ids are machine-local, the prefab name and world anchor are not.
        /// </summary>
        public PropertyRentIdentity Identity =>
            new PropertyRentIdentity(PrefabName, AnchorX, AnchorY, AnchorZ);
    }

    /// <summary>One household in a property, identified by a host-issued world-scoped id.</summary>
    public struct OccupancyHousehold
    {
        public ulong HouseholdId;
        public string PrefabName;
        public byte Flags;

        /// <summary>
        /// Explicit host lifecycle decision. Property-page absence alone is not a departure: the
        /// household may have moved to a destination whose page was dropped or is unresolved.
        /// </summary>
        public bool Departing;

        public int Rent;

        /// <summary><see cref="Game.Citizens.Household.m_Resources"/>: accumulated savings.</summary>
        public int Savings;

        /// <summary>The money resource in the household's own resource buffer.</summary>
        public int Money;

        /// <summary>Salary recorded by the host's household behavior pass for the last day.</summary>
        public int SalaryLastDay;

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
        /// Prefabs of the household's live personal vehicles. Synced households deliberately skip
        /// the local random-arrival initializer, so the owned vehicles created by that initializer
        /// have to be realized explicitly on receiving peers.
        /// </summary>
        public string[] OwnedVehicles;
    }

    /// <summary>
    /// One resident. The stable id prevents a same-sized roster replacement from reusing the wrong
    /// local citizen. Age, education and gender live in the citizen's flag word; employment and
    /// unemployment state feed the household-income calculation.
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

        /// <summary>Bit 0: holds a job. Bits 4-7: wage level.</summary>
        public byte Employment;

        /// <summary>Frames of unemployment used by the benefit branch of household income.</summary>
        public int UnemploymentCounter;

        /// <summary>Random name slots; the first is this person's first name.</summary>
        public int[] NameIndices;

        public bool Employed => (Employment & 1) != 0;
        public byte WorkerLevel => (byte)((Employment >> 4) & 0xF);

        public static byte PackEmployment(bool employed, byte level) =>
            (byte)((employed ? 1 : 0) | ((level & 0xF) << 4));
    }

    /// <summary>
    /// A repeated, revisioned host lifecycle tombstone. It is carried independently of a property
    /// roster so coalescing one move-away page cannot leave the client preserving that family.
    /// </summary>
    public struct OccupancyDeparture
    {
        public ulong HouseholdId;
        public ulong Revision;

        /// <summary>
        /// The live household currently has no property. A client releases its old renter link but
        /// preserves the family and identity for a later host-authored destination.
        /// </summary>
        public bool Unhoused;
    }

    /// <summary>
    /// A retained exact-person tombstone. It closes individual death or emigration without
    /// treating absence from one household page as proof of departure; a later, higher-revision
    /// positive location still wins when the citizen actually moved to another household.
    /// </summary>
    public struct OccupancyCitizenDeparture
    {
        public ulong CitizenId;
        public ulong Revision;
    }
}
