using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Sync;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>
    /// "A player drew this district/surface polygon." Carries the area prefab name and
    /// the full node ring - see <see cref="AreaSyncSystem"/>.
    /// </summary>
    public sealed class AreaCreateCommand : ISimulationCommand
    {
        public const ushort Id = 10;

        public string PrefabName;
        public float[] NodeX, NodeY, NodeZ, NodeElevation;

        public ushort CommandId => Id;

        public void Write(NetworkWriter writer)
        {
            writer.WriteString(PrefabName);
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
            var writer = new NetworkWriter(64 + (NodeX != null ? NodeX.Length * 16 : 0));
            Write(writer);
            return writer.ToArray();
        }

        public static AreaCreateCommand Decode(byte[] body)
        {
            var command = new AreaCreateCommand();
            command.Read(new NetworkReader(body));
            return command;
        }
    }
}
