using System;
using System.Collections.Generic;
using System.Reflection;
using Colossal.Collections;
using Game.Simulation;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Game.Diagnostics;

namespace CS2MultiplayerMod.Game.Sync.Channels
{
    /// <summary>
    /// The full state of the three zone-demand systems. They expose no setters for the values the game
    /// serializes, so the client holds the local writers and installs this snapshot into their storage.
    /// </summary>
    internal sealed class DemandStateSnapshot
    {
        internal const byte Format = 1;
        private const int MaxArrayLength = 128;

        /// <summary>
        /// Only a framing check: household demand reaches 200 and goes negative, storage and company demand
        /// are unbounded. The array lengths are the structural guard.
        /// </summary>
        private const int DemandBound = 1000000;

        public int ResidentialCurrentHousehold;
        public int3 ResidentialCurrentBuilding;
        public int ResidentialLastHousehold;
        public int3 ResidentialLastBuilding;

        public int CommercialCurrentCompany;
        public int CommercialCurrentBuilding;
        public int CommercialLastCompany;
        public int CommercialLastBuilding;

        public readonly int[] IndustrialCurrent = new int[6];
        public readonly int[] IndustrialLast = new int[6];

        public int[][] ResidentialArrays;
        public int[][] CommercialArrays;
        public int[][] IndustrialArrays;

        public void Write(NetworkWriter writer)
        {
            writer.WriteByte(Format);
            writer.WriteInt(ResidentialCurrentHousehold);
            WriteInt3(writer, ResidentialCurrentBuilding);
            writer.WriteInt(ResidentialLastHousehold);
            WriteInt3(writer, ResidentialLastBuilding);

            writer.WriteInt(CommercialCurrentCompany);
            writer.WriteInt(CommercialCurrentBuilding);
            writer.WriteInt(CommercialLastCompany);
            writer.WriteInt(CommercialLastBuilding);

            WriteScalars(writer, IndustrialCurrent);
            WriteScalars(writer, IndustrialLast);
            WriteArrays(writer, ResidentialArrays);
            WriteArrays(writer, CommercialArrays);
            WriteArrays(writer, IndustrialArrays);
        }

        public static DemandStateSnapshot Read(NetworkReader reader)
        {
            byte format = reader.ReadByte();
            if (format != Format)
                throw new ProtocolException("Unknown zone-demand state format " + format + ".");

            var result = new DemandStateSnapshot
            {
                ResidentialCurrentHousehold = ReadDemand(reader),
                ResidentialCurrentBuilding = ReadDemand3(reader),
                ResidentialLastHousehold = ReadDemand(reader),
                ResidentialLastBuilding = ReadDemand3(reader),
                CommercialCurrentCompany = ReadDemand(reader),
                CommercialCurrentBuilding = ReadDemand(reader),
                CommercialLastCompany = ReadDemand(reader),
                CommercialLastBuilding = ReadDemand(reader),
            };
            ReadScalars(reader, result.IndustrialCurrent);
            ReadScalars(reader, result.IndustrialLast);
            result.ResidentialArrays = ReadArrays(reader, DemandStateAccess.ResidentialArrayFields.Length);
            result.CommercialArrays = ReadArrays(reader, DemandStateAccess.CommercialArrayFields.Length);
            result.IndustrialArrays = ReadArrays(reader, DemandStateAccess.IndustrialArrayFields.Length);
            return result;
        }

        private static void WriteInt3(NetworkWriter writer, int3 value)
        {
            writer.WriteInt(value.x);
            writer.WriteInt(value.y);
            writer.WriteInt(value.z);
        }

        private static int3 ReadDemand3(NetworkReader reader) =>
            new int3(ReadDemand(reader), ReadDemand(reader), ReadDemand(reader));

        private static int ReadDemand(NetworkReader reader)
        {
            int value = reader.ReadInt();
            if (value < -DemandBound || value > DemandBound)
                throw new ProtocolException("Zone demand is outside +/-" + DemandBound + ": " +
                    value + ".");
            return value;
        }

        private static void WriteScalars(NetworkWriter writer, int[] values)
        {
            for (int i = 0; i < values.Length; i++) writer.WriteInt(values[i]);
        }

        private static void ReadScalars(NetworkReader reader, int[] values)
        {
            for (int i = 0; i < values.Length; i++) values[i] = ReadDemand(reader);
        }

        private static void WriteArrays(NetworkWriter writer, int[][] arrays)
        {
            for (int i = 0; i < arrays.Length; i++)
            {
                int[] values = arrays[i];
                if (values == null || values.Length <= 0 || values.Length > MaxArrayLength)
                    throw new ProtocolException("Invalid local zone-demand array length.");
                writer.WriteShort((short)values.Length);
                for (int j = 0; j < values.Length; j++) writer.WriteInt(values[j]);
            }
        }

