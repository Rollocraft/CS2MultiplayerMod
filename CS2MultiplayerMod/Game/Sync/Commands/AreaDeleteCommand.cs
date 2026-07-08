using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Sync;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>"A player deleted this district/surface" - matched by prefab + first node.</summary>
    public sealed class AreaDeleteCommand : ISimulationCommand
    {
        public const ushort Id = 11;

        public string PrefabName;
        public float NodeX, NodeY, NodeZ;
        public int NodeCount;

        public ushort CommandId => Id;

        public void Write(NetworkWriter writer)
        {
            writer.WriteString(PrefabName);
            writer.WriteFloat(NodeX); writer.WriteFloat(NodeY); writer.WriteFloat(NodeZ);
            writer.WriteInt(NodeCount);
        }

        public void Read(NetworkReader reader)
        {
            PrefabName = WireGuard.ReadName(reader);
            NodeX = WireGuard.ReadCoordinate(reader); NodeY = WireGuard.ReadCoordinate(reader); NodeZ = WireGuard.ReadCoordinate(reader);
            NodeCount = reader.ReadInt();
            if (NodeCount < 0 || NodeCount > WireGuard.MaxItemCount)
                throw new ProtocolException("Implausible node count: " + NodeCount + ".");
        }

        public byte[] Encode()
        {
            var writer = new NetworkWriter(48);
            Write(writer);
            return writer.ToArray();
        }

        public static AreaDeleteCommand Decode(byte[] body)
        {
            var command = new AreaDeleteCommand();
            command.Read(new NetworkReader(body));
            return command;
        }
    }
}
