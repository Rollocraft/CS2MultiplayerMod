using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Sync;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>
    /// Complete live polygon for an extractor or storage area owned by a placed building.
    /// The stable owner identity disambiguates otherwise identical nearby lots and lets the
    /// receiver repair a missing owned area without replacing its building.
    /// </summary>
    public sealed class OwnedAreaSnapshotCommand : ISimulationCommand
    {
        public const ushort Id = 23;
        public const int MaxNodes = 1024;
        public const int MaxEncodedBytes = 20 * 1024;

        public string AreaPrefabName;
        public string OwnerPrefabName;
        public float OwnerX, OwnerY, OwnerZ;
        public float OwnerRotX, OwnerRotY, OwnerRotZ, OwnerRotW;
        public float[] NodeX, NodeY, NodeZ, NodeElevation;

        public ushort CommandId => Id;

        public void Write(NetworkWriter writer)
        {
            ValidateForWrite();
            writer.WriteString(AreaPrefabName);
            writer.WriteString(OwnerPrefabName);
            writer.WriteFloat(OwnerX);
            writer.WriteFloat(OwnerY);
            writer.WriteFloat(OwnerZ);
            writer.WriteFloat(OwnerRotX);
            writer.WriteFloat(OwnerRotY);
            writer.WriteFloat(OwnerRotZ);
            writer.WriteFloat(OwnerRotW);
            writer.WriteShort((short)NodeX.Length);
            for (int i = 0; i < NodeX.Length; i++)
            {
                writer.WriteFloat(NodeX[i]);
                writer.WriteFloat(NodeY[i]);
                writer.WriteFloat(NodeZ[i]);
                writer.WriteFloat(NodeElevation[i]);
            }
        }

        public void Read(NetworkReader reader)
        {
            AreaPrefabName = WireGuard.ReadName(reader);
            OwnerPrefabName = WireGuard.ReadName(reader);
            OwnerX = WireGuard.ReadCoordinate(reader);
            OwnerY = WireGuard.ReadCoordinate(reader);
            OwnerZ = WireGuard.ReadCoordinate(reader);
            OwnerRotX = WireGuard.ReadFinite(reader);
            OwnerRotY = WireGuard.ReadFinite(reader);
            OwnerRotZ = WireGuard.ReadFinite(reader);
            OwnerRotW = WireGuard.ReadFinite(reader);
            ValidateQuaternion(OwnerRotX, OwnerRotY, OwnerRotZ, OwnerRotW);

            int count = WireGuard.ReadCount(reader, 16, MaxNodes);
            if (count < 3)
                throw new ProtocolException("Owned area has fewer than three nodes.");
            NodeX = new float[count];
            NodeY = new float[count];
            NodeZ = new float[count];
            NodeElevation = new float[count];
            for (int i = 0; i < count; i++)
            {
                NodeX[i] = WireGuard.ReadCoordinate(reader);
                NodeY[i] = WireGuard.ReadCoordinate(reader);
                NodeZ[i] = WireGuard.ReadCoordinate(reader);
                NodeElevation[i] = WireGuard.ReadFinite(reader);
                ValidateElevation(NodeElevation[i]);
            }

            if (reader.Remaining != 0)
                throw new ProtocolException("Trailing bytes in owned-area snapshot: " +
                                            reader.Remaining + ".");
        }

        public byte[] Encode()
        {
            var writer = new NetworkWriter(96 + (NodeX != null ? NodeX.Length * 16 : 0));
            Write(writer);
            if (writer.Length > MaxEncodedBytes)
                throw new ProtocolException("Owned-area snapshot exceeds the " +
                                            MaxEncodedBytes + "-byte cap.");
            return writer.ToArray();
        }

        public static OwnedAreaSnapshotCommand Decode(byte[] body)
        {
            if (body == null)
                throw new ProtocolException("Missing owned-area snapshot body.");
            if (body.Length > MaxEncodedBytes)
                throw new ProtocolException("Owned-area snapshot exceeds the " +
                                            MaxEncodedBytes + "-byte cap.");
            var command = new OwnedAreaSnapshotCommand();
            command.Read(new NetworkReader(body));
            return command;
        }

        private void ValidateForWrite()
        {
            ValidateName(AreaPrefabName, "area prefab");
            ValidateName(OwnerPrefabName, "owner prefab");
            ValidateCoordinate(OwnerX, "owner X");
            ValidateCoordinate(OwnerY, "owner Y");
            ValidateCoordinate(OwnerZ, "owner Z");
            ValidateQuaternion(OwnerRotX, OwnerRotY, OwnerRotZ, OwnerRotW);
            int count = NodeX != null ? NodeX.Length : 0;
            if (count < 3 || count > MaxNodes ||
                NodeY == null || NodeY.Length != count ||
                NodeZ == null || NodeZ.Length != count ||
                NodeElevation == null || NodeElevation.Length != count)
                throw new ProtocolException("Owned-area node arrays are missing or inconsistent.");
            for (int i = 0; i < count; i++)
            {
                ValidateCoordinate(NodeX[i], "node X");
                ValidateCoordinate(NodeY[i], "node Y");
                ValidateCoordinate(NodeZ[i], "node Z");
                ValidateElevation(NodeElevation[i]);
            }
        }

        private static void ValidateName(string value, string label)
        {
            if (string.IsNullOrEmpty(value) || value.Length > WireGuard.MaxNameLength)
                throw new ProtocolException("Invalid " + label + " name.");
            for (int i = 0; i < value.Length; i++)
                if (char.IsControl(value[i]))
                    throw new ProtocolException("Control character in " + label + " name.");
        }

        private static void ValidateCoordinate(float value, string label)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) ||
                value < -WireGuard.MaxCoordinate || value > WireGuard.MaxCoordinate)
                throw new ProtocolException("Invalid " + label + " coordinate.");
        }

        private static void ValidateQuaternion(float x, float y, float z, float w)
        {
            if (float.IsNaN(x) || float.IsInfinity(x) ||
                float.IsNaN(y) || float.IsInfinity(y) ||
                float.IsNaN(z) || float.IsInfinity(z) ||
                float.IsNaN(w) || float.IsInfinity(w))
                throw new ProtocolException("Non-finite owned-area owner rotation.");
            float lengthSq = x * x + y * y + z * z + w * w;
            if (lengthSq < 0.25f || lengthSq > 2.25f)
                throw new ProtocolException("Implausible owned-area owner rotation.");
        }

        private static void ValidateElevation(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) ||
                (value != float.MinValue && (value < -100000f || value > 100000f)))
                throw new ProtocolException("Implausible owned-area node elevation.");
        }
    }
}
