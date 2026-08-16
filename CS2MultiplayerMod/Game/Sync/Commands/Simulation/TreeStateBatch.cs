using System;
using System.Collections.Generic;
using CS2MultiplayerMod.Core.Protocol;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>One host-authoritative tree state, identified without an entity index.</summary>
    public struct TreeStateRecord
    {
        public string PrefabName;
        public float PosX, PosY, PosZ;
        public ushort RandomSeed;
        public byte State;
        public byte Growth;
    }

    /// <summary>
    /// Bounded payload used by the tree state channel. Prefab names are interned once per batch;
    /// a large planted forest therefore costs 18 bytes per tree rather than repeating names.
    /// </summary>
    public sealed class TreeStateBatch
    {
        public const int MaxRecords = 2048;
        public const int MaxPrefabNames = 2048;
        public const int MaxEncodedBytes = 240 * 1024;

        // Child is represented by no life-stage bit. Collected is an independent modifier.
        public const byte Teen = 1;
        public const byte Adult = 2;
        public const byte Elderly = 4;
        public const byte Dead = 8;
        public const byte Stump = 16;
        public const byte Collected = 32;
        public const byte LifeStageMask = Teen | Adult | Elderly | Dead | Stump;
        public const byte KnownStateMask = LifeStageMask | Collected;

        public TreeStateRecord[] Records = Array.Empty<TreeStateRecord>();

        public void Write(NetworkWriter writer)
        {
            TreeStateRecord[] records = Records ?? Array.Empty<TreeStateRecord>();
            if (records.Length > MaxRecords)
                throw new ProtocolException("Tree-state record count exceeds " + MaxRecords + ".");

            var prefabIndices = new Dictionary<string, short>(StringComparer.Ordinal);
            var prefabNames = new List<string>();
            for (int i = 0; i < records.Length; i++)
            {
                ValidateName(records[i].PrefabName);
                ValidateRecord(records[i]);
                if (prefabIndices.ContainsKey(records[i].PrefabName)) continue;
                if (prefabNames.Count >= MaxPrefabNames)
                    throw new ProtocolException("Tree-state prefab count exceeds " + MaxPrefabNames + ".");
                short index = (short)prefabNames.Count;
                prefabIndices.Add(records[i].PrefabName, index);
                prefabNames.Add(records[i].PrefabName);
            }

            writer.WriteShort((short)prefabNames.Count);
            for (int i = 0; i < prefabNames.Count; i++) writer.WriteString(prefabNames[i]);
            writer.WriteShort((short)records.Length);
            for (int i = 0; i < records.Length; i++)
            {
                TreeStateRecord record = records[i];
                writer.WriteShort(prefabIndices[record.PrefabName]);
                writer.WriteFloat(record.PosX);
                writer.WriteFloat(record.PosY);
                writer.WriteFloat(record.PosZ);
                writer.WriteShort(unchecked((short)record.RandomSeed));
                writer.WriteByte(record.State);
                writer.WriteByte(record.Growth);
            }
        }

        public byte[] Encode()
        {
            var writer = new NetworkWriter(4096);
            Write(writer);
            if (writer.Length > MaxEncodedBytes)
                throw new ProtocolException("Tree-state payload exceeds " + MaxEncodedBytes + " bytes.");
            return writer.ToArray();
        }

        public static TreeStateBatch Decode(byte[] payload)
        {
            if (payload == null) throw new ProtocolException("Null tree-state payload.");
            if (payload.Length > MaxEncodedBytes)
                throw new ProtocolException("Tree-state payload exceeds " + MaxEncodedBytes + " bytes.");

            var reader = new NetworkReader(payload);
            int prefabCount = WireGuard.ReadCount(reader, 4, MaxPrefabNames);
            var prefabNames = new string[prefabCount];
            for (int i = 0; i < prefabCount; i++) prefabNames[i] = WireGuard.ReadName(reader);

            int recordCount = WireGuard.ReadCount(reader, 18, MaxRecords);
            var result = new TreeStateBatch { Records = new TreeStateRecord[recordCount] };
            for (int i = 0; i < recordCount; i++)
            {
                int prefabIndex = reader.ReadShort();
                if (prefabIndex < 0 || prefabIndex >= prefabNames.Length)
                    throw new ProtocolException("Tree-state prefab index " + prefabIndex + " is invalid.");

                var record = new TreeStateRecord
                {
                    PrefabName = prefabNames[prefabIndex],
                    PosX = WireGuard.ReadCoordinate(reader),
                    PosY = WireGuard.ReadCoordinate(reader),
                    PosZ = WireGuard.ReadCoordinate(reader),
                    RandomSeed = unchecked((ushort)reader.ReadShort()),
                    State = reader.ReadByte(),
                    Growth = reader.ReadByte(),
                };
                ValidateRecord(record);
                result.Records[i] = record;
            }

            if (reader.Remaining != 0)
                throw new ProtocolException("Trailing bytes in tree-state payload: " + reader.Remaining + ".");
            return result;
        }

        public static bool IsValidState(byte state)
        {
            if ((state & ~KnownStateMask) != 0) return false;
            int lifeStage = state & LifeStageMask;
            return lifeStage == 0 || (lifeStage & (lifeStage - 1)) == 0;
        }

        private static void ValidateRecord(TreeStateRecord record)
        {
            ValidateCoordinate(record.PosX);
            ValidateCoordinate(record.PosY);
            ValidateCoordinate(record.PosZ);
            if (!IsValidState(record.State))
                throw new ProtocolException("Invalid tree state 0x" + record.State.ToString("x2") + ".");
        }

        private static void ValidateCoordinate(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) ||
                value < -WireGuard.MaxCoordinate || value > WireGuard.MaxCoordinate)
                throw new ProtocolException("Invalid tree coordinate " + value + ".");
        }

        private static void ValidateName(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > WireGuard.MaxNameLength)
                throw new ProtocolException("Invalid tree prefab name.");
            for (int i = 0; i < value.Length; i++)
                if (char.IsControl(value[i]))
                    throw new ProtocolException("Control character in tree prefab name.");
        }
    }
}