        private static int[][] ReadArrays(NetworkReader reader, int count)
        {
            var result = new int[count][];
            for (int i = 0; i < count; i++)
            {
                int length = reader.ReadShort();
                if (length <= 0 || length > MaxArrayLength || (long)length * 4 > reader.Remaining)
                    throw new ProtocolException("Invalid zone-demand array length " + length + ".");
                var values = new int[length];
                for (int j = 0; j < length; j++) values[j] = reader.ReadInt();
                result[i] = values;
            }
            return result;
        }
    }

    /// <summary>
    /// Reflection over serialized but non-writable fields. Names and array counts are validated before
    /// any write; protocol negotiation rejects other builds.
    /// </summary>
    internal static class DemandStateAccess
    {
        internal static readonly string[] ResidentialArrayFields =
        {
            "m_LowDemandFactors", "m_MediumDemandFactors", "m_HighDemandFactors",
        };

        internal static readonly string[] CommercialArrayFields =
        {
            "m_DemandFactors", "m_ResourceDemands", "m_BuildingDemands", "m_Consumption",
            "m_FreeProperties",
        };

        internal static readonly string[] IndustrialArrayFields =
        {
            "m_ResourceDemands", "m_IndustrialDemandFactors", "m_OfficeDemandFactors",
            "m_IndustrialCompanyDemands", "m_IndustrialZoningDemands",
            "m_IndustrialBuildingDemands", "m_StorageBuildingDemands",
            "m_StorageCompanyDemands", "m_FreeProperties", "m_FreeStorages", "m_Storages",
            "m_StorageCapacities",
        };

        private static readonly string[] IndustrialCurrentFields =
        {
            "m_IndustrialCompanyDemand", "m_IndustrialBuildingDemand",
            "m_StorageCompanyDemand", "m_StorageBuildingDemand", "m_OfficeCompanyDemand",
            "m_OfficeBuildingDemand",
        };

        private static readonly string[] IndustrialLastFields =
        {
            "m_LastIndustrialCompanyDemand", "m_LastIndustrialBuildingDemand",
            "m_LastStorageCompanyDemand", "m_LastStorageBuildingDemand",
            "m_LastOfficeCompanyDemand", "m_LastOfficeBuildingDemand",
        };

        private static readonly Dictionary<string, FieldInfo> Fields =
            new Dictionary<string, FieldInfo>(StringComparer.Ordinal);

        private static bool _factorsWarned;

        public static bool TryCapture(ResidentialDemandSystem residential,
            CommercialDemandSystem commercial, IndustrialDemandSystem industrial,
            out DemandStateSnapshot snapshot)
        {
            snapshot = null;
            if (residential == null || commercial == null || industrial == null) return false;

            CompleteJob(residential, "m_WriteDependencies");
            CompleteJob(commercial, "m_WriteDependencies");
            CompleteJob(industrial, "m_WriteDependencies");

            var result = new DemandStateSnapshot
            {
                ResidentialCurrentHousehold = GetNativeValue<int>(residential, "m_HouseholdDemand"),
                ResidentialCurrentBuilding = GetNativeValue<int3>(residential, "m_BuildingDemand"),
                ResidentialLastHousehold = residential.householdDemand,
                ResidentialLastBuilding = residential.buildingDemand,
                CommercialCurrentCompany = GetNativeValue<int>(commercial, "m_CompanyDemand"),
                CommercialCurrentBuilding = GetNativeValue<int>(commercial, "m_BuildingDemand"),
                CommercialLastCompany = commercial.companyDemand,
                CommercialLastBuilding = commercial.buildingDemand,
                ResidentialArrays = CopyArrays(residential, ResidentialArrayFields),
                CommercialArrays = CopyArrays(commercial, CommercialArrayFields),
                IndustrialArrays = CopyArrays(industrial, IndustrialArrayFields),
            };
            for (int i = 0; i < IndustrialCurrentFields.Length; i++)
            {
                result.IndustrialCurrent[i] = GetNativeValue<int>(industrial,
                    IndustrialCurrentFields[i]);
                result.IndustrialLast[i] = (int)GetField(industrial.GetType(),
                    IndustrialLastFields[i]).GetValue(industrial);
            }
            snapshot = result;
            return true;
        }

