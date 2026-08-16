using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Sync;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>
    /// "A player attached an upgrade/extension to a service building." The upgrade and
    /// its owner both travel as prefab name + position so the receiver can find its own
    /// owner entity - see <see cref="UpgradeSyncSystem"/>.
    ///
    /// These fields are also the complete input set the game's own definition generator needs:
    /// prefab, the building being upgraded, one placement transform, and the placing tool's seed.
    /// The receiver re-runs that generator rather than rebuilding the transaction by hand.
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
        /// <summary>The placing tool's own seed; every per-definition seed is derived from it.</summary>
        public uint ToolRandomSeed;

        public ushort CommandId => Id;

        public void Write(NetworkWriter writer)
        {
            writer.WriteString(PrefabName);
            writer.WriteString(OwnerPrefabName);
            writer.WriteFloat(OwnerX); writer.WriteFloat(OwnerY); writer.WriteFloat(OwnerZ);
            writer.WriteFloat(PosX); writer.WriteFloat(PosY); writer.WriteFloat(PosZ);
            writer.WriteFloat(RotX); writer.WriteFloat(RotY); writer.WriteFloat(RotZ); writer.WriteFloat(RotW);
            writer.WriteInt(RandomSeed);
            writer.WriteInt(unchecked((int)ToolRandomSeed));
        }

        public void Read(NetworkReader reader)
        {
            PrefabName = WireGuard.ReadName(reader);
            OwnerPrefabName = WireGuard.ReadName(reader);
            OwnerX = WireGuard.ReadCoordinate(reader); OwnerY = WireGuard.ReadCoordinate(reader); OwnerZ = WireGuard.ReadCoordinate(reader);
            PosX = WireGuard.ReadCoordinate(reader); PosY = WireGuard.ReadCoordinate(reader); PosZ = WireGuard.ReadCoordinate(reader);
            RotX = WireGuard.ReadFinite(reader); RotY = WireGuard.ReadFinite(reader); RotZ = WireGuard.ReadFinite(reader); RotW = WireGuard.ReadFinite(reader);
            float rotationLengthSq = RotX * RotX + RotY * RotY + RotZ * RotZ + RotW * RotW;
            if (rotationLengthSq < 0.25f || rotationLengthSq > 2.25f)
                throw new ProtocolException("Implausible upgrade rotation length " + rotationLengthSq + ".");
            RandomSeed = reader.ReadInt();
            if (RandomSeed < 0 || RandomSeed > ushort.MaxValue)
                throw new ProtocolException("Upgrade random seed is outside ushort range.");
            // The tool seed is opaque: every 32-bit value is legal input to the game's generator.
            ToolRandomSeed = unchecked((uint)reader.ReadInt());
            if (reader.Remaining != 0)
                throw new ProtocolException("Trailing bytes in upgrade-placement command: " +
                                            reader.Remaining + ".");
        }

        public byte[] Encode()
        {
            var writer = new NetworkWriter(112);
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
