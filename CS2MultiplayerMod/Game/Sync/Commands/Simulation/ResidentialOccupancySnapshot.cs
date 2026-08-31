using System;
using System.Collections.Generic;
using System.Text;
using CS2MultiplayerMod.Core.Protocol;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>
    /// One bounded page of the host's residential occupancy: for each property it names, the
    /// complete set of households living there and the people in them.
    ///
    /// Every page is an absolute statement about the properties it carries, never a delta. Losing
    /// one delays those properties until the next rolling sweep touches them again; it can never
    /// make a later page unsafe to apply, and no page is ever a reason to reload the world.
    ///
    /// Prefab names repeat heavily inside a page (one house model, one household archetype and two
    /// citizen models can cover a whole street), so names are interned in a page-local table and
    /// referenced by index.
    /// </summary>
    // The page's limits, its contents, and the codec that puts it on the wire.
    //
    // Everything a page must satisfy before it is trusted - and the page-local name table the codec
    // interns through - is in OccupancySnapshotValidation.cs. The records themselves are in
    // OccupancyRecords.cs.
    public sealed partial class ResidentialOccupancySnapshot
    {
        public const int MaxNames = 512;
        public const int MaxProperties = 256;
        public const int MaxHouseholdsPerProperty = byte.MaxValue;
        public const int MaxCitizensPerHousehold = 24;
        public const int MaxPetsPerHousehold = 4;
        public const int MaxVehiclesPerHousehold = 16;
        public const int MaxHouseholdsPerPage = 512;
        public const int MaxCitizensPerPage = MaxHouseholdsPerProperty * MaxCitizensPerHousehold;
        public const int MaxPetsPerPage = MaxHouseholdsPerProperty * MaxPetsPerHousehold;
        public const int MaxVehiclesPerPage = MaxHouseholdsPerPage * MaxVehiclesPerHousehold;
        public const int MaxDeparturesPerPage = 256;
        public const int MaxCitizenDeparturesPerPage = 256;
        public const int MaxPagesPerSweep = short.MaxValue;
        // StateSnapshot has a 256 KiB transport cap. Leave envelope/headroom while allowing one
        // dense residential tower to remain an atomic absolute roster.
        public const int MaxEncodedBytes = 240 * 1024;

        /// <summary>
        /// Random name slots carried per household and per citizen. A household uses one (the
        /// family surname) and a citizen one (their first name); the cap leaves room for prefabs
        /// that declare more without making a malformed page expensive.
        /// </summary>
        public const int MaxNameIndices = 4;

        /// <summary>Far above any plausible in-game rent, still short of overflowing the economy.</summary>
        public const int MaxRent = 100000000;

        /// <summary>
        /// Same idea for a household's savings, cash, and signed daily economy totals.
        /// </summary>
        public const int MaxMoney = 1000000000;

        /// <summary>Highest wage bracket a worker can be paid at.</summary>
        public const int MaxWorkerLevel = 4;

        public uint SweepId;
        public int PageIndex;
        public bool EndOfSweep;

        /// <summary>
        /// True only on an end page whose baseline visited every host property without a capture
        /// skip. A client may prune properties absent from that complete baseline; household and
        /// citizen absence is never inferred across rolling pages and uses explicit tombstones.
        /// </summary>
        public bool SweepComplete;

        /// <summary>
        /// Highest host roster revision issued when this page was closed. It gives an empty,
        /// complete sweep a non-zero ordering point for confirmed departures.
        /// </summary>
        public ulong RevisionWatermark;

        public readonly List<OccupancyDeparture> Departures = new List<OccupancyDeparture>();
        public readonly List<OccupancyCitizenDeparture> CitizenDepartures =
            new List<OccupancyCitizenDeparture>();
        public readonly List<OccupancyProperty> Properties = new List<OccupancyProperty>();

        public void Write(NetworkWriter writer)
        {
            if (writer == null) throw new ArgumentNullException(nameof(writer));
            if (SweepId == 0) throw new ProtocolException("Occupancy sweep id must be non-zero.");
            if (PageIndex < 0 || PageIndex >= MaxPagesPerSweep)
                throw new ProtocolException("Occupancy page index is outside its cap.");
            if (SweepComplete && !EndOfSweep)
                throw new ProtocolException("Only an occupancy end page can complete a sweep.");
            if (RevisionWatermark == 0)
                throw new ProtocolException("Occupancy revision watermark must be non-zero.");
            if (Departures.Count > MaxDeparturesPerPage)
                throw new ProtocolException("Occupancy page exceeds its departure cap.");
            if (CitizenDepartures.Count > MaxCitizenDeparturesPerPage)
                throw new ProtocolException("Occupancy page exceeds its citizen-departure cap.");
            if (Properties.Count > MaxProperties)
                throw new ProtocolException("Occupancy page exceeds its property cap.");

            // Validate the entire absolute page before touching the caller's writer. A channel
            // shares its capture tick with the other city-state channels, so a late duplicate or
            // aggregate-cap failure must not leave a truncated occupancy payload behind.
            var names = new NameTable();
            var identities = new HashSet<PropertyRentIdentity>();
            var householdIds = new HashSet<ulong>();
            var citizenIds = new HashSet<ulong>();
            long encodedBytes = 24L + Departures.Count * 17L +
                                CitizenDepartures.Count * 16L;
            var departureIds = new HashSet<ulong>();
            for (int i = 0; i < Departures.Count; i++)
            {
                OccupancyDeparture departure = Departures[i];
                if (departure.HouseholdId == 0 || departure.Revision == 0 ||
                    departure.Revision > RevisionWatermark ||
                    !departureIds.Add(departure.HouseholdId))
                    throw new ProtocolException("Invalid occupancy departure entry.");
            }
            var departedCitizenIds = new HashSet<ulong>();
            for (int i = 0; i < CitizenDepartures.Count; i++)
            {
                OccupancyCitizenDeparture departure = CitizenDepartures[i];
                if (departure.CitizenId == 0 || departure.Revision == 0 ||
                    departure.Revision > RevisionWatermark ||
                    !departedCitizenIds.Add(departure.CitizenId))
                    throw new ProtocolException("Invalid occupancy citizen-departure entry.");
            }
            int households = 0, citizens = 0, pets = 0, vehicles = 0;
            for (int i = 0; i < Properties.Count; i++)
            {
                OccupancyProperty property = Properties[i];
                Validate(property);
                if (property.Revision > RevisionWatermark)
                    throw new ProtocolException("Occupancy property revision exceeds its page watermark.");
                if (!identities.Add(property.Identity))
                    throw new ProtocolException("Duplicate property identity in occupancy page.");
                households += property.Households.Length;
                if (households > MaxHouseholdsPerPage)
                    throw new ProtocolException("Occupancy page exceeds its household cap.");
                Intern(names, property);
                encodedBytes += 24;
                for (int h = 0; h < property.Households.Length; h++)
                {
                    OccupancyHousehold household = property.Households[h];
                    if (!householdIds.Add(household.HouseholdId))
                        throw new ProtocolException("Duplicate household id in occupancy page.");
                    citizens += household.Citizens.Length;
                    if (citizens > MaxCitizensPerPage)
                        throw new ProtocolException("Occupancy page exceeds its citizen cap.");
                    pets += household.Pets.Length;
                    if (pets > MaxPetsPerPage)
                        throw new ProtocolException("Occupancy page exceeds its pet cap.");
                    vehicles += household.OwnedVehicles.Length;
                    if (vehicles > MaxVehiclesPerPage)
                        throw new ProtocolException("Occupancy page exceeds its vehicle cap.");
                    encodedBytes += 50L + household.NameIndices.Length * 4L +
                                    (household.Pets.Length + household.OwnedVehicles.Length) * 2L;
                    for (int c = 0; c < household.Citizens.Length; c++)
                    {
                        OccupancyCitizen citizen = household.Citizens[c];
                        if (!citizenIds.Add(citizen.CitizenId))
                            throw new ProtocolException("Duplicate citizen id in occupancy page.");
                        encodedBytes += 24L + citizen.NameIndices.Length * 4L;
                    }
                }
            }
            if (names.Count > MaxNames)
                throw new ProtocolException("Occupancy page exceeds its name-table cap.");
            for (int i = 0; i < names.Count; i++)
                encodedBytes += 4L + Encoding.UTF8.GetByteCount(names.Ordered[i]);
            if (encodedBytes > MaxEncodedBytes)
                throw new ProtocolException("Occupancy page exceeds its encoded-byte cap.");

            writer.WriteInt(unchecked((int)SweepId));
            writer.WriteShort((short)PageIndex);
            writer.WriteBool(EndOfSweep);
            writer.WriteBool(SweepComplete);
            writer.WriteLong(unchecked((long)RevisionWatermark));
            writer.WriteShort((short)names.Count);
            for (int i = 0; i < names.Count; i++) writer.WriteString(names.Ordered[i]);

            writer.WriteShort((short)Departures.Count);
            for (int i = 0; i < Departures.Count; i++)
            {
                writer.WriteLong(unchecked((long)Departures[i].HouseholdId));
                writer.WriteLong(unchecked((long)Departures[i].Revision));
                writer.WriteBool(Departures[i].Unhoused);
            }
            writer.WriteShort((short)CitizenDepartures.Count);
            for (int i = 0; i < CitizenDepartures.Count; i++)
            {
                writer.WriteLong(unchecked((long)CitizenDepartures[i].CitizenId));
                writer.WriteLong(unchecked((long)CitizenDepartures[i].Revision));
            }
            writer.WriteShort((short)Properties.Count);
            for (int i = 0; i < Properties.Count; i++)
            {
                OccupancyProperty property = Properties[i];
                writer.WriteShort((short)names.IndexOf(property.PrefabName));
                writer.WriteFloat(property.AnchorX);
                writer.WriteFloat(property.AnchorY);
                writer.WriteFloat(property.AnchorZ);
                writer.WriteLong(unchecked((long)property.Revision));
                writer.WriteByte(property.ConstructionSpeed);
                writer.WriteByte((byte)property.Households.Length);
                for (int h = 0; h < property.Households.Length; h++)
                {
                    OccupancyHousehold household = property.Households[h];
                    writer.WriteLong(unchecked((long)household.HouseholdId));
                    writer.WriteShort((short)names.IndexOf(household.PrefabName));
                    writer.WriteByte(household.Flags);
                    writer.WriteBool(household.Departing);
                    writer.WriteInt(household.Rent);
                    writer.WriteInt(household.Savings);
                    writer.WriteInt(household.Money);
                    writer.WriteInt(household.SalaryLastDay);
                    writer.WriteShort(household.ConsumptionPerDay);
                    writer.WriteInt(unchecked((int)household.ShoppedValuePerDay));
                    writer.WriteInt(unchecked((int)household.ShoppedValueLastDay));
                    writer.WriteInt(unchecked((int)household.LastDayFrameIndex));
                    writer.WriteInt(household.MoneySpentOnBuildingLevelingLastDay);
                    WriteNameIndices(writer, household.NameIndices);
                    writer.WriteByte((byte)household.Citizens.Length);
                    for (int c = 0; c < household.Citizens.Length; c++)
                    {
                        OccupancyCitizen citizen = household.Citizens[c];
                        writer.WriteLong(unchecked((long)citizen.CitizenId));
                        writer.WriteShort((short)names.IndexOf(citizen.PrefabName));
                        writer.WriteShort(citizen.State);
                        writer.WriteShort(unchecked((short)citizen.PseudoRandom));
                        writer.WriteShort(citizen.BirthDay);
                        writer.WriteByte(citizen.Health);
                        writer.WriteByte(citizen.WellBeing);
                        writer.WriteByte(citizen.Employment);
                        writer.WriteInt(citizen.UnemploymentCounter);
                        WriteNameIndices(writer, citizen.NameIndices);
                    }
                    writer.WriteByte((byte)household.Pets.Length);
                    for (int p = 0; p < household.Pets.Length; p++)
                        writer.WriteShort((short)names.IndexOf(household.Pets[p]));
                    writer.WriteByte((byte)household.OwnedVehicles.Length);
                    for (int v = 0; v < household.OwnedVehicles.Length; v++)
                        writer.WriteShort((short)names.IndexOf(household.OwnedVehicles[v]));
                }
            }
        }

        public byte[] Encode()
        {
            var writer = new NetworkWriter(8192);
            Write(writer);
            return writer.ToArray();
        }

        public static ResidentialOccupancySnapshot Read(NetworkReader reader)
        {
            if (reader == null) throw new ArgumentNullException(nameof(reader));
            if (reader.Remaining > MaxEncodedBytes)
                throw new ProtocolException("Occupancy page exceeds its encoded-byte cap.");

            var snapshot = new ResidentialOccupancySnapshot
            {
                SweepId = unchecked((uint)reader.ReadInt()),
                PageIndex = reader.ReadShort(),
                EndOfSweep = ReadStrictBool(reader),
                SweepComplete = ReadStrictBool(reader),
                RevisionWatermark = unchecked((ulong)reader.ReadLong()),
            };
            if (snapshot.SweepId == 0)
                throw new ProtocolException("Occupancy sweep id must be non-zero.");
            if (snapshot.PageIndex < 0 || snapshot.PageIndex >= MaxPagesPerSweep)
                throw new ProtocolException("Occupancy page index is outside its cap.");
            if (snapshot.SweepComplete && !snapshot.EndOfSweep)
                throw new ProtocolException("Only an occupancy end page can complete a sweep.");
            if (snapshot.RevisionWatermark == 0)
                throw new ProtocolException("Occupancy revision watermark must be non-zero.");

            // A name costs at least a two-byte length prefix on the wire.
            int nameCount = WireGuard.ReadCount(reader, 2, MaxNames);
            var names = new string[nameCount];
            for (int i = 0; i < nameCount; i++) names[i] = WireGuard.ReadName(reader);

            int departureCount = WireGuard.ReadCount(reader, 17, MaxDeparturesPerPage);
            var departureIds = new HashSet<ulong>();
            for (int i = 0; i < departureCount; i++)
            {
                var departure = new OccupancyDeparture
                {
                    HouseholdId = unchecked((ulong)reader.ReadLong()),
                    Revision = unchecked((ulong)reader.ReadLong()),
                    Unhoused = ReadStrictBool(reader),
                };
                if (departure.HouseholdId == 0 || departure.Revision == 0 ||
                    departure.Revision > snapshot.RevisionWatermark ||
                    !departureIds.Add(departure.HouseholdId))
                    throw new ProtocolException("Invalid occupancy departure entry.");
                snapshot.Departures.Add(departure);
            }

            int citizenDepartureCount = WireGuard.ReadCount(reader, 16,
                MaxCitizenDeparturesPerPage);
            var departedCitizenIds = new HashSet<ulong>();
            for (int i = 0; i < citizenDepartureCount; i++)
            {
                var departure = new OccupancyCitizenDeparture
                {
                    CitizenId = unchecked((ulong)reader.ReadLong()),
                    Revision = unchecked((ulong)reader.ReadLong()),
                };
                if (departure.CitizenId == 0 || departure.Revision == 0 ||
                    departure.Revision > snapshot.RevisionWatermark ||
                    !departedCitizenIds.Add(departure.CitizenId))
                    throw new ProtocolException("Invalid occupancy citizen-departure entry.");
                snapshot.CitizenDepartures.Add(departure);
            }

            // 24 bytes is the smallest a property with no households can encode to: name index,
            // three coordinates, revision, construction speed, and household count.
            int propertyCount = WireGuard.ReadCount(reader, 24, MaxProperties);
            var identities = new HashSet<PropertyRentIdentity>();
            var householdIds = new HashSet<ulong>();
            var citizenIds = new HashSet<ulong>();
            int households = 0, citizens = 0, pets = 0, vehicles = 0;
            for (int i = 0; i < propertyCount; i++)
            {
                var property = new OccupancyProperty
                {
                    PrefabName = names[ReadNameIndex(reader, nameCount)],
                    AnchorX = WireGuard.ReadCoordinate(reader),
                    AnchorY = WireGuard.ReadCoordinate(reader),
                    AnchorZ = WireGuard.ReadCoordinate(reader),
                    Revision = unchecked((ulong)reader.ReadLong()),
                    ConstructionSpeed = reader.ReadByte(),
                };
                int householdCount = reader.ReadByte();
                if (householdCount > MaxHouseholdsPerProperty)
                    throw new ProtocolException("Occupancy property exceeds its household cap.");
                households += householdCount;
                if (households > MaxHouseholdsPerPage)
                    throw new ProtocolException("Occupancy page exceeds its household cap.");
                property.Households = new OccupancyHousehold[householdCount];
                for (int h = 0; h < householdCount; h++)
                {
                    var household = new OccupancyHousehold
                    {
                        HouseholdId = unchecked((ulong)reader.ReadLong()),
                        PrefabName = names[ReadNameIndex(reader, nameCount)],
                        Flags = reader.ReadByte(),
                        Departing = ReadStrictBool(reader),
                        Rent = reader.ReadInt(),
                        Savings = reader.ReadInt(),
                        Money = reader.ReadInt(),
                        SalaryLastDay = reader.ReadInt(),
                        ConsumptionPerDay = reader.ReadShort(),
                        ShoppedValuePerDay = unchecked((uint)reader.ReadInt()),
                        ShoppedValueLastDay = unchecked((uint)reader.ReadInt()),
                        LastDayFrameIndex = unchecked((uint)reader.ReadInt()),
                        MoneySpentOnBuildingLevelingLastDay = reader.ReadInt(),
                        NameIndices = ReadNameIndices(reader),
                    };
                    if (!householdIds.Add(household.HouseholdId))
                        throw new ProtocolException("Duplicate household id in occupancy page.");
                    int citizenCount = reader.ReadByte();
                    if (citizenCount > MaxCitizensPerHousehold)
                        throw new ProtocolException("Occupancy household exceeds its citizen cap.");
                    citizens += citizenCount;
                    if (citizens > MaxCitizensPerPage)
                        throw new ProtocolException("Occupancy page exceeds its citizen cap.");
                    household.Citizens = new OccupancyCitizen[citizenCount];
                    for (int c = 0; c < citizenCount; c++)
                        household.Citizens[c] = new OccupancyCitizen
                        {
                            CitizenId = unchecked((ulong)reader.ReadLong()),
                            PrefabName = names[ReadNameIndex(reader, nameCount)],
                            State = reader.ReadShort(),
                            PseudoRandom = unchecked((ushort)reader.ReadShort()),
                            BirthDay = reader.ReadShort(),
                            Health = reader.ReadByte(),
                            WellBeing = reader.ReadByte(),
                            Employment = reader.ReadByte(),
                            UnemploymentCounter = reader.ReadInt(),
                            NameIndices = ReadNameIndices(reader),
                        };
                    for (int c = 0; c < citizenCount; c++)
                        if (!citizenIds.Add(household.Citizens[c].CitizenId))
                            throw new ProtocolException("Duplicate citizen id in occupancy page.");
                    int petCount = reader.ReadByte();
                    if (petCount > MaxPetsPerHousehold)
                        throw new ProtocolException("Occupancy household exceeds its pet cap.");
                    pets += petCount;
                    if (pets > MaxPetsPerPage)
                        throw new ProtocolException("Occupancy page exceeds its pet cap.");
                    household.Pets = new string[petCount];
                    for (int p = 0; p < petCount; p++)
                        household.Pets[p] = names[ReadNameIndex(reader, nameCount)];
                    int vehicleCount = reader.ReadByte();
                    if (vehicleCount > MaxVehiclesPerHousehold)
                        throw new ProtocolException("Occupancy household exceeds its vehicle cap.");
                    vehicles += vehicleCount;
                    if (vehicles > MaxVehiclesPerPage)
                        throw new ProtocolException("Occupancy page exceeds its vehicle cap.");
                    household.OwnedVehicles = new string[vehicleCount];
                    for (int v = 0; v < vehicleCount; v++)
                        household.OwnedVehicles[v] = names[ReadNameIndex(reader, nameCount)];
                    property.Households[h] = household;
                }
                Validate(property);
                if (property.Revision > snapshot.RevisionWatermark)
                    throw new ProtocolException("Occupancy property revision exceeds its page watermark.");
                if (!identities.Add(property.Identity))
                    throw new ProtocolException("Duplicate property identity in occupancy page.");
                snapshot.Properties.Add(property);
            }
            if (reader.Remaining != 0)
                throw new ProtocolException("Trailing bytes in occupancy page.");
            return snapshot;
        }

        public static ResidentialOccupancySnapshot Decode(byte[] body)
        {
            if (body == null) throw new ProtocolException("Null occupancy page.");
            if (body.Length > MaxEncodedBytes)
                throw new ProtocolException("Occupancy page exceeds its encoded-byte cap.");
            return Read(new NetworkReader(body));
        }
    }
}
