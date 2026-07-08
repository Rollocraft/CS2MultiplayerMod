using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Sync;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>
    /// "A player placed this object here." Identifies the prefab by its stable name
    /// (entity indices differ per machine, names do not) plus a world transform. The
    /// receiver resolves the name back to a local prefab and lets the game's own
    /// object-creation systems realize it - see <see cref="BuildSyncSystem"/>.
    /// </summary>
    public sealed class ObjectPlacementCommand : ISimulationCommand
    {
        public const ushort Id = 1;

        public string PrefabName;
        public float PosX, PosY, PosZ;
        public float RotX, RotY, RotZ, RotW;

        public ushort CommandId => Id;

        public void Write(NetworkWriter writer)
        {
            writer.WriteString(PrefabName);
            writer.WriteFloat(PosX);
            writer.WriteFloat(PosY);
            writer.WriteFloat(PosZ);
            writer.WriteFloat(RotX);
            writer.WriteFloat(RotY);
            writer.WriteFloat(RotZ);
            writer.WriteFloat(RotW);
        }

        public void Read(NetworkReader reader)
        {
            PrefabName = WireGuard.ReadName(reader);
            PosX = WireGuard.ReadCoordinate(reader);
            PosY = WireGuard.ReadCoordinate(reader);
            PosZ = WireGuard.ReadCoordinate(reader);
            RotX = WireGuard.ReadFinite(reader);
            RotY = WireGuard.ReadFinite(reader);
            RotZ = WireGuard.ReadFinite(reader);
            RotW = WireGuard.ReadFinite(reader);
        }

        public byte[] Encode()
        {
            var writer = new NetworkWriter(64);
            Write(writer);
            return writer.ToArray();
        }

        public static ObjectPlacementCommand Decode(byte[] body)
        {
            var command = new ObjectPlacementCommand();
            command.Read(new NetworkReader(body));
            return command;
        }
    }
}