        public static void Apply(ResidentialDemandSystem residential,
            CommercialDemandSystem commercial, IndustrialDemandSystem industrial,
            DemandStateSnapshot snapshot)
        {
            if (residential == null || commercial == null || industrial == null || snapshot == null)
                throw new InvalidOperationException("Zone-demand systems are not available.");

            // Complete writers and readers before replacing native storage.
            CompleteJob(residential, "m_WriteDependencies");
            CompleteJob(residential, "m_ReadDependencies");
            CompleteJob(commercial, "m_WriteDependencies");
            CompleteJob(commercial, "m_ReadDependencies");
            CompleteJob(industrial, "m_WriteDependencies");
            CompleteJob(industrial, "m_ReadDependencies");

            // Headline values first and unconditionally: they drive the toolbar bars, which must always have a
            // host value even if the factor arrays below mismatch.
            SetNativeValue(residential, "m_HouseholdDemand", snapshot.ResidentialCurrentHousehold);
            SetNativeValue(residential, "m_BuildingDemand", snapshot.ResidentialCurrentBuilding);
            SetField(residential, "m_LastHouseholdDemand", snapshot.ResidentialLastHousehold);
            SetField(residential, "m_LastBuildingDemand", snapshot.ResidentialLastBuilding);

            SetNativeValue(commercial, "m_CompanyDemand", snapshot.CommercialCurrentCompany);
            SetNativeValue(commercial, "m_BuildingDemand", snapshot.CommercialCurrentBuilding);
            SetField(commercial, "m_LastCompanyDemand", snapshot.CommercialLastCompany);
            SetField(commercial, "m_LastBuildingDemand", snapshot.CommercialLastBuilding);

            for (int i = 0; i < IndustrialCurrentFields.Length; i++)
            {
                SetNativeValue(industrial, IndustrialCurrentFields[i], snapshot.IndustrialCurrent[i]);
                SetField(industrial, IndustrialLastFields[i], snapshot.IndustrialLast[i]);
            }

            // Factor arrays only feed the City Info breakdown; a mismatch leaves it local.
            try
            {
                ValidateArrayLengths(residential, ResidentialArrayFields, snapshot.ResidentialArrays);
                ValidateArrayLengths(commercial, CommercialArrayFields, snapshot.CommercialArrays);
                ValidateArrayLengths(industrial, IndustrialArrayFields, snapshot.IndustrialArrays);

                CopyIntoArrays(residential, ResidentialArrayFields, snapshot.ResidentialArrays);
                CopyIntoArrays(commercial, CommercialArrayFields, snapshot.CommercialArrays);
                CopyIntoArrays(industrial, IndustrialArrayFields, snapshot.IndustrialArrays);
            }
            catch (Exception ex)
            {
                if (!_factorsWarned)
                {
                    _factorsWarned = true;
                    SyncLog.Warn(LogTopic.City, "ZoneDemand: host demand factor arrays did not " +
                        "match this game build (logged once); the demand bars still follow the " +
                        "host, the factor breakdown stays locally simulated: " + ex.Message);
                }
            }
        }

        private static int[][] CopyArrays(object system, string[] names)
        {
            var result = new int[names.Length][];
            for (int i = 0; i < names.Length; i++)
            {
                NativeArray<int> source = GetArray(system, names[i]);
                if (!source.IsCreated || source.Length <= 0 || source.Length > 128)
                    throw new InvalidOperationException("Invalid native demand array " + names[i] + ".");
                var values = new int[source.Length];
                for (int j = 0; j < source.Length; j++) values[j] = source[j];
                result[i] = values;
            }
            return result;
        }

        private static void ValidateArrayLengths(object system, string[] names, int[][] incoming)
        {
            if (incoming == null || incoming.Length != names.Length)
                throw new ProtocolException("Zone-demand array group count differs from this game build.");
            for (int i = 0; i < names.Length; i++)
            {
                NativeArray<int> local = GetArray(system, names[i]);
                if (!local.IsCreated || incoming[i] == null || local.Length != incoming[i].Length)
                    throw new ProtocolException("Zone-demand array '" + names[i] +
                        "' differs from this game build.");
            }
        }

        private static void CopyIntoArrays(object system, string[] names, int[][] incoming)
        {
            for (int i = 0; i < names.Length; i++)
            {
                NativeArray<int> destination = GetArray(system, names[i]);
                int[] source = incoming[i];
                for (int j = 0; j < source.Length; j++) destination[j] = source[j];
            }
        }

        private static NativeArray<int> GetArray(object system, string name) =>
            (NativeArray<int>)GetField(system.GetType(), name).GetValue(system);

        private static T GetNativeValue<T>(object system, string name) where T : unmanaged
        {
            var value = (NativeValue<T>)GetField(system.GetType(), name).GetValue(system);
            return value.value;
        }

        private static void SetNativeValue<T>(object system, string name, T wanted)
            where T : unmanaged
        {
            FieldInfo field = GetField(system.GetType(), name);
            var value = (NativeValue<T>)field.GetValue(system);
            value.value = wanted;
            field.SetValue(system, value);
        }

        private static void SetField<T>(object system, string name, T value) =>
            GetField(system.GetType(), name).SetValue(system, value);

        private static void CompleteJob(object system, string name)
        {
            var handle = (JobHandle)GetField(system.GetType(), name).GetValue(system);
            handle.Complete();
        }

        private static FieldInfo GetField(Type type, string name)
        {
            string key = type.FullName + "|" + name;
            lock (Fields)
            {
                if (Fields.TryGetValue(key, out FieldInfo field)) return field;
                field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
                if (field == null) throw new MissingFieldException(type.FullName, name);
                Fields.Add(key, field);
                return field;
            }
        }
    }
}
