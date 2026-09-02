using System;
using System.Collections.Generic;
using System.Text;
using CS2MultiplayerMod.Core.Protocol;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>
    /// One bounded page of host-authoritative workplace state. Entries are keyed by the rented
    /// building, so a vacant entry is an absolute statement and a missing local company can be
    /// recreated through the game's normal rent transaction.
    /// </summary>
    public sealed class CompanyStatsSnapshot
    {
        // Dense offices carry hundreds of real employee identities. At the former 48 KiB/64-entry
        // ceiling a large city's change queue grew faster than one 1 Hz page could drain, while
        // one-tenant low-density shops happened to keep up. StateSnapshot allows 256 KiB; retain
        // envelope headroom and bound both allocation count and spatial resolution work here.
        public const int MaxEntries = 256;
        public const int MaxPagesPerSweep = 4096;
        public const int MaxEncodedBytes = 240 * 1024;
        public const int MaxStatValue = 1000000000;
        public const int MaxResourceSlots = 64;
        public const int MaxTradeCostSlots = 64;
        public const int MaxEmployeeSlots = 512;

        private const int FlagHasTenant = 1 << 0;
        private const int FlagHasProfitability = 1 << 1;
        private const int FlagHasServiceAvailable = 1 << 2;
        private const int FlagHasLodgingProvider = 1 << 3;
        private const int FlagHasWorkProvider = 1 << 4;
        private const int FlagEmployeeRosterComplete = 1 << 5;
        private const int FlagHasTaxPayer = 1 << 6;
        private const int FlagsMask = FlagHasTenant | FlagHasProfitability |
                                      FlagHasServiceAvailable | FlagHasLodgingProvider |
                                      FlagHasWorkProvider | FlagEmployeeRosterComplete |
                                      FlagHasTaxPayer;

        private static readonly CompanyStatsResource[] EmptyResources =
            new CompanyStatsResource[0];
        private static readonly CompanyStatsTradeCost[] EmptyTradeCosts =
            new CompanyStatsTradeCost[0];
        private static readonly CompanyStatsEmployee[] EmptyEmployees =
            new CompanyStatsEmployee[0];

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
                writer.WriteByte(entry.ConstructionSpeed);
                writer.WriteShort((short)EncodeFlags(entry));
                if (!entry.HasTenant) continue;

                writer.WriteString(entry.CompanyPrefabName);
                writer.WriteString(entry.BrandPrefabName);
                writer.WriteString(entry.CompanyCustomName ?? string.Empty);
                writer.WriteInt(unchecked((int)entry.CompanyRandomState));

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
                if (entry.HasServiceAvailable)
                {
                    writer.WriteInt(entry.ServiceAvailable);
                    writer.WriteFloat(entry.ServiceMeanPriority);
                }
                if (entry.HasLodgingProvider)
                {
                    writer.WriteInt(entry.FreeLodgingRooms);
                    writer.WriteInt(entry.LodgingPrice);
                }
                if (entry.HasWorkProvider) writer.WriteInt(entry.MaxWorkers);
                if (entry.HasTaxPayer)
                {
                    writer.WriteInt(entry.UntaxedIncome);
                    writer.WriteInt(entry.AverageTaxRate);
                    writer.WriteInt(entry.AverageTaxPaid);
                }

                CompanyStatsResource[] resources = entry.Resources ?? EmptyResources;
                writer.WriteShort((short)resources.Length);
                for (int r = 0; r < resources.Length; r++)
                {
                    writer.WriteShort((short)resources[r].Index);
                    writer.WriteInt(resources[r].Amount);
                }

                CompanyStatsTradeCost[] tradeCosts = entry.TradeCosts ?? EmptyTradeCosts;
                writer.WriteShort((short)tradeCosts.Length);
                for (int t = 0; t < tradeCosts.Length; t++)
                {
                    writer.WriteShort((short)tradeCosts[t].Index);
                    writer.WriteFloat(tradeCosts[t].BuyCost);
                    writer.WriteFloat(tradeCosts[t].SellCost);
                    writer.WriteLong(tradeCosts[t].LastTransferRequestTime);
                }

                CompanyStatsEmployee[] employees = entry.Employees ?? EmptyEmployees;
                writer.WriteShort((short)employees.Length);
                for (int e = 0; e < employees.Length; e++)
                {
                    writer.WriteLong(unchecked((long)employees[e].CitizenId));
                    writer.WriteByte(employees[e].Level);
                    writer.WriteFloat(employees[e].LastCommuteTime);
                    writer.WriteByte(employees[e].Shift);
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

            // A vacant record is a name, three coordinates, construction state and a 16-bit
            // flag block.
            int count = WireGuard.ReadCount(reader, 18, MaxEntries);
            var identities = new HashSet<PropertyRentIdentity>();
            for (int i = 0; i < count; i++)
            {
                var entry = new CompanyStatsEntry
                {
                    PrefabName = WireGuard.ReadName(reader),
                    AnchorX = WireGuard.ReadCoordinate(reader),
                    AnchorY = WireGuard.ReadCoordinate(reader),
                    AnchorZ = WireGuard.ReadCoordinate(reader),
                    ConstructionSpeed = reader.ReadByte(),
                    CompanyPrefabName = string.Empty,
                    BrandPrefabName = string.Empty,
                    CompanyCustomName = string.Empty,
                };
                DecodeFlags(unchecked((ushort)reader.ReadShort()), ref entry);
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
            entry.CompanyPrefabName = WireGuard.ReadName(reader);
            entry.BrandPrefabName = WireGuard.ReadName(reader);
            entry.CompanyCustomName = reader.ReadString() ?? string.Empty;
            entry.CompanyRandomState = unchecked((uint)reader.ReadInt());

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
            if (entry.HasServiceAvailable)
            {
                entry.ServiceAvailable = reader.ReadInt();
                entry.ServiceMeanPriority = WireGuard.ReadFinite(reader);
            }
            if (entry.HasLodgingProvider)
            {
                entry.FreeLodgingRooms = reader.ReadInt();
                entry.LodgingPrice = reader.ReadInt();
            }
            if (entry.HasWorkProvider) entry.MaxWorkers = reader.ReadInt();
            if (entry.HasTaxPayer)
            {
                entry.UntaxedIncome = reader.ReadInt();
                entry.AverageTaxRate = reader.ReadInt();
                entry.AverageTaxPaid = reader.ReadInt();
            }

            int resourceCount = WireGuard.ReadCount(reader, 6, MaxResourceSlots);
            if (resourceCount > 0)
            {
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

            int tradeCount = WireGuard.ReadCount(reader, 18, MaxTradeCostSlots);
            if (tradeCount > 0)
            {
                var tradeCosts = new CompanyStatsTradeCost[tradeCount];
                for (int t = 0; t < tradeCount; t++)
                {
                    tradeCosts[t] = new CompanyStatsTradeCost
                    {
                        Index = reader.ReadShort(),
                        BuyCost = WireGuard.ReadFinite(reader),
                        SellCost = WireGuard.ReadFinite(reader),
                        LastTransferRequestTime = reader.ReadLong(),
                    };
                }
                entry.TradeCosts = tradeCosts;
            }

            int employeeCount = WireGuard.ReadCount(reader, 14, MaxEmployeeSlots);
            if (employeeCount > 0)
            {
                var employees = new CompanyStatsEmployee[employeeCount];
                for (int e = 0; e < employeeCount; e++)
                {
                    employees[e] = new CompanyStatsEmployee
                    {
                        CitizenId = unchecked((ulong)reader.ReadLong()),
                        Level = reader.ReadByte(),
                        LastCommuteTime = WireGuard.ReadFinite(reader),
                        Shift = reader.ReadByte(),
                    };
                }
                entry.Employees = employees;
            }
        }

        /// <summary>
        /// Exact encoded size of an entry. Capture uses it before appending variable employee
        /// rosters, preventing one large employer from wedging a bounded page.
        /// </summary>
        public static int EstimateEncodedBytes(CompanyStatsEntry entry)
        {
            int size = EncodedStringBytes(entry.PrefabName) + 12 + 1 + 2;
            if (!entry.HasTenant) return size;

            size += EncodedStringBytes(entry.CompanyPrefabName);
            size += EncodedStringBytes(entry.BrandPrefabName);
            size += EncodedStringBytes(entry.CompanyCustomName);
            size += 4;
            size += 19 * 4;
            if (entry.HasProfitability) size += 5;
            if (entry.HasServiceAvailable) size += 8;
            if (entry.HasLodgingProvider) size += 8;
            if (entry.HasWorkProvider) size += 4;
            if (entry.HasTaxPayer) size += 12;
            size += 2 + 6 * (entry.Resources == null ? 0 : entry.Resources.Length);
            size += 2 + 18 * (entry.TradeCosts == null ? 0 : entry.TradeCosts.Length);
            size += 2 + 14 * (entry.Employees == null ? 0 : entry.Employees.Length);
            return size;
        }

        public static bool IsValidEntry(CompanyStatsEntry entry)
        {
            if (!IsValidName(entry.PrefabName, false) ||
                !IsValidCoordinate(entry.AnchorX) || !IsValidCoordinate(entry.AnchorY) ||
                !IsValidCoordinate(entry.AnchorZ)) return false;

            if (!entry.HasTenant)
            {
                return !entry.HasProfitability && !entry.HasServiceAvailable &&
                       !entry.HasLodgingProvider && !entry.HasWorkProvider &&
                       !entry.EmployeeRosterComplete && !entry.HasTaxPayer &&
                       string.IsNullOrEmpty(entry.CompanyPrefabName) &&
                       string.IsNullOrEmpty(entry.BrandPrefabName) &&
                       string.IsNullOrEmpty(entry.CompanyCustomName) &&
                       entry.CompanyRandomState == 0 &&
                       IsEmpty(entry.Resources) && IsEmpty(entry.TradeCosts) &&
                       IsEmpty(entry.Employees);
            }

            if (!IsValidName(entry.CompanyPrefabName, false) ||
                !IsValidName(entry.BrandPrefabName, false) ||
                !IsValidName(entry.CompanyCustomName, true) ||
                entry.CompanyRandomState == 0) return false;

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

            if (entry.HasProfitability && !IsValidStat(entry.LastTotalWorth)) return false;
            if (entry.HasServiceAvailable &&
                (!IsValidStat(entry.ServiceAvailable) ||
                 !IsValidFiniteScalar(entry.ServiceMeanPriority))) return false;
            if (entry.HasLodgingProvider &&
                (!IsValidStat(entry.FreeLodgingRooms) || !IsValidStat(entry.LodgingPrice)))
                return false;
            if (entry.HasWorkProvider && !IsValidStat(entry.MaxWorkers)) return false;
            if (entry.HasTaxPayer &&
                (!IsValidStat(entry.UntaxedIncome) || !IsValidStat(entry.AverageTaxRate) ||
                 !IsValidStat(entry.AverageTaxPaid))) return false;

            return ValidateResources(entry.Resources) &&
                   ValidateTradeCosts(entry.TradeCosts) &&
                   ValidateEmployees(entry.Employees);
        }

        private static bool ValidateResources(CompanyStatsResource[] resources)
        {
            if (resources == null) return true;
            if (resources.Length > MaxResourceSlots) return false;
            var seen = new HashSet<int>();
            for (int i = 0; i < resources.Length; i++)
            {
                if (resources[i].Index < 0 || resources[i].Index >= MaxResourceSlots ||
                    !IsValidStat(resources[i].Amount) || !seen.Add(resources[i].Index))
                    return false;
            }
            return true;
        }

        private static bool ValidateTradeCosts(CompanyStatsTradeCost[] costs)
        {
            if (costs == null) return true;
            if (costs.Length > MaxTradeCostSlots) return false;
            var seen = new HashSet<int>();
            for (int i = 0; i < costs.Length; i++)
            {
                if (costs[i].Index < 0 || costs[i].Index >= MaxResourceSlots ||
                    !IsValidTradeCost(costs[i].BuyCost) ||
                    !IsValidTradeCost(costs[i].SellCost) || !seen.Add(costs[i].Index))
                    return false;
            }
            return true;
        }

        private static bool ValidateEmployees(CompanyStatsEmployee[] employees)
        {
            if (employees == null) return true;
            if (employees.Length > MaxEmployeeSlots) return false;
            var seen = new HashSet<ulong>();
            for (int i = 0; i < employees.Length; i++)
            {
                if (employees[i].CitizenId == 0 || employees[i].Level > 4 ||
                    employees[i].Shift > 2 || employees[i].LastCommuteTime < 0f ||
                    !IsValidFiniteScalar(employees[i].LastCommuteTime) ||
                    !seen.Add(employees[i].CitizenId)) return false;
            }
            return true;
        }

        private static int EncodeFlags(CompanyStatsEntry entry)
        {
            int flags = 0;
            if (entry.HasTenant) flags |= FlagHasTenant;
            if (entry.HasProfitability) flags |= FlagHasProfitability;
            if (entry.HasServiceAvailable) flags |= FlagHasServiceAvailable;
            if (entry.HasLodgingProvider) flags |= FlagHasLodgingProvider;
            if (entry.HasWorkProvider) flags |= FlagHasWorkProvider;
            if (entry.EmployeeRosterComplete) flags |= FlagEmployeeRosterComplete;
            if (entry.HasTaxPayer) flags |= FlagHasTaxPayer;
            return flags;
        }

        private static void DecodeFlags(ushort flags, ref CompanyStatsEntry entry)
        {
            if ((flags & ~FlagsMask) != 0)
                throw new ProtocolException("Unknown company-stats entry flag.");
            entry.HasTenant = (flags & FlagHasTenant) != 0;
            entry.HasProfitability = (flags & FlagHasProfitability) != 0;
            entry.HasServiceAvailable = (flags & FlagHasServiceAvailable) != 0;
            entry.HasLodgingProvider = (flags & FlagHasLodgingProvider) != 0;
            entry.HasWorkProvider = (flags & FlagHasWorkProvider) != 0;
            entry.EmployeeRosterComplete = (flags & FlagEmployeeRosterComplete) != 0;
            entry.HasTaxPayer = (flags & FlagHasTaxPayer) != 0;
        }

        private static bool IsValidStat(int value) =>
            value >= -MaxStatValue && value <= MaxStatValue;

        private static bool IsValidFiniteScalar(float value) =>
            !float.IsNaN(value) && !float.IsInfinity(value) &&
            value >= -MaxStatValue && value <= MaxStatValue;

        // Game.dll uses float.MaxValue as the legitimate "no available transfer route" sentinel
        // in TradeSystem. Other company floats stay tightly bounded.
        private static bool IsValidTradeCost(float value) =>
            value == float.MaxValue || IsValidFiniteScalar(value);

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

        private static int EncodedStringBytes(string value) =>
            4 + (string.IsNullOrEmpty(value) ? 0 : Encoding.UTF8.GetByteCount(value));

        private static bool IsEmpty<T>(T[] values) => values == null || values.Length == 0;
    }

    public struct CompanyStatsResource
    {
        public int Index;
        public int Amount;
    }

    public struct CompanyStatsTradeCost
    {
        public int Index;
        public float BuyCost;
        public float SellCost;
        public long LastTransferRequestTime;
    }

    /// <summary>
    /// A host resident assigned to this workplace. CitizenId is resolved through the residential
    /// occupancy identity map; it is never interpreted as a local entity handle.
    /// </summary>
    public struct CompanyStatsEmployee
    {
        public ulong CitizenId;
        public byte Level;
        public float LastCommuteTime;
        public byte Shift;
    }

    public struct CompanyStatsEntry
    {
        public string PrefabName;
        public float AnchorX;
        public float AnchorY;
        public float AnchorZ;
        /// <summary>
        /// Zero means the host's building is complete. A non-zero value means the host still has
        /// an UnderConstruction component; zero-speed sites are encoded as one by capture.
        /// </summary>
        public byte ConstructionSpeed;
        public bool HasTenant;
        public string CompanyPrefabName;

        public string BrandPrefabName;
        public string CompanyCustomName;
        public uint CompanyRandomState;

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

        public bool HasProfitability;
        public byte Profitability;
        public int LastTotalWorth;

        public bool HasServiceAvailable;
        public int ServiceAvailable;
        public float ServiceMeanPriority;

        public bool HasLodgingProvider;
        public int FreeLodgingRooms;
        public int LodgingPrice;

        public bool HasWorkProvider;
        public int MaxWorkers;

        public bool HasTaxPayer;
        public int UntaxedIncome;
        public int AverageTaxRate;
        public int AverageTaxPaid;

        public CompanyStatsResource[] Resources;
        public CompanyStatsTradeCost[] TradeCosts;

        /// <summary>
        /// Complete means every host employee was a regular resident and is represented here. If
        /// false, the receiver adds the resolvable residents but preserves unmatched local workers
        /// rather than deleting a commuter or tourist it cannot identify safely.
        /// </summary>
        public bool EmployeeRosterComplete;
        public CompanyStatsEmployee[] Employees;

        public PropertyRentIdentity Identity =>
            new PropertyRentIdentity(PrefabName, AnchorX, AnchorY, AnchorZ);
    }
}
