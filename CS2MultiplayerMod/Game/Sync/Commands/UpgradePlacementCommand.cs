using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Sync;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>
    /// "A player attached an upgrade/extension to a service building." The upgrade and
    /// its owner both travel as prefab name + position so the receiver can find its own
    /// owner entity - see <see cref="UpgradeSyncSystem"/>.
    /// </summary>
    public sealed class UpgradePlacementCommand : ISimulationCommand
    {
        public const ushort Id = 7;

        public string PrefabName;
        public string OwnerPrefabName;
        public float OwnerX, OwnerY, OwnerZ;
        public float PosX, PosY, PosZ;
        public float RotX, RotY, RotZ, RotW;
        public int RandomSeed;

        public ushort CommandId => Id;

        public void Write(NetworkWriter writer)
        {
            writer.WriteString(PrefabName);
            writer.WriteString(OwnerPrefabName);
            writer.WriteFloat(OwnerX); writer.WriteFloat(OwnerY); writer.WriteFloat(OwnerZ);
            writer.WriteFloat(PosX); writer.WriteFloat(PosY); writer.WriteFloat(PosZ);
            writer.WriteFloat(RotX); writer.WriteFloat(RotY); writer.WriteFloat(RotZ); writer.WriteFloat(RotW);
            writer.WriteInt(RandomSeed);
        }

        public void Read(NetworkReader reader)
        {
            PrefabName = WireGuard.ReadName(reader);
            OwnerPrefabName = WireGuard.ReadName(reader);
            OwnerX = WireGuard.ReadCoordinate(reader); OwnerY = WireGuard.ReadCoordinate(reader); OwnerZ = WireGuard.ReadCoordinate(reader);
            PosX = WireGuard.ReadCoordinate(reader); PosY = WireGuard.ReadCoordinate(reader); PosZ = WireGuard.ReadCoordinate(reader);
            RotX = WireGuard.ReadFinite(reader); RotY = WireGuard.ReadFinite(reader); RotZ = WireGuard.ReadFinite(reader); RotW = WireGuard.ReadFinite(reader);
            RandomSeed = reader.ReadInt();
            if (RandomSeed < 0 || RandomSeed > ushort.MaxValue)
                throw new ProtocolException("Upgrade random seed is outside ushort range.");
            if (reader.Remaining != 0)
                throw new ProtocolException("Trailing bytes in upgrade-placement command: " +
                                            reader.Remaining + ".");
        }

        public byte[] Encode()
        {
            var writer = new NetworkWriter(96);
            Write(writer);
            return writer.ToArray();
        }

        public static UpgradePlacementCommand Decode(byte[] body)
        {
            var command = new UpgradePlacementCommand();
            command.Read(new NetworkReader(body));
            return command;
        }
    }
}
