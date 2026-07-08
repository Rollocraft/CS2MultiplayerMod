using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Sync;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>
    /// "A player redrew this district/surface polygon." The anchor is the OLD centroid
    /// (which still matches the receiver's not-yet-edited polygon); the payload is the
    /// complete new node ring - see <see cref="AreaSyncSystem"/>.
    /// </summary>
    public sealed class AreaUpdateCommand : ISimulationCommand
    {
        public const ushort Id = 16;

        public string PrefabName;
        public float AnchorX, AnchorY, AnchorZ;
        public float[] NodeX, NodeY, NodeZ, NodeElevation;

        public ushort CommandId => Id;

        public void Write(NetworkWriter writer)
        {
            writer.WriteString(PrefabName);
            writer.WriteFloat(AnchorX); writer.WriteFloat(AnchorY); writer.WriteFloat(AnchorZ);
            int count = NodeX != null ? NodeX.Length : 0;
            writer.WriteShort((short)count);
            for (int i = 0; i < count; i++)
            {
                writer.WriteFloat(NodeX[i]);
                writer.WriteFloat(NodeY[i]);
                writer.WriteFloat(NodeZ[i]);
                writer.WriteFloat(NodeElevation[i]);
            }
        }

        public void Read(NetworkReader reader)
        {
            PrefabName = WireGuard.ReadName(reader);
            AnchorX = WireGuard.ReadCoordinate(reader); AnchorY = WireGuard.ReadCoordinate(reader); AnchorZ = WireGuard.ReadCoordinate(reader);
            int count = WireGuard.ReadCount(reader, 16);
            NodeX = new float[count]; NodeY = new float[count];
            NodeZ = new float[count]; NodeElevation = new float[count];
            for (int i = 0; i < count; i++)
            {
                NodeX[i] = WireGuard.ReadCoordinate(reader);
                NodeY[i] = WireGuard.ReadCoordinate(reader);
                NodeZ[i] = WireGuard.ReadCoordinate(reader);
                NodeElevation[i] = WireGuard.ReadFinite(reader);
            }
        }

        public byte[] Encode()
        {
            var writer = new NetworkWriter(80 + (NodeX != null ? NodeX.Length * 16 : 0));
            Write(writer);
            return writer.ToArray();
        }

        public static AreaUpdateCommand Decode(byte[] body)
        {
            var command = new AreaUpdateCommand();
            command.Read(new NetworkReader(body));
            return command;
        }
    }
}
