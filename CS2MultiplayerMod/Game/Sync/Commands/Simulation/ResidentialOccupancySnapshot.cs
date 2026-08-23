using System;
using System.Collections.Generic;
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
    public sealed class ResidentialOccupancySnapshot
    {
        public const int MaxNames = 512;
        public const int MaxProperties = 256;
        public const int MaxHouseholdsPerProperty = 64;
        public const int MaxCitizensPerHousehold = 24;
        public const int MaxPetsPerHousehold = 4;
        public const int MaxHouseholdsPerPage = 512;
        public const int MaxCitizensPerPage = 2048;
        public const int MaxPetsPerPage = 512;
        public const int MaxPagesPerSweep = 4096;
        public const int MaxEncodedBytes = 48 * 1024;

        /// <summary>
        /// Random name slots carried per household and per citizen. A household uses one (the
        /// family surname) and a citizen one (their first name); the cap leaves room for prefabs
        /// that declare more without making a malformed page expensive.
        /// </summary>
        public const int MaxNameIndices = 4;

        /// <summary>Far above any plausible in-game rent, still short of overflowing the economy.</summary>
        public const int MaxRent = 100000000;

        /// <summary>Same idea for a household's savings and cash on hand.</summary>
        public const int MaxMoney = 1000000000;

        /// <summary>Highest wage bracket a worker can be paid at.</summary>
        public const int MaxWorkerLevel = 4;

        public uint SweepId;
        public int PageIndex;
        public bool EndOfSweep;
        public readonly List<OccupancyProperty> Properties = new List<OccupancyProperty>();

        public void Write(NetworkWriter writer)
        {
            if (writer == null) throw new ArgumentNullException(nameof(writer));
            if (SweepId == 0) throw new ProtocolException("Occupancy sweep id must be non-zero.");
            if (PageIndex < 0 || PageIndex >= MaxPagesPerSweep)
                throw new ProtocolException("Occupancy page index is outside its cap.");
            if (Properties.Count > MaxProperties)
                throw new ProtocolException("Occupancy page exceeds its property cap.");

            var names = new NameTable();
            for (int i = 0; i < Properties.Count; i++) Intern(names, Properties[i]);
            if (names.Count > MaxNames)
                throw new ProtocolException("Occupancy page exceeds its name-table cap.");

            writer.WriteInt(unchecked((int)SweepId));
            writer.WriteShort((short)PageIndex);
            writer.WriteBool(EndOfSweep);
            writer.WriteShort((short)names.Count);
            for (int i = 0; i < names.Count; i++) writer.WriteString(names.Ordered[i]);

            writer.WriteShort((short)Properties.Count);
            var identities = new HashSet<PropertyRentIdentity>();
            for (int i = 0; i < Properties.Count; i++)
            {
                OccupancyProperty property = Properties[i];
                Validate(property);
                if (!identities.Add(property.Identity))
                    throw new ProtocolException("Duplicate property identity in occupancy page.");
                writer.WriteShort((short)names.IndexOf(property.PrefabName));
                writer.WriteFloat(property.AnchorX);
                writer.WriteFloat(property.AnchorY);
                writer.WriteFloat(property.AnchorZ);
                writer.WriteByte(property.ConstructionSpeed);
                writer.WriteByte((byte)property.Households.Length);
                for (int h = 0; h < property.Households.Length; h++)
                {
                    OccupancyHousehold household = property.Households[h];
                    writer.WriteShort((short)names.IndexOf(household.PrefabName));
                    writer.WriteByte(household.Flags);
                    writer.WriteInt(household.Rent);
                    writer.WriteInt(household.Savings);
                    writer.WriteInt(household.Money);
                    WriteNameIndices(writer, household.NameIndices);
                    writer.WriteByte((byte)household.Citizens.Length);
                    for (int c = 0; c < household.Citizens.Length; c++)
                    {
                        OccupancyCitizen citizen = household.Citizens[c];
                        writer.WriteShort((short)names.IndexOf(citizen.PrefabName));
                        writer.WriteShort(citizen.State);
                        writer.WriteShort(unchecked((short)citizen.PseudoRandom));
                        writer.WriteShort(citizen.BirthDay);
                        writer.WriteByte(citizen.Health);
                        writer.WriteByte(citizen.WellBeing);
                        writer.WriteByte(citizen.Employment);
                        WriteNameIndices(writer, citizen.NameIndices);
                    }
                    writer.WriteByte((byte)household.Pets.Length);
                    for (int p = 0; p < household.Pets.Length; p++)
                        writer.WriteShort((short)names.IndexOf(household.Pets[p]));
                }
            }
            if (writer.Length > MaxEncodedBytes)
                throw new ProtocolException("Occupancy page exceeds its encoded-byte cap.");
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
            };
            if (snapshot.SweepId == 0)
                throw new ProtocolException("Occupancy sweep id must be non-zero.");
            if (snapshot.PageIndex < 0 || snapshot.PageIndex >= MaxPagesPerSweep)
                throw new ProtocolException("Occupancy page index is outside its cap.");

            // A name costs at least a two-byte length prefix on the wire.
            int nameCount = WireGuard.ReadCount(reader, 2, MaxNames);
            var names = new string[nameCount];
            for (int i = 0; i < nameCount; i++) names[i] = WireGuard.ReadName(reader);

            // 16 bytes is the smallest a property with no households can encode to.
            int propertyCount = WireGuard.ReadCount(reader, 16, MaxProperties);
            var identities = new HashSet<PropertyRentIdentity>();
            int households = 0, citizens = 0, pets = 0;
            for (int i = 0; i < propertyCount; i++)
            {
                var property = new OccupancyProperty
                {
                    PrefabName = names[ReadNameIndex(reader, nameCount)],
                    AnchorX = WireGuard.ReadCoordinate(reader),
                    AnchorY = WireGuard.ReadCoordinate(reader),
                    AnchorZ = WireGuard.ReadCoordinate(reader),
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
                        PrefabName = names[ReadNameIndex(reader, nameCount)],
                        Flags = reader.ReadByte(),
                        Rent = reader.ReadInt(),
                        Savings = reader.ReadInt(),
                        Money = reader.ReadInt(),
                        NameIndices = ReadNameIndices(reader),
                    };
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
                            PrefabName = names[ReadNameIndex(reader, nameCount)],
                            State = reader.ReadShort(),
                            PseudoRandom = unchecked((ushort)reader.ReadShort()),
                            BirthDay = reader.ReadShort(),
                            Health = reader.ReadByte(),
                            WellBeing = reader.ReadByte(),
                            Employment = reader.ReadByte(),
                            NameIndices = ReadNameIndices(reader),
                        };
                    int petCount = reader.ReadByte();
                    if (petCount > MaxPetsPerHousehold)
                        throw new ProtocolException("Occupancy household exceeds its pet cap.");
                    pets += petCount;
                    if (pets > MaxPetsPerPage)
                        throw new ProtocolException("Occupancy page exceeds its pet cap.");
                    household.Pets = new string[petCount];
                    for (int p = 0; p < petCount; p++)
                        household.Pets[p] = names[ReadNameIndex(reader, nameCount)];
                    property.Households[h] = household;
                }
                Validate(property);
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

        /// <summary>
        /// Shared validation for the codec and for host capture. Capture calls it before an entry
        /// reaches a page, so one broken local prefab or transform is skipped rather than making
        /// <see cref="Write"/> throw — city-state capture is shared, and a throw there would
        /// suppress money, clock, demand and every other channel in the same snapshot.
        /// </summary>
        public static bool IsValidProperty(OccupancyProperty property)
        {
            if (!IsValidName(property.PrefabName)) return false;
            if (!IsValidCoordinate(property.AnchorX) || !IsValidCoordinate(property.AnchorY) ||
                !IsValidCoordinate(property.AnchorZ)) return false;
            if (property.Households == null ||
                property.Households.Length > MaxHouseholdsPerProperty) return false;
            for (int h = 0; h < property.Households.Length; h++)
            {
                OccupancyHousehold household = property.Households[h];
                if (!IsValidName(household.PrefabName)) return false;
                if (!IsValidNameIndices(household.NameIndices)) return false;
                if (household.Rent < 0 || household.Rent > MaxRent) return false;
                if (household.Savings < -MaxMoney || household.Savings > MaxMoney) return false;
                if (household.Money < -MaxMoney || household.Money > MaxMoney) return false;
                if (household.Citizens == null ||
                    household.Citizens.Length > MaxCitizensPerHousehold) return false;
                for (int c = 0; c < household.Citizens.Length; c++)
                {
                    OccupancyCitizen citizen = household.Citizens[c];
                    if (!IsValidName(citizen.PrefabName)) return false;
                    if (!IsValidNameIndices(citizen.NameIndices)) return false;
                    if (citizen.WorkerLevel > MaxWorkerLevel) return false;
                }
                if (household.Pets == null || household.Pets.Length > MaxPetsPerHousehold)
                    return false;
                for (int p = 0; p < household.Pets.Length; p++)
                    if (!IsValidName(household.Pets[p])) return false;
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

    /// <summary>One residential property and everyone the host has living in it.</summary>
    public struct OccupancyProperty
    {
        public string PrefabName;
        public float AnchorX;
        public float AnchorY;
        public float AnchorZ;

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

    /// <summary>One household in a property, in the order it appears in the host's renter list.</summary>
    public struct OccupancyHousehold
    {
        public string PrefabName;
        public byte Flags;
        public int Rent;

        /// <summary><see cref="Game.Citizens.Household.m_Resources"/>: accumulated savings.</summary>
        public int Savings;

        /// <summary>The money resource in the household's own resource buffer.</summary>
        public int Money;

        /// <summary>Random name slots; the first is the family surname.</summary>
        public int[] NameIndices;

        public OccupancyCitizen[] Citizens;
        public string[] Pets;
    }

    /// <summary>
    /// One resident. Age, education and gender all live in the citizen's flag word, and household
    /// income is a function of age, employment and wage level only — never of which company employs
    /// the person — so these few fields reproduce both the resident list and the income the panel
    /// shows.
    /// </summary>
    public struct OccupancyCitizen
    {
        public string PrefabName;
        public short State;
        public ushort PseudoRandom;
        public short BirthDay;
        public byte Health;
        public byte WellBeing;

        /// <summary>Bit 0: holds a job. Bits 4-7: wage level.</summary>
        public byte Employment;

        /// <summary>Random name slots; the first is this person's first name.</summary>
        public int[] NameIndices;

        public bool Employed => (Employment & 1) != 0;
        public byte WorkerLevel => (byte)((Employment >> 4) & 0xF);

        public static byte PackEmployment(bool employed, byte level) =>
            (byte)((employed ? 1 : 0) | ((level & 0xF) << 4));
    }
}
