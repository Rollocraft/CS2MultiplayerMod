using System;
using System.Collections.Generic;
using System.Text;
using CS2MultiplayerMod.Core.Protocol;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>
    /// One non-zero entry from a household's economy resource buffer. Resource indices are the
    /// compact, stable ordering used by the game's economy API; omitted entries mean zero.
    /// </summary>
    public struct HouseholdResourceInitial
    {
        public byte ResourceIndex;
        public int Amount;
    }

    /// <summary>
    /// Initial semantic citizen state. This deliberately contains no pathfinding Resident, trip,
    /// controller, workplace or school entity: those references are machine-local and cannot be
    /// made safe merely by copying an entity index.
    /// </summary>
    public struct CitizenInitial
    {
        /// <summary>Opaque host identity, valid only until the next world load/resync.</summary>
        public ulong CitizenId;

        public const byte EmploymentHasWorker = 1 << 0;

        public string PrefabName;
        public ushort State;
        public ushort PseudoRandom;
        public byte WellBeing;
        public byte Health;
        public byte LeisureCounter;
        public byte PenaltyCounter;
        public int UnemploymentCounter;
        public short BirthDay;
        public float UnemploymentTimeCounter;
        public int SicknessPenalty;

        /// <summary>
        /// Income-relevant worker metadata. No workplace entity is carried; a receiver must never
        /// manufacture a Worker with a null workplace from these scalars.
        /// </summary>
        public byte EmploymentFlags;
        public byte WorkerLevel;
        public byte WorkerShift;
    }

    /// <summary>
    /// One logical household pet. The walking animal is generated locally from this semantic pet
    /// and is intentionally absent from the wire format.
    /// </summary>
    public struct HouseholdPetInitial
    {
        /// <summary>Opaque host identity, valid only until the next world load/resync.</summary>
        public ulong PetId;
        public string PrefabName;
    }

    /// <summary>A household, its initial members, logical pets and safe scalar economy state.</summary>
    public struct HouseholdInitial
    {
        /// <summary>Opaque host identity, valid only until the next world load/resync.</summary>
        public ulong HouseholdId;
        public string PrefabName;
        public byte Flags;

        // Household's own scalar state. HouseholdResources is Household.m_Resources, while
        // ResourceAmounts represents the separate Game.Economy.Resources buffer.
        public int HouseholdResources;
        public short ConsumptionPerDay;
        public uint ShoppedValuePerDay;
        public uint ShoppedValueLastDay;
        public uint LastDayFrameIndex;
        public int SalaryLastDay;
        public int MoneySpentOnBuildingLevelingLastDay;
        public int Rent;

        public HouseholdResourceInitial[] ResourceAmounts;
        public CitizenInitial[] Citizens;
        public HouseholdPetInitial[] Pets;
    }

    /// <summary>
    /// One bounded part of the complete initial household graph for a residential property. Prefab
    /// plus transform anchor and the building's random seed form the same portable identity on every
    /// machine; no Unity entity index crosses the wire.
    /// </summary>
    public struct PropertyHouseholdsInitial
    {
        /// <summary>Shared by every part of one complete property snapshot.</summary>
        public uint SnapshotId;

        /// <summary>
        /// Command-27 generation identity when the property was host-grown, or zero when the
        /// fallback prefab/anchor/seed identity is the only available identity.
        /// </summary>
        public uint GrowableSequence;

        public string PrefabName;
        public float AnchorX, AnchorY, AnchorZ;
        public ushort RandomSeed;

        /// <summary>Zero-based part metadata for high-density properties split across payloads.</summary>
        public ushort PartIndex;
        public ushort PartCount;
        public ushort TotalHouseholdCount;
        public HouseholdInitial[] Households;
    }

    /// <summary>
    /// Bounded state-channel payload containing newly initialized residential properties. Names
    /// are interned once per batch, so the usual family costs 35 bytes per citizen and ten bytes
    /// per logical pet after its prefab name first appears, including their opaque host identities.
    /// A batch may contain several properties,
    /// but never exceeds 16 KiB; capture can use <see cref="EstimateEncodedBytes"/> to split work
    /// before handing it to the state channel.
    /// </summary>
    public sealed class HouseholdInitialBatch
    {
        public const byte FormatVersion = 1;
        // Small enough for predictable relay/backlog behaviour and far below the generic state
        // envelope cap. Capture splits at a property boundary rather than emitting a giant census.
        public const int MaxEncodedBytes = 16 * 1024;

        public const int MaxProperties = 16;
        public const int MaxPrefabNames = 256;
        public const int MaxHouseholdsPerBatch = 32;
        public const int MaxHouseholdsPerPropertyPart = MaxHouseholdsPerBatch;
        public const int MaxTotalHouseholdsPerProperty = 4096;
        public const int MaxSnapshotParts = 128;
        public const int MaxCitizensPerHousehold = 16;
        public const int MaxPetsPerHousehold = 8;
        public const int MaxCitizensPerBatch = 256;
        public const int MaxPetsPerBatch = 128;

        /// <summary>Money is index 0 and Fish is index 40 in the current economy resource order.</summary>
        public const int MaxResourceIndex = 40;
        public const int MaxResourceEntriesPerHousehold = MaxResourceIndex + 1;

        public const int PropertyFixedBytes = 2 + 4 + 4 + 12 + 2 + 2 + 2 + 2 + 2;
        public const int HouseholdFixedBytes = 8 + 2 + 1 + 4 + 2 + 12 + 4 + 4 + 4 + 6;
        public const int ResourceBytes = 1 + 4;
        public const int CitizenBytes = 8 + 2 + 2 + 2 + 4 + 4 + 2 + 4 + 4 + 3;
        public const int PetBytes = 8 + 2;

        private const ushort KnownCitizenFlags = 0x7fff;
        private const byte KnownHouseholdFlags = 0x07;
        private const byte KnownEmploymentFlags = CitizenInitial.EmploymentHasWorker;
        private const float MaxUnemploymentTime = 1000000f;

        /// <summary>Host-issued replay sequence for this batch; zero is never valid.</summary>
        public uint Sequence;

        public PropertyHouseholdsInitial[] Properties = Array.Empty<PropertyHouseholdsInitial>();

        /// <summary>Returns the exact byte count after name interning, without allocating a body.</summary>
        public int EstimateEncodedBytes()
        {
            Prepared prepared = Prepare();
            return prepared.Size;
        }

        public void Write(NetworkWriter writer)
        {
            if (writer == null) throw new ArgumentNullException(nameof(writer));
            Prepared prepared = Prepare();
            if (prepared.Size > MaxEncodedBytes)
                throw new ProtocolException("Initial-household payload " + prepared.Size +
                                            " bytes exceeds the " + MaxEncodedBytes + "-byte cap.");

            writer.WriteByte(FormatVersion);
            writer.WriteInt(unchecked((int)Sequence));
            writer.WriteShort((short)prepared.Names.Count);
            for (int i = 0; i < prepared.Names.Count; i++) writer.WriteString(prepared.Names[i]);

            PropertyHouseholdsInitial[] properties = Properties ?? Array.Empty<PropertyHouseholdsInitial>();
            writer.WriteShort((short)properties.Length);
            for (int i = 0; i < properties.Length; i++)
            {
                PropertyHouseholdsInitial property = properties[i];
                writer.WriteShort(prepared.NameIndices[property.PrefabName]);
                writer.WriteInt(unchecked((int)property.SnapshotId));
                writer.WriteInt(unchecked((int)property.GrowableSequence));
                writer.WriteFloat(property.AnchorX);
                writer.WriteFloat(property.AnchorY);
                writer.WriteFloat(property.AnchorZ);
                writer.WriteShort(unchecked((short)property.RandomSeed));
                writer.WriteShort(unchecked((short)property.PartIndex));
                writer.WriteShort(unchecked((short)property.PartCount));
                writer.WriteShort(unchecked((short)property.TotalHouseholdCount));

                HouseholdInitial[] households = property.Households ?? Array.Empty<HouseholdInitial>();
                writer.WriteShort((short)households.Length);
                for (int j = 0; j < households.Length; j++)
                    WriteHousehold(writer, households[j], prepared.NameIndices);
            }
        }

        public byte[] Encode()
        {
            int size = EstimateEncodedBytes();
            if (size > MaxEncodedBytes)
                throw new ProtocolException("Initial-household payload " + size +
                                            " bytes exceeds the " + MaxEncodedBytes + "-byte cap.");
            var writer = new NetworkWriter(size);
            Write(writer);
            if (writer.Length != size)
                throw new ProtocolException("Initial-household size estimate was not exact.");
            return writer.ToArray();
        }

        public static HouseholdInitialBatch Decode(byte[] payload)
        {
            if (payload == null)
                throw new ProtocolException("Missing initial-household payload.");
            if (payload.Length > MaxEncodedBytes)
                throw new ProtocolException("Initial-household payload " + payload.Length +
                                            " bytes exceeds the " + MaxEncodedBytes + "-byte cap.");

            var reader = new NetworkReader(payload);
            byte version = reader.ReadByte();
            if (version != FormatVersion)
                throw new ProtocolException("Unknown initial-household format " + version + ".");

            uint sequence = unchecked((uint)reader.ReadInt());
            if (sequence == 0)
                throw new ProtocolException("Initial-household batch has sequence zero.");

            int nameCount = WireGuard.ReadCount(reader, 4, MaxPrefabNames);
            if (nameCount == 0)
                throw new ProtocolException("Initial-household payload has no prefab names.");
            var names = new string[nameCount];
            var uniqueNames = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < nameCount; i++)
            {
                names[i] = WireGuard.ReadName(reader);
                if (!uniqueNames.Add(names[i]))
                    throw new ProtocolException("Duplicate prefab name in initial-household payload.");
            }

            int propertyCount = WireGuard.ReadCount(reader, PropertyFixedBytes, MaxProperties);
            if (propertyCount == 0)
                throw new ProtocolException("Initial-household payload has no properties.");

            var result = new HouseholdInitialBatch
            {
                Sequence = sequence,
                Properties = new PropertyHouseholdsInitial[propertyCount],
            };
            var snapshotParts = new HashSet<SnapshotPartKey>();
            var snapshots = new Dictionary<uint, SnapshotDescriptor>();
            var householdIds = new HashSet<ulong>();
            var citizenIds = new HashSet<ulong>();
            var petIds = new HashSet<ulong>();
            int totalHouseholds = 0;
            int totalCitizens = 0;
            int totalPets = 0;
            for (int i = 0; i < propertyCount; i++)
            {
                var property = new PropertyHouseholdsInitial
                {
                    PrefabName = ReadNameIndex(reader, names, "property"),
                    SnapshotId = unchecked((uint)reader.ReadInt()),
                    GrowableSequence = unchecked((uint)reader.ReadInt()),
                    AnchorX = WireGuard.ReadCoordinate(reader),
                    AnchorY = WireGuard.ReadCoordinate(reader),
                    AnchorZ = WireGuard.ReadCoordinate(reader),
                    RandomSeed = unchecked((ushort)reader.ReadShort()),
                    PartIndex = unchecked((ushort)reader.ReadShort()),
                    PartCount = unchecked((ushort)reader.ReadShort()),
                    TotalHouseholdCount = unchecked((ushort)reader.ReadShort()),
                };
                ValidatePropertyPart(property, snapshots, snapshotParts);

                int householdCount = WireGuard.ReadCount(
                    reader, HouseholdFixedBytes, MaxHouseholdsPerPropertyPart);
                ValidatePartHouseholdCount(property, householdCount);
                totalHouseholds += householdCount;
                if (totalHouseholds > MaxHouseholdsPerBatch)
                    throw new ProtocolException("Initial-household batch contains more than " +
                                                MaxHouseholdsPerBatch + " households.");
                property.Households = new HouseholdInitial[householdCount];
                for (int j = 0; j < householdCount; j++)
                    property.Households[j] = ReadHousehold(reader, names, householdIds,
                        citizenIds, petIds, ref totalCitizens, ref totalPets);
                result.Properties[i] = property;
            }

            if (reader.Remaining != 0)
                throw new ProtocolException("Trailing bytes in initial-household payload: " +
                                            reader.Remaining + ".");
            return result;
        }

        private static void WriteHousehold(NetworkWriter writer, HouseholdInitial household,
            Dictionary<string, short> names)
        {
            writer.WriteLong(unchecked((long)household.HouseholdId));
            writer.WriteShort(names[household.PrefabName]);
            writer.WriteByte(household.Flags);
            writer.WriteInt(household.HouseholdResources);
            writer.WriteShort(household.ConsumptionPerDay);
            writer.WriteInt(unchecked((int)household.ShoppedValuePerDay));
            writer.WriteInt(unchecked((int)household.ShoppedValueLastDay));
            writer.WriteInt(unchecked((int)household.LastDayFrameIndex));
            writer.WriteInt(household.SalaryLastDay);
            writer.WriteInt(household.MoneySpentOnBuildingLevelingLastDay);
            writer.WriteInt(household.Rent);

            HouseholdResourceInitial[] resources =
                household.ResourceAmounts ?? Array.Empty<HouseholdResourceInitial>();
            writer.WriteShort((short)resources.Length);
            for (int i = 0; i < resources.Length; i++)
            {
                writer.WriteByte(resources[i].ResourceIndex);
                writer.WriteInt(resources[i].Amount);
            }

            CitizenInitial[] citizens = household.Citizens ?? Array.Empty<CitizenInitial>();
            writer.WriteShort((short)citizens.Length);
            for (int i = 0; i < citizens.Length; i++)
            {
                CitizenInitial citizen = citizens[i];
                writer.WriteLong(unchecked((long)citizen.CitizenId));
                writer.WriteShort(names[citizen.PrefabName]);
                writer.WriteShort(unchecked((short)citizen.State));
                writer.WriteShort(unchecked((short)citizen.PseudoRandom));
                writer.WriteByte(citizen.WellBeing);
                writer.WriteByte(citizen.Health);
                writer.WriteByte(citizen.LeisureCounter);
                writer.WriteByte(citizen.PenaltyCounter);
                writer.WriteInt(citizen.UnemploymentCounter);
                writer.WriteShort(citizen.BirthDay);
                writer.WriteFloat(citizen.UnemploymentTimeCounter);
                writer.WriteInt(citizen.SicknessPenalty);
                writer.WriteByte(citizen.EmploymentFlags);
                writer.WriteByte(citizen.WorkerLevel);
                writer.WriteByte(citizen.WorkerShift);
            }

            HouseholdPetInitial[] pets = household.Pets ?? Array.Empty<HouseholdPetInitial>();
            writer.WriteShort((short)pets.Length);
            for (int i = 0; i < pets.Length; i++)
            {
                writer.WriteLong(unchecked((long)pets[i].PetId));
                writer.WriteShort(names[pets[i].PrefabName]);
            }
        }

        private static HouseholdInitial ReadHousehold(NetworkReader reader, string[] names,
            HashSet<ulong> householdIds, HashSet<ulong> citizenIds, HashSet<ulong> petIds,
            ref int totalCitizens, ref int totalPets)
        {
            var household = new HouseholdInitial
            {
                HouseholdId = unchecked((ulong)reader.ReadLong()),
                PrefabName = ReadNameIndex(reader, names, "household"),
                Flags = reader.ReadByte(),
                HouseholdResources = reader.ReadInt(),
                ConsumptionPerDay = reader.ReadShort(),
                ShoppedValuePerDay = unchecked((uint)reader.ReadInt()),
                ShoppedValueLastDay = unchecked((uint)reader.ReadInt()),
                LastDayFrameIndex = unchecked((uint)reader.ReadInt()),
                SalaryLastDay = reader.ReadInt(),
                MoneySpentOnBuildingLevelingLastDay = reader.ReadInt(),
                Rent = reader.ReadInt(),
            };
            ValidateOpaqueId(household.HouseholdId, "household", householdIds);
            ValidateHouseholdScalars(household);

            int resourceCount = WireGuard.ReadCount(
                reader, ResourceBytes, MaxResourceEntriesPerHousehold);
            household.ResourceAmounts = new HouseholdResourceInitial[resourceCount];
            int previousResource = -1;
            for (int i = 0; i < resourceCount; i++)
            {
                byte resourceIndex = reader.ReadByte();
                if (resourceIndex > MaxResourceIndex)
                    throw new ProtocolException("Unknown household resource index " +
                                                resourceIndex + ".");
                if (resourceIndex <= previousResource)
                    throw new ProtocolException(
                        "Household resources are duplicated or not in canonical order.");
                previousResource = resourceIndex;
                household.ResourceAmounts[i] = new HouseholdResourceInitial
                {
                    ResourceIndex = resourceIndex,
                    Amount = reader.ReadInt(),
                };
                if (household.ResourceAmounts[i].Amount == 0)
                    throw new ProtocolException("Zero household resource must be omitted.");
            }

            int citizenCount = WireGuard.ReadCount(
                reader, CitizenBytes, MaxCitizensPerHousehold);
            totalCitizens += citizenCount;
            if (totalCitizens > MaxCitizensPerBatch)
                throw new ProtocolException("Initial-household batch contains more than " +
                                            MaxCitizensPerBatch + " citizens.");
            household.Citizens = new CitizenInitial[citizenCount];
            for (int i = 0; i < citizenCount; i++)
            {
                var citizen = new CitizenInitial
                {
                    CitizenId = unchecked((ulong)reader.ReadLong()),
                    PrefabName = ReadNameIndex(reader, names, "citizen"),
                    State = unchecked((ushort)reader.ReadShort()),
                    PseudoRandom = unchecked((ushort)reader.ReadShort()),
                    WellBeing = reader.ReadByte(),
                    Health = reader.ReadByte(),
                    LeisureCounter = reader.ReadByte(),
                    PenaltyCounter = reader.ReadByte(),
                    UnemploymentCounter = reader.ReadInt(),
                    BirthDay = reader.ReadShort(),
                    UnemploymentTimeCounter = WireGuard.ReadFinite(reader),
                    SicknessPenalty = reader.ReadInt(),
                    EmploymentFlags = reader.ReadByte(),
                    WorkerLevel = reader.ReadByte(),
                    WorkerShift = reader.ReadByte(),
                };
                ValidateOpaqueId(citizen.CitizenId, "citizen", citizenIds);
                ValidateCitizen(citizen);
                household.Citizens[i] = citizen;
            }

            int petCount = WireGuard.ReadCount(reader, PetBytes, MaxPetsPerHousehold);
            totalPets += petCount;
            if (totalPets > MaxPetsPerBatch)
                throw new ProtocolException("Initial-household batch contains more than " +
                                            MaxPetsPerBatch + " logical pets.");
            household.Pets = new HouseholdPetInitial[petCount];
            for (int i = 0; i < petCount; i++)
            {
                household.Pets[i].PetId = unchecked((ulong)reader.ReadLong());
                ValidateOpaqueId(household.Pets[i].PetId, "household pet", petIds);
                household.Pets[i].PrefabName = ReadNameIndex(reader, names, "household pet");
            }
            return household;
        }

        private Prepared Prepare()
        {
            if (Sequence == 0)
                throw new ProtocolException("Initial-household batch has sequence zero.");
            PropertyHouseholdsInitial[] properties = Properties ?? Array.Empty<PropertyHouseholdsInitial>();
            if (properties.Length == 0 || properties.Length > MaxProperties)
                throw new ProtocolException("Initial-household property count must be between 1 and " +
                                            MaxProperties + ".");

            var names = new List<string>();
            var indices = new Dictionary<string, short>(StringComparer.Ordinal);
            var snapshotParts = new HashSet<SnapshotPartKey>();
            var snapshots = new Dictionary<uint, SnapshotDescriptor>();
            var householdIds = new HashSet<ulong>();
            var citizenIds = new HashSet<ulong>();
            var petIds = new HashSet<ulong>();
            int totalHouseholds = 0;
            int totalCitizens = 0;
            int totalPets = 0;
            long fixedSize = 1 + 4 + 2 + 2;

            for (int i = 0; i < properties.Length; i++)
            {
                PropertyHouseholdsInitial property = properties[i];
                AddName(property.PrefabName, names, indices);
                ValidateCoordinate(property.AnchorX, "property X");
                ValidateCoordinate(property.AnchorY, "property Y");
                ValidateCoordinate(property.AnchorZ, "property Z");
                ValidatePropertyPart(property, snapshots, snapshotParts);

                HouseholdInitial[] households = property.Households ?? Array.Empty<HouseholdInitial>();
                if (households.Length > MaxHouseholdsPerPropertyPart)
                    throw new ProtocolException("Household count exceeds " +
                                                MaxHouseholdsPerPropertyPart + " for one property part.");
                ValidatePartHouseholdCount(property, households.Length);
                totalHouseholds += households.Length;
                if (totalHouseholds > MaxHouseholdsPerBatch)
                    throw new ProtocolException("Initial-household batch contains more than " +
                                                MaxHouseholdsPerBatch + " households.");
                fixedSize += PropertyFixedBytes;
                for (int j = 0; j < households.Length; j++)
                {
                    HouseholdInitial household = households[j];
                    ValidateOpaqueId(household.HouseholdId, "household", householdIds);
                    AddName(household.PrefabName, names, indices);
                    ValidateHouseholdScalars(household);

                    HouseholdResourceInitial[] resources =
                        household.ResourceAmounts ?? Array.Empty<HouseholdResourceInitial>();
                    CitizenInitial[] citizens = household.Citizens ?? Array.Empty<CitizenInitial>();
                    HouseholdPetInitial[] pets =
                        household.Pets ?? Array.Empty<HouseholdPetInitial>();
                    if (resources.Length > MaxResourceEntriesPerHousehold)
                        throw new ProtocolException("Household resource entry count exceeds " +
                                                    MaxResourceEntriesPerHousehold + ".");
                    if (citizens.Length > MaxCitizensPerHousehold)
                        throw new ProtocolException("Citizen count exceeds " +
                                                    MaxCitizensPerHousehold + " for one household.");
                    if (pets.Length > MaxPetsPerHousehold)
                        throw new ProtocolException("Pet count exceeds " + MaxPetsPerHousehold +
                                                    " for one household.");
                    totalCitizens += citizens.Length;
                    if (totalCitizens > MaxCitizensPerBatch)
                        throw new ProtocolException("Initial-household batch contains more than " +
                                                    MaxCitizensPerBatch + " citizens.");
                    totalPets += pets.Length;
                    if (totalPets > MaxPetsPerBatch)
                        throw new ProtocolException("Initial-household batch contains more than " +
                                                    MaxPetsPerBatch + " logical pets.");

                    int previousResource = -1;
                    for (int k = 0; k < resources.Length; k++)
                    {
                        int resourceIndex = resources[k].ResourceIndex;
                        if (resourceIndex > MaxResourceIndex)
                            throw new ProtocolException("Unknown household resource index " +
                                                        resourceIndex + ".");
                        if (resourceIndex <= previousResource)
                            throw new ProtocolException(
                                "Household resources are duplicated or not in canonical order.");
                        if (resources[k].Amount == 0)
                            throw new ProtocolException("Zero household resource must be omitted.");
                        previousResource = resourceIndex;
                    }

                    for (int k = 0; k < citizens.Length; k++)
                    {
                        ValidateOpaqueId(citizens[k].CitizenId, "citizen", citizenIds);
                        AddName(citizens[k].PrefabName, names, indices);
                        ValidateCitizen(citizens[k]);
                    }
                    for (int k = 0; k < pets.Length; k++)
                    {
                        ValidateOpaqueId(pets[k].PetId, "household pet", petIds);
                        AddName(pets[k].PrefabName, names, indices);
                    }

                    fixedSize += HouseholdFixedBytes +
                                 (long)resources.Length * ResourceBytes +
                                 (long)citizens.Length * CitizenBytes +
                                 (long)pets.Length * PetBytes;
                }
            }

            long nameBytes = 0;
            for (int i = 0; i < names.Count; i++)
                nameBytes += 4L + Encoding.UTF8.GetByteCount(names[i]);
            long total = fixedSize + nameBytes;
            if (total > int.MaxValue)
                throw new ProtocolException("Initial-household size calculation overflowed.");
            return new Prepared(names, indices, (int)total);
        }

        private static void ValidateHouseholdScalars(HouseholdInitial household)
        {
            if ((household.Flags & ~KnownHouseholdFlags) != 0)
                throw new ProtocolException("Unknown household flags 0x" +
                                            household.Flags.ToString("x2") + ".");
            if (household.ConsumptionPerDay < 0)
                throw new ProtocolException("Negative household consumption per day.");
        }

        private static void ValidateCitizen(CitizenInitial citizen)
        {
            if ((citizen.State & ~KnownCitizenFlags) != 0)
                throw new ProtocolException("Unknown citizen flags 0x" +
                                            citizen.State.ToString("x4") + ".");
            if (citizen.WellBeing > 100 || citizen.Health > 100)
                throw new ProtocolException("Citizen wellbeing or health exceeds 100.");
            if (float.IsNaN(citizen.UnemploymentTimeCounter) ||
                float.IsInfinity(citizen.UnemploymentTimeCounter) ||
                citizen.UnemploymentTimeCounter < 0f ||
                citizen.UnemploymentTimeCounter > MaxUnemploymentTime)
                throw new ProtocolException("Implausible citizen unemployment time.");
            if ((citizen.EmploymentFlags & ~KnownEmploymentFlags) != 0)
                throw new ProtocolException("Unknown citizen employment flags 0x" +
                                            citizen.EmploymentFlags.ToString("x2") + ".");
            bool hasWorker = (citizen.EmploymentFlags & CitizenInitial.EmploymentHasWorker) != 0;
            if (!hasWorker && (citizen.WorkerLevel != 0 || citizen.WorkerShift != 0))
                throw new ProtocolException("Citizen without a worker carries worker metadata.");
            if (hasWorker && (citizen.WorkerLevel > 4 || citizen.WorkerShift > 2))
                throw new ProtocolException("Citizen worker level or shift is out of range.");
        }

        private static void ValidateOpaqueId(ulong id, string label, HashSet<ulong> ids)
        {
            if (id == 0)
                throw new ProtocolException("Initial-household " + label + " id is zero.");
            if (!ids.Add(id))
                throw new ProtocolException("Duplicate initial-household " + label + " id " +
                                            id + ".");
        }

        private static void ValidatePropertyPart(PropertyHouseholdsInitial property,
            Dictionary<uint, SnapshotDescriptor> snapshots,
            HashSet<SnapshotPartKey> snapshotParts)
        {
            if (property.SnapshotId == 0)
                throw new ProtocolException("Initial-household property has snapshot id zero.");
            if (property.PartCount == 0 || property.PartCount > MaxSnapshotParts)
                throw new ProtocolException("Initial-household property part count is out of range.");
            if (property.PartIndex >= property.PartCount)
                throw new ProtocolException("Initial-household property part index is out of range.");
            if (property.TotalHouseholdCount > MaxTotalHouseholdsPerProperty)
                throw new ProtocolException("Initial-household property total exceeds " +
                                            MaxTotalHouseholdsPerProperty + ".");

            SnapshotDescriptor descriptor;
            var current = new SnapshotDescriptor(property);
            if (snapshots.TryGetValue(property.SnapshotId, out descriptor))
            {
                if (!descriptor.Equals(current))
                    throw new ProtocolException("Conflicting metadata for initial-household snapshot " +
                                                property.SnapshotId + ".");
            }
            else
            {
                snapshots.Add(property.SnapshotId, current);
            }

            if (!snapshotParts.Add(new SnapshotPartKey(property.SnapshotId, property.PartIndex)))
                throw new ProtocolException("Duplicate part " + property.PartIndex +
                                            " of initial-household snapshot " +
                                            property.SnapshotId + ".");
        }

        private static void ValidatePartHouseholdCount(PropertyHouseholdsInitial property,
            int householdCount)
        {
            if (householdCount > property.TotalHouseholdCount)
                throw new ProtocolException("Property part exceeds its declared household total.");
            if (property.TotalHouseholdCount == 0)
            {
                if (property.PartCount != 1 || property.PartIndex != 0 || householdCount != 0)
                    throw new ProtocolException("Empty property snapshot has invalid part metadata.");
                return;
            }
            if (householdCount == 0)
                throw new ProtocolException("Non-empty property snapshot contains an empty part.");
            if (property.PartCount == 1 && householdCount != property.TotalHouseholdCount)
                throw new ProtocolException("Single-part property snapshot is incomplete.");
            int minimumParts = (property.TotalHouseholdCount +
                                MaxHouseholdsPerPropertyPart - 1) /
                               MaxHouseholdsPerPropertyPart;
            if (property.PartCount < minimumParts ||
                property.PartCount > property.TotalHouseholdCount)
                throw new ProtocolException("Property snapshot part count cannot contain its " +
                                            "declared household total.");
        }

        private static void AddName(string value, List<string> names,
            Dictionary<string, short> indices)
        {
            ValidateName(value);
            if (indices.ContainsKey(value)) return;
            if (names.Count >= MaxPrefabNames)
                throw new ProtocolException("Initial-household prefab count exceeds " +
                                            MaxPrefabNames + ".");
            short index = (short)names.Count;
            indices.Add(value, index);
            names.Add(value);
        }

        private static string ReadNameIndex(NetworkReader reader, string[] names, string label)
        {
            int index = reader.ReadShort();
            if (index < 0 || index >= names.Length)
                throw new ProtocolException("Invalid " + label + " prefab index " + index + ".");
            return names[index];
        }

        private static void ValidateCoordinate(float value, string label)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) ||
                value < -WireGuard.MaxCoordinate || value > WireGuard.MaxCoordinate)
                throw new ProtocolException("Invalid " + label + " coordinate.");
        }

        private static void ValidateName(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > WireGuard.MaxNameLength)
                throw new ProtocolException("Invalid initial-household prefab name.");
            for (int i = 0; i < value.Length; i++)
                if (char.IsControl(value[i]))
                    throw new ProtocolException("Control character in initial-household prefab name.");
        }

        private sealed class Prepared
        {
            public readonly List<string> Names;
            public readonly Dictionary<string, short> NameIndices;
            public readonly int Size;

            public Prepared(List<string> names, Dictionary<string, short> nameIndices, int size)
            {
                Names = names;
                NameIndices = nameIndices;
                Size = size;
            }
        }

        private struct SnapshotPartKey : IEquatable<SnapshotPartKey>
        {
            private readonly uint _snapshotId;
            private readonly ushort _partIndex;

            public SnapshotPartKey(uint snapshotId, ushort partIndex)
            {
                _snapshotId = snapshotId;
                _partIndex = partIndex;
            }

            public bool Equals(SnapshotPartKey other) =>
                _snapshotId == other._snapshotId && _partIndex == other._partIndex;

            public override bool Equals(object obj) =>
                obj is SnapshotPartKey && Equals((SnapshotPartKey)obj);

            public override int GetHashCode() =>
                unchecked(((int)_snapshotId * 397) ^ _partIndex);
        }

        private struct SnapshotDescriptor : IEquatable<SnapshotDescriptor>
        {
            private readonly string _prefabName;
            private readonly uint _growableSequence;
            private readonly int _x, _y, _z;
            private readonly ushort _seed;
            private readonly ushort _partCount;
            private readonly ushort _totalHouseholds;

            public SnapshotDescriptor(PropertyHouseholdsInitial property)
            {
                _prefabName = property.PrefabName;
                _growableSequence = property.GrowableSequence;
                _x = FloatBits(property.AnchorX);
                _y = FloatBits(property.AnchorY);
                _z = FloatBits(property.AnchorZ);
                _seed = property.RandomSeed;
                _partCount = property.PartCount;
                _totalHouseholds = property.TotalHouseholdCount;
            }

            public bool Equals(SnapshotDescriptor other) =>
                string.Equals(_prefabName, other._prefabName, StringComparison.Ordinal) &&
                _growableSequence == other._growableSequence &&
                _x == other._x && _y == other._y && _z == other._z &&
                _seed == other._seed && _partCount == other._partCount &&
                _totalHouseholds == other._totalHouseholds;

            public override bool Equals(object obj) =>
                obj is SnapshotDescriptor && Equals((SnapshotDescriptor)obj);

            public override int GetHashCode() => _x;

            private static int FloatBits(float value) =>
                BitConverter.ToInt32(BitConverter.GetBytes(value), 0);
        }
    }
}
