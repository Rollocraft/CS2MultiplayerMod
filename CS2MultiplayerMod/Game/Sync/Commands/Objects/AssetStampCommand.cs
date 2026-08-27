using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Sync;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>
    /// "A player stamped a prebuilt intersection." Carries the complete input set the game's own
    /// definition generator takes for a stamp - the asset-stamp prefab, one placement control
    /// point, and the placing tool's seed - so the receiver regenerates the graph locally.
    ///
    /// Two courses of a stamp share a node only when their endpoint positions are bit-identical:
    /// the node generator keys them on an exact float comparison. The generator guarantees that by
    /// computing every shared endpoint from one prefab-local averaged table, so regenerating
    /// reproduces the guarantee. Replaying finished definitions only reproduces the numbers, and a
    /// single endpoint that does not survive the round trip intact becomes a ramp that renders but
    /// connects to nothing.
    /// </summary>
    public sealed class AssetStampCommand : ISimulationCommand
    {
        public const ushort Id = 25;
        public const int MaxEncodedBytes = 512;

        /// <summary>Distinguishes replays of one stamp from two deliberate identical placements.</summary>
        public long OperationId;

        /// <summary>Name of the <c>AssetStampPrefab</c>, resolved to a local prefab by the receiver.</summary>
        public string PrefabName;

        public float PosX, PosY, PosZ;
        public float RotX, RotY, RotZ, RotW;

        /// <summary>Control-point elevation; the stamp's own sub-net heights are prefab-relative.</summary>
        public float Elevation;

        /// <summary>The placing tool's seed; every per-definition seed is derived from it.</summary>
        public uint ToolRandomSeed;

        public ushort CommandId => Id;

        public void Write(NetworkWriter writer)
        {
            if (OperationId <= 0)
                throw new ProtocolException("Invalid asset-stamp operation id " + OperationId + ".");
            writer.WriteLong(OperationId);
            writer.WriteString(PrefabName);
            writer.WriteFloat(PosX); writer.WriteFloat(PosY); writer.WriteFloat(PosZ);
            writer.WriteFloat(RotX); writer.WriteFloat(RotY);
            writer.WriteFloat(RotZ); writer.WriteFloat(RotW);
            writer.WriteFloat(Elevation);
            writer.WriteInt(unchecked((int)ToolRandomSeed));
        }

        public void Read(NetworkReader reader)
        {
            OperationId = reader.ReadLong();
            if (OperationId <= 0)
                throw new ProtocolException("Invalid asset-stamp operation id " + OperationId + ".");
            PrefabName = WireGuard.ReadName(reader);
            PosX = WireGuard.ReadCoordinate(reader);
            PosY = WireGuard.ReadCoordinate(reader);
            PosZ = WireGuard.ReadCoordinate(reader);
            RotX = WireGuard.ReadFinite(reader); RotY = WireGuard.ReadFinite(reader);
            RotZ = WireGuard.ReadFinite(reader); RotW = WireGuard.ReadFinite(reader);
            float rotationLengthSq = RotX * RotX + RotY * RotY + RotZ * RotZ + RotW * RotW;
            if (rotationLengthSq < 0.25f || rotationLengthSq > 2.25f)
                throw new ProtocolException("Implausible asset-stamp rotation length " +
                                            rotationLengthSq + ".");
            Elevation = WireGuard.ReadCoordinate(reader);
            // The tool seed is opaque: every 32-bit value is legal input to the game's generator.
            ToolRandomSeed = unchecked((uint)reader.ReadInt());
            if (reader.Remaining != 0)
                throw new ProtocolException("Trailing bytes in asset-stamp command: " +
                                            reader.Remaining + ".");
        }

        public byte[] Encode()
        {
            var writer = new NetworkWriter(128);
            Write(writer);
            if (writer.Length > MaxEncodedBytes)
                throw new ProtocolException("Asset-stamp command of " + writer.Length +
                                            " bytes exceeds the " + MaxEncodedBytes + "-byte cap.");
            return writer.ToArray();
        }

        public static AssetStampCommand Decode(byte[] body)
        {
            var command = new AssetStampCommand();
            command.Read(new NetworkReader(body));
            return command;
        }
    }
}
