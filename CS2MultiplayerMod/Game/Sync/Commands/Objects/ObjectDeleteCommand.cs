using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Sync;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>
    /// "A player bulldozed this object." Like <see cref="ObjectPlacementCommand"/> the
    /// target is identified by prefab name + world position (entity ids differ per
    /// machine); the receiver finds the matching local entity and marks it Deleted - see
    /// <see cref="DeleteSyncSystem"/>.
    /// </summary>
    public sealed class ObjectDeleteCommand : ISimulationCommand
    {
        public const ushort Id = 3;

        public string PrefabName;
        public float PosX, PosY, PosZ;

        public ushort CommandId => Id;

        public void Write(NetworkWriter writer)
        {
            writer.WriteString(PrefabName);
            writer.WriteFloat(PosX);
            writer.WriteFloat(PosY);
            writer.WriteFloat(PosZ);
        }

        public void Read(NetworkReader reader)
        {
            PrefabName = WireGuard.ReadName(reader);
            PosX = WireGuard.ReadCoordinate(reader);
            PosY = WireGuard.ReadCoordinate(reader);
            PosZ = WireGuard.ReadCoordinate(reader);
            if (reader.Remaining != 0)
                throw new ProtocolException("Trailing bytes in object-delete command: " + reader.Remaining + ".");
        }

        public byte[] Encode()
        {
            var writer = new NetworkWriter(48);
            Write(writer);
            return writer.ToArray();
        }

        public static ObjectDeleteCommand Decode(byte[] body)
        {
            var command = new ObjectDeleteCommand();
            command.Read(new NetworkReader(body));
            return command;
        }
    }
}
