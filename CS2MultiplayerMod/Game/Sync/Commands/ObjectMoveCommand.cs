using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Sync;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>
    /// "A player relocated this building." The old position identifies the local entity,
    /// the new transform is where it goes - see <see cref="MoveSyncSystem"/>.
    /// </summary>
    public sealed class ObjectMoveCommand : ISimulationCommand
    {
        public const ushort Id = 8;

        public string PrefabName;
        public float OldX, OldY, OldZ;
        public float NewX, NewY, NewZ;
        public float RotX, RotY, RotZ, RotW;

        public ushort CommandId => Id;

        public void Write(NetworkWriter writer)
        {
            writer.WriteString(PrefabName);
            writer.WriteFloat(OldX); writer.WriteFloat(OldY); writer.WriteFloat(OldZ);
            writer.WriteFloat(NewX); writer.WriteFloat(NewY); writer.WriteFloat(NewZ);
            writer.WriteFloat(RotX); writer.WriteFloat(RotY); writer.WriteFloat(RotZ); writer.WriteFloat(RotW);
        }

        public void Read(NetworkReader reader)
        {
            PrefabName = WireGuard.ReadName(reader);
            OldX = WireGuard.ReadCoordinate(reader); OldY = WireGuard.ReadCoordinate(reader); OldZ = WireGuard.ReadCoordinate(reader);
            NewX = WireGuard.ReadCoordinate(reader); NewY = WireGuard.ReadCoordinate(reader); NewZ = WireGuard.ReadCoordinate(reader);
            RotX = WireGuard.ReadFinite(reader); RotY = WireGuard.ReadFinite(reader); RotZ = WireGuard.ReadFinite(reader); RotW = WireGuard.ReadFinite(reader);
        }

        public byte[] Encode()
        {
            var writer = new NetworkWriter(80);
            Write(writer);
            return writer.ToArray();
        }

        public static ObjectMoveCommand Decode(byte[] body)
        {
            var command = new ObjectMoveCommand();
            command.Read(new NetworkReader(body));
            return command;
        }
    }
}
