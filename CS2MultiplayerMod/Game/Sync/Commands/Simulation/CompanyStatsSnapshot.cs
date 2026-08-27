using System;
using System.Collections.Generic;
using CS2MultiplayerMod.Core.Protocol;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>
    /// One bounded page of host-authoritative workplace state: which business occupies each
    /// commercial, industrial and office building, the money-facing block behind its panel, and
    /// the goods it is holding.
    ///
    /// An entry is about the <b>building</b>, not the business. That is what lets the host say
    /// "nobody rents this one" - the single statement a client cannot derive for itself, and the
    /// reason tenancy can be host-authoritative at all. A vacant entry stops after the identity,
    /// so an empty shop costs a name and three floats rather than a block of zeroes.
    ///
    /// Like the rent and occupancy pages this is an absolute statement about the properties it
    /// names rather than an ordered delta: losing a page delays those workplaces until the next
    /// sweep, repeating one is harmless, and malformed data is dropped locally instead of
    /// escalating into a world resync.
    /// </summary>
    public sealed class CompanyStatsSnapshot
    {
        public const int MaxEntries = 64;
        public const int MaxPagesPerSweep = 4096;
        public const int MaxEncodedBytes = 48 * 1024;

        /// <summary>
        /// Magnitude cap for every signed money/count scalar. Far above any figure the game can
        /// reach, still short of letting a corrupt host feed a near-int.MaxValue number into the
        /// economy systems where it would overflow on the next accumulation.
        /// </summary>
        public const int MaxStatValue = 1000000000;

        /// <summary>
        /// Distinct resources one company can hold. The game's resource enum is well under this;
        /// the cap exists so a forged count cannot make the decoder allocate.
        /// </summary>
        public const int MaxResourceSlots = 64;

        private const byte FlagHasTenant = 1;
        private const byte FlagHasProfitability = 2;
        private const byte FlagsMask = FlagHasTenant | FlagHasProfitability;

        private static readonly CompanyStatsResource[] EmptyResources = new CompanyStatsResource[0];

        public uint SweepId;
        public int PageIndex;
        public bool EndOfSweep;
        public readonly List<CompanyStatsEntry> Entries = new List<CompanyStatsEntry>();

        public void Write(NetworkWriter writer)
        {
            if (writer == null) throw new ArgumentNullException(nameof(writer));
            if (SweepId == 0) throw new ProtocolException("Company-stats sweep id must be non-zero.");
            if (PageIndex < 0 || PageIndex >= MaxPagesPerSweep)
                throw new ProtocolException("Company-stats page index is outside its cap.");
            if (Entries.Count > MaxEntries)
                throw new ProtocolException("Company-stats page exceeds its entry cap.");

            writer.WriteInt(unchecked((int)SweepId));
            writer.WriteShort((short)PageIndex);
            writer.WriteBool(EndOfSweep);
            writer.WriteShort((short)Entries.Count);

            var identities = new HashSet<PropertyRentIdentity>();
            for (int i = 0; i < Entries.Count; i++)
            {
                CompanyStatsEntry entry = Entries[i];
                Validate(entry);
                if (!identities.Add(entry.Identity))
                    throw new ProtocolException("Duplicate workplace identity in company-stats page.");

                writer.WriteString(entry.PrefabName);
                writer.WriteFloat(entry.AnchorX);
                writer.WriteFloat(entry.AnchorY);
                writer.WriteFloat(entry.AnchorZ);
                writer.WriteByte(EncodeFlags(entry));
                if (!entry.HasTenant) continue;

                writer.WriteString(entry.CompanyPrefabName);
                writer.WriteInt(entry.MaxNumberOfCustomers);
                writer.WriteInt(entry.MonthlyCustomerCount);
                writer.WriteInt(entry.MonthlyCostBuyingResources);
                writer.WriteInt(entry.CurrentNumberOfCustomers);
                writer.WriteInt(entry.CurrentCostOfBuyingResources);
                writer.WriteInt(entry.Income);
                writer.WriteInt(entry.Worth);
                writer.WriteInt(entry.Profit);
                writer.WriteInt(entry.WagePaid);
                writer.WriteInt(entry.RentPaid);
                writer.WriteInt(entry.ElectricityPaid);
                writer.WriteInt(entry.WaterPaid);
                writer.WriteInt(entry.SewagePaid);
                writer.WriteInt(entry.GarbagePaid);
                writer.WriteInt(entry.TaxPaid);
                writer.WriteInt(entry.CostBuyResource);
                writer.WriteInt(entry.LastUpdateWorth);
                writer.WriteInt(entry.LastUpdateProduce);
                writer.WriteInt(unchecked((int)entry.LastFrameLowIncome));

                if (entry.HasProfitability)
                {
                    writer.WriteByte(entry.Profitability);
                    writer.WriteInt(entry.LastTotalWorth);
                }

                CompanyStatsResource[] resources = entry.Resources ?? EmptyResources;
                writer.WriteShort((short)resources.Length);
                for (int r = 0; r < resources.Length; r++)
                {
                    writer.WriteShort((short)resources[r].Index);
                    writer.WriteInt(resources[r].Amount);
                }
            }

            if (writer.Length > MaxEncodedBytes)
                throw new ProtocolException("Company-stats page exceeds its encoded-byte cap.");
        }

        public byte[] Encode()
        {
            var writer = new NetworkWriter(8192);
            Write(writer);
            return writer.ToArray();
        }

        public static CompanyStatsSnapshot Read(NetworkReader reader)
        {
            if (reader == null) throw new ArgumentNullException(nameof(reader));
            if (reader.Remaining > MaxEncodedBytes)
                throw new ProtocolException("Company-stats page exceeds its encoded-byte cap.");

            var snapshot = new CompanyStatsSnapshot
            {
                SweepId = unchecked((uint)reader.ReadInt()),
                PageIndex = reader.ReadShort(),
                EndOfSweep = ReadStrictBool(reader),
            };
            if (snapshot.SweepId == 0)
                throw new ProtocolException("Company-stats sweep id must be non-zero.");
            if (snapshot.PageIndex < 0 || snapshot.PageIndex >= MaxPagesPerSweep)
                throw new ProtocolException("Company-stats page index is outside its cap.");

            // Smallest possible entry is a vacant building: one length-prefixed name, three
            // coordinates and the flag byte.
            int count = WireGuard.ReadCount(reader, 16, MaxEntries);
            var identities = new HashSet<PropertyRentIdentity>();
            for (int i = 0; i < count; i++)
            {
                var entry = new CompanyStatsEntry
                {
                    PrefabName = WireGuard.ReadName(reader),
                    AnchorX = WireGuard.ReadCoordinate(reader),
                    AnchorY = WireGuard.ReadCoordinate(reader),
                    AnchorZ = WireGuard.ReadCoordinate(reader),
                    CompanyPrefabName = string.Empty,
                };
                DecodeFlags(reader.ReadByte(), ref entry);
                if (entry.HasTenant) ReadTenant(reader, ref entry);

                Validate(entry);
                if (!identities.Add(entry.Identity))
                    throw new ProtocolException("Duplicate workplace identity in company-stats page.");
                snapshot.Entries.Add(entry);
            }

            if (reader.Remaining != 0)
                throw new ProtocolException("Trailing bytes in company-stats page.");
            return snapshot;
        }

        public static CompanyStatsSnapshot Decode(byte[] body)
        {
            if (body == null) throw new ProtocolException("Null company-stats page.");
            if (body.Length > MaxEncodedBytes)
                throw new ProtocolException("Company-stats page exceeds its encoded-byte cap.");
            return Read(new NetworkReader(body));
        }

        private static void ReadTenant(NetworkReader reader, ref CompanyStatsEntry entry)
        {
            // Required, not optional: the receiver builds a missing business from this archetype,
            // so an unnamed tenant would be a building nobody can move into.
            entry.CompanyPrefabName = WireGuard.ReadName(reader);
            entry.MaxNumberOfCustomers = reader.ReadInt();
            entry.MonthlyCustomerCount = reader.ReadInt();
            entry.MonthlyCostBuyingResources = reader.ReadInt();
            entry.CurrentNumberOfCustomers = reader.ReadInt();
            entry.CurrentCostOfBuyingResources = reader.ReadInt();
            entry.Income = reader.ReadInt();
            entry.Worth = reader.ReadInt();
            entry.Profit = reader.ReadInt();
            entry.WagePaid = reader.ReadInt();
            entry.RentPaid = reader.ReadInt();
            entry.ElectricityPaid = reader.ReadInt();
            entry.WaterPaid = reader.ReadInt();
            entry.SewagePaid = reader.ReadInt();
            entry.GarbagePaid = reader.ReadInt();
            entry.TaxPaid = reader.ReadInt();
            entry.CostBuyResource = reader.ReadInt();
            entry.LastUpdateWorth = reader.ReadInt();
            entry.LastUpdateProduce = reader.ReadInt();
            entry.LastFrameLowIncome = unchecked((uint)reader.ReadInt());

            if (entry.HasProfitability)
            {
                entry.Profitability = reader.ReadByte();
                entry.LastTotalWorth = reader.ReadInt();
            }

            int resourceCount = WireGuard.ReadCount(reader, 6, MaxResourceSlots);
            if (resourceCount == 0) return;
            var resources = new CompanyStatsResource[resourceCount];
            for (int r = 0; r < resourceCount; r++)
            {
                resources[r] = new CompanyStatsResource
                {
                    Index = reader.ReadShort(),
                    Amount = reader.ReadInt(),
                };
            }
            entry.Resources = resources;
        }

        /// <summary>
        /// Shared validation for the wire codec and for host capture. Capture calls this before an
        /// entry reaches a page: CityState capture has no per-channel exception boundary, so one
        /// broken local prefab or transform must be skipped rather than made to throw and suppress
        /// money, clock and demand in the same snapshot.
        /// </summary>
        public static bool IsValidEntry(CompanyStatsEntry entry)
        {
            if (!IsValidName(entry.PrefabName, false)) return false;
            if (!IsValidCoordinate(entry.AnchorX) || !IsValidCoordinate(entry.AnchorY) ||
                !IsValidCoordinate(entry.AnchorZ)) return false;

            if (!entry.HasTenant)
            {
                // A vacant building carries nothing else. A rating and a shelf of goods both
                // belong to a business, so either one here means the entry was built wrong.
                return !entry.HasProfitability &&
                       string.IsNullOrEmpty(entry.CompanyPrefabName) &&
                       (entry.Resources == null || entry.Resources.Length == 0);
            }

            if (!IsValidName(entry.CompanyPrefabName, false)) return false;
            if (!IsValidStat(entry.MaxNumberOfCustomers) ||
                !IsValidStat(entry.MonthlyCustomerCount) ||
                !IsValidStat(entry.MonthlyCostBuyingResources) ||
                !IsValidStat(entry.CurrentNumberOfCustomers) ||
                !IsValidStat(entry.CurrentCostOfBuyingResources) ||
                !IsValidStat(entry.Income) || !IsValidStat(entry.Worth) ||
                !IsValidStat(entry.Profit) || !IsValidStat(entry.WagePaid) ||
                !IsValidStat(entry.RentPaid) || !IsValidStat(entry.ElectricityPaid) ||
                !IsValidStat(entry.WaterPaid) || !IsValidStat(entry.SewagePaid) ||
                !IsValidStat(entry.GarbagePaid) || !IsValidStat(entry.TaxPaid) ||
                !IsValidStat(entry.CostBuyResource) || !IsValidStat(entry.LastUpdateWorth) ||
                !IsValidStat(entry.LastUpdateProduce)) return false;
            // LastFrameLowIncome is a frame stamp whose "never" value is uint.MaxValue, so the
            // whole range is meaningful and it is deliberately not bounded here.

            if (entry.HasProfitability && !IsValidStat(entry.LastTotalWorth)) return false;

            CompanyStatsResource[] resources = entry.Resources;
            if (resources == null) return true;
            if (resources.Length > MaxResourceSlots) return false;
            for (int i = 0; i < resources.Length; i++)
            {
                if (resources[i].Index < 0 || resources[i].Index >= MaxResourceSlots) return false;
                if (!IsValidStat(resources[i].Amount)) return false;
            }
            return true;
        }

        private static byte EncodeFlags(CompanyStatsEntry entry)
        {
            byte flags = 0;
            if (entry.HasTenant) flags |= FlagHasTenant;
            if (entry.HasProfitability) flags |= FlagHasProfitability;
            return flags;
        }

        private static void DecodeFlags(byte flags, ref CompanyStatsEntry entry)
        {
            if ((flags & ~FlagsMask) != 0)
                throw new ProtocolException("Unknown company-stats entry flag.");
            entry.HasTenant = (flags & FlagHasTenant) != 0;
            entry.HasProfitability = (flags & FlagHasProfitability) != 0;
        }

        private static bool IsValidStat(int value) =>
            value >= -MaxStatValue && value <= MaxStatValue;

        private static bool IsValidName(string value, bool optional)
        {
            if (string.IsNullOrEmpty(value)) return optional;
            if (value.Length > WireGuard.MaxNameLength) return false;
            for (int i = 0; i < value.Length; i++)
                if (char.IsControl(value[i])) return false;
            return true;
        }

        private static bool ReadStrictBool(NetworkReader reader)
        {
            byte value = reader.ReadByte();
            if (value > 1) throw new ProtocolException("Invalid company-stats page flag.");
            return value != 0;
        }

        private static void Validate(CompanyStatsEntry entry)
        {
            if (!IsValidEntry(entry))
                throw new ProtocolException("Invalid company-stats entry.");
        }

        private static bool IsValidCoordinate(float value) =>
            !float.IsNaN(value) && !float.IsInfinity(value) &&
            value >= -WireGuard.MaxCoordinate && value <= WireGuard.MaxCoordinate;
    }

    /// <summary>One resource slot a company is holding: the game's resource index and the amount.</summary>
    public struct CompanyStatsResource
    {
        public int Index;
        public int Amount;
    }

    /// <summary>
    /// One workplace building: who rents it, and everything the company panel renders about that
    /// business. The identity is the same prefab-plus-anchor pair rent resolution and growable
    /// realization use, so all three channels agree on what "that building" means.
    /// </summary>
    public struct CompanyStatsEntry
    {
        public string PrefabName;
        public float AnchorX;
        public float AnchorY;
        public float AnchorZ;

        /// <summary>
        /// False means the host has nobody in this building. Everything below is then absent, and
        /// a receiver holding a business there is expected to close it.
        /// </summary>
        public bool HasTenant;

        /// <summary>
        /// The tenant's company archetype. Required whenever <see cref="HasTenant"/> is set,
        /// because a receiver with an empty building builds the business from this prefab.
        /// </summary>
        public string CompanyPrefabName;

        public int MaxNumberOfCustomers;
        public int MonthlyCustomerCount;
        public int MonthlyCostBuyingResources;
        public int CurrentNumberOfCustomers;
        public int CurrentCostOfBuyingResources;
        public int Income;
        public int Worth;
        public int Profit;
        public int WagePaid;
        public int RentPaid;
        public int ElectricityPaid;
        public int WaterPaid;
        public int SewagePaid;
        public int GarbagePaid;
        public int TaxPaid;
        public int CostBuyResource;
        public int LastUpdateWorth;
        public int LastUpdateProduce;
        public uint LastFrameLowIncome;

        /// <summary>
        /// Set only when the sender's company carries the rating component. A company without it
        /// must not be given a fabricated one, so the flag travels rather than a sentinel value.
        /// </summary>
        public bool HasProfitability;
        public byte Profitability;
        public int LastTotalWorth;

        /// <summary>
        /// The goods the business is holding. Null and empty both mean "nothing stored"; the
        /// receiver clears any local resource the host did not report for this company.
        /// </summary>
        public CompanyStatsResource[] Resources;

        public PropertyRentIdentity Identity =>
            new PropertyRentIdentity(PrefabName, AnchorX, AnchorY, AnchorZ);
    }
}
