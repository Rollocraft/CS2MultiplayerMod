using System;
using System.Collections.Generic;
using CS2MultiplayerMod.Core.Protocol;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>
    /// Complete host service-accounting state consumed by the budget and service-detail panels.
    /// Service prefab entities are process-local, so every record travels under its stable prefab
    /// name. The payload contains the terminal records produced by all fee and upkeep paths rather
    /// than event deltas; losing one snapshot is therefore repaired by the next one.
    /// </summary>
    internal sealed class ServiceAccountingSnapshot
    {
        public const int MaxServices = 128;
        public const int MaxFeesPerService = 32;
        public const int MaxUpkeepsPerService = 64;
        public const int MaxEncodedBytes = 240 * 1024;

        private const int MaxAccountingMagnitude = 2000000000;
        private const float MaxFloatMagnitude = 1e30f;
        private const int PlayerResourceCount = 13;
        private const long ResourceLast = 1L << 41;

        // Native enum values are part of this protocol layout. Keeping the codec game-free lets
        // the standalone hostile-payload harness compile and fuzz it without loading the game.
        internal static readonly int[] FeeIncomeSources =
        {
            3,  // FeeHealthcare
            4,  // FeeElectricity
            6,  // FeeEducation
            7,  // ExportElectricity
            8,  // ExportWater
            9,  // FeeParking
            10, // FeePublicTransport
            12, // FeeGarbage
            13, // FeeWater
        };

        internal static readonly int[] FeeAndUpkeepExpenseSources =
        {
            2, // ImportElectricity
            3, // ImportWater
            4, // ExportSewage
            5, // ServiceUpkeep
        };

        internal readonly List<ServiceAccountingService> Services =
            new List<ServiceAccountingService>();
        internal readonly int[] IncomeValues = new int[FeeIncomeSources.Length];
        internal readonly int[] ExpenseValues =
            new int[FeeAndUpkeepExpenseSources.Length];

        internal void Write(NetworkWriter writer)
        {
            if (writer == null) throw new ArgumentNullException(nameof(writer));
            if (Services.Count > MaxServices)
                throw new ProtocolException("Service-accounting snapshot exceeds its service cap.");

            writer.WriteShort((short)Services.Count);
            var names = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < Services.Count; i++)
            {
                ServiceAccountingService service = Services[i];
                Validate(service);
                if (!names.Add(service.PrefabName))
                    throw new ProtocolException("Duplicate service prefab in accounting snapshot.");

                writer.WriteString(service.PrefabName);
                writer.WriteInt(service.WorkplacesX);
                writer.WriteInt(service.WorkplacesY);
                writer.WriteInt(service.WorkplacesZ);
                writer.WriteInt(service.Count);
                writer.WriteInt(service.Export);
                writer.WriteInt(service.BaseCost);
                writer.WriteInt(service.Wages);
                writer.WriteInt(service.FullWages);

                writer.WriteShort((short)service.Fees.Length);
                for (int f = 0; f < service.Fees.Length; f++)
                {
                    ServiceAccountingFee fee = service.Fees[f];
                    writer.WriteByte((byte)fee.PlayerResource);
                    writer.WriteFloat(fee.Export);
                    writer.WriteFloat(fee.Import);
                    writer.WriteFloat(fee.Internal);
                    writer.WriteFloat(fee.ExportCount);
                    writer.WriteFloat(fee.ImportCount);
                    writer.WriteFloat(fee.InternalCount);
                }

                writer.WriteShort((short)service.Upkeeps.Length);
                for (int u = 0; u < service.Upkeeps.Length; u++)
                {
                    ServiceAccountingUpkeep upkeep = service.Upkeeps[u];
                    writer.WriteLong(upkeep.Resource);
                    writer.WriteInt(upkeep.FullCost);
                    writer.WriteInt(upkeep.Amount);
                    writer.WriteInt(upkeep.Cost);
                }
            }

            WriteAccountingValues(writer, FeeIncomeSources, IncomeValues);
            WriteAccountingValues(writer, FeeAndUpkeepExpenseSources, ExpenseValues);
            if (writer.Length > MaxEncodedBytes)
                throw new ProtocolException("Service-accounting snapshot exceeds its byte cap.");
        }

        internal static ServiceAccountingSnapshot Read(NetworkReader reader)
        {
            if (reader == null) throw new ArgumentNullException(nameof(reader));
            if (reader.Remaining > MaxEncodedBytes)
                throw new ProtocolException("Service-accounting snapshot exceeds its byte cap.");

            var snapshot = new ServiceAccountingSnapshot();
            int count = WireGuard.ReadCount(reader, 40, MaxServices);
            var names = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < count; i++)
            {
                var service = new ServiceAccountingService
                {
                    PrefabName = WireGuard.ReadName(reader),
                    WorkplacesX = ReadAccountingInt(reader),
                    WorkplacesY = ReadAccountingInt(reader),
                    WorkplacesZ = ReadAccountingInt(reader),
                    Count = ReadAccountingInt(reader),
                    Export = ReadAccountingInt(reader),
                    BaseCost = ReadAccountingInt(reader),
                    Wages = ReadAccountingInt(reader),
                    FullWages = ReadAccountingInt(reader),
                };
                if (!names.Add(service.PrefabName))
                    throw new ProtocolException("Duplicate service prefab in accounting snapshot.");

                int feeCount = WireGuard.ReadCount(reader, 25, MaxFeesPerService);
                service.Fees = new ServiceAccountingFee[feeCount];
                var feeResources = new HashSet<int>();
                for (int f = 0; f < feeCount; f++)
                {
                    int resource = reader.ReadByte();
                    var fee = new ServiceAccountingFee
                    {
                        PlayerResource = resource,
                        Export = ReadAccountingFloat(reader),
                        Import = ReadAccountingFloat(reader),
                        Internal = ReadAccountingFloat(reader),
                        ExportCount = ReadAccountingFloat(reader),
                        ImportCount = ReadAccountingFloat(reader),
                        InternalCount = ReadAccountingFloat(reader),
                    };
                    if (!IsPlayerResource(resource) || !feeResources.Add(resource))
                        throw new ProtocolException("Invalid or duplicate service-fee resource.");
                    service.Fees[f] = fee;
                }

                int upkeepCount = WireGuard.ReadCount(reader, 20, MaxUpkeepsPerService);
                service.Upkeeps = new ServiceAccountingUpkeep[upkeepCount];
                var upkeepResources = new HashSet<long>();
                for (int u = 0; u < upkeepCount; u++)
                {
                    var upkeep = new ServiceAccountingUpkeep
                    {
                        Resource = reader.ReadLong(),
                        FullCost = ReadAccountingInt(reader),
                        Amount = ReadAccountingInt(reader),
                        Cost = ReadAccountingInt(reader),
                    };
                    if (!IsResource(upkeep.Resource) ||
                        !upkeepResources.Add(upkeep.Resource))
                        throw new ProtocolException("Invalid or duplicate service-upkeep resource.");
                    service.Upkeeps[u] = upkeep;
                }

                Validate(service);
                snapshot.Services.Add(service);
            }

            ReadAccountingValues(reader, FeeIncomeSources, snapshot.IncomeValues);
            ReadAccountingValues(reader, FeeAndUpkeepExpenseSources,
                snapshot.ExpenseValues);
            if (reader.Remaining != 0)
                throw new ProtocolException("Trailing bytes in service-accounting snapshot.");
            return snapshot;
        }

        internal int GetIncome(int source) =>
            IncomeValues[RequireIndex(FeeIncomeSources, source)];

        internal void SetIncome(int source, int value) =>
            IncomeValues[RequireIndex(FeeIncomeSources, source)] = value;

        internal int GetExpense(int source) =>
            ExpenseValues[RequireIndex(FeeAndUpkeepExpenseSources, source)];

        internal void SetExpense(int source, int value) =>
            ExpenseValues[RequireIndex(FeeAndUpkeepExpenseSources, source)] = value;

        private static void WriteAccountingValues(NetworkWriter writer, int[] sources,
            int[] values)
        {
            if (values.Length != sources.Length)
                throw new ProtocolException("Invalid service-accounting value table.");
            writer.WriteShort((short)sources.Length);
            for (int i = 0; i < sources.Length; i++)
            {
                int source = sources[i];
                if (source < 0 || source > byte.MaxValue || !IsAccountingInt(values[i]))
                    throw new ProtocolException("Invalid service-accounting value.");
                writer.WriteByte((byte)source);
                writer.WriteInt(values[i]);
            }
        }

        private static void ReadAccountingValues(NetworkReader reader, int[] expected,
            int[] values)
        {
            int count = WireGuard.ReadCount(reader, 5, expected.Length);
            if (count != expected.Length)
                throw new ProtocolException("Incomplete service-accounting value table.");
            var seen = new HashSet<int>();
            for (int i = 0; i < count; i++)
            {
                int source = reader.ReadByte();
                int index = FindIndex(expected, source);
                if (index < 0 || !seen.Add(source))
                    throw new ProtocolException("Invalid or duplicate accounting source.");
                values[index] = ReadAccountingInt(reader);
            }
        }

        private static void Validate(ServiceAccountingService service)
        {
            if (!IsName(service.PrefabName))
                throw new ProtocolException("Invalid service prefab name in accounting snapshot.");
            if (!IsAccountingInt(service.WorkplacesX) ||
                !IsAccountingInt(service.WorkplacesY) ||
                !IsAccountingInt(service.WorkplacesZ) ||
                !IsAccountingInt(service.Count) || !IsAccountingInt(service.Export) ||
                !IsAccountingInt(service.BaseCost) || !IsAccountingInt(service.Wages) ||
                !IsAccountingInt(service.FullWages))
                throw new ProtocolException("Invalid service-budget aggregate.");

            ServiceAccountingFee[] fees = service.Fees ?? EmptyFees;
            ServiceAccountingUpkeep[] upkeeps = service.Upkeeps ?? EmptyUpkeeps;
            if (fees.Length > MaxFeesPerService || upkeeps.Length > MaxUpkeepsPerService)
                throw new ProtocolException("Service accounting nested entry cap exceeded.");

            var feeResources = new HashSet<int>();
            for (int i = 0; i < fees.Length; i++)
            {
                ServiceAccountingFee fee = fees[i];
                if (!IsPlayerResource(fee.PlayerResource) ||
                    !feeResources.Add(fee.PlayerResource) ||
                    !IsAccountingFloat(fee.Export) || !IsAccountingFloat(fee.Import) ||
                    !IsAccountingFloat(fee.Internal) ||
                    !IsAccountingFloat(fee.ExportCount) ||
                    !IsAccountingFloat(fee.ImportCount) ||
                    !IsAccountingFloat(fee.InternalCount))
                    throw new ProtocolException("Invalid service-fee aggregate.");
            }

            var upkeepResources = new HashSet<long>();
            for (int i = 0; i < upkeeps.Length; i++)
            {
                ServiceAccountingUpkeep upkeep = upkeeps[i];
                if (!IsResource(upkeep.Resource) ||
                    !upkeepResources.Add(upkeep.Resource) ||
                    !IsAccountingInt(upkeep.FullCost) ||
                    !IsAccountingInt(upkeep.Amount) ||
                    !IsAccountingInt(upkeep.Cost))
                    throw new ProtocolException("Invalid service-upkeep aggregate.");
            }

            service.Fees = fees;
            service.Upkeeps = upkeeps;
        }

        private static int ReadAccountingInt(NetworkReader reader)
        {
            int value = reader.ReadInt();
            if (!IsAccountingInt(value))
                throw new ProtocolException("Service-accounting integer outside its bound.");
            return value;
        }

        private static float ReadAccountingFloat(NetworkReader reader)
        {
            float value = WireGuard.ReadFinite(reader);
            if (!IsAccountingFloat(value))
                throw new ProtocolException("Service-accounting float outside its bound.");
            return value;
        }

        private static bool IsAccountingInt(int value) =>
            value >= -MaxAccountingMagnitude && value <= MaxAccountingMagnitude;

        private static bool IsAccountingFloat(float value) =>
            !float.IsNaN(value) && !float.IsInfinity(value) &&
            value >= -MaxFloatMagnitude && value <= MaxFloatMagnitude;

        private static bool IsPlayerResource(int value) =>
            value >= 0 && value < PlayerResourceCount;

        private static bool IsResource(long value) =>
            value > 0 && value < ResourceLast &&
            (value & (value - 1)) == 0;

        private static bool IsName(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > WireGuard.MaxNameLength)
                return false;
            for (int i = 0; i < value.Length; i++)
                if (char.IsControl(value[i])) return false;
            return true;
        }

        private static int RequireIndex(int[] values, int wanted)
        {
            int index = FindIndex(values, wanted);
            if (index >= 0) return index;
            throw new ProtocolException("Unsupported service-accounting source.");
        }

        private static int FindIndex(int[] values, int wanted)
        {
            for (int i = 0; i < values.Length; i++)
                if (values[i] == wanted) return i;
            return -1;
        }

        private static readonly ServiceAccountingFee[] EmptyFees =
            new ServiceAccountingFee[0];
        private static readonly ServiceAccountingUpkeep[] EmptyUpkeeps =
            new ServiceAccountingUpkeep[0];
    }

    internal sealed class ServiceAccountingService
    {
        internal string PrefabName;
        internal int WorkplacesX;
        internal int WorkplacesY;
        internal int WorkplacesZ;
        internal int Count;
        internal int Export;
        internal int BaseCost;
        internal int Wages;
        internal int FullWages;
        internal ServiceAccountingFee[] Fees = new ServiceAccountingFee[0];
        internal ServiceAccountingUpkeep[] Upkeeps = new ServiceAccountingUpkeep[0];
    }

    internal struct ServiceAccountingFee
    {
        internal int PlayerResource;
        internal float Export;
        internal float Import;
        internal float Internal;
        internal float ExportCount;
        internal float ImportCount;
        internal float InternalCount;
    }

    internal struct ServiceAccountingUpkeep
    {
        internal long Resource;
        internal int FullCost;
        internal int Amount;
        internal int Cost;
    }
}
