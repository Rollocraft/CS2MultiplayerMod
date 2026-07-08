using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Sync;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>
    /// "A player applied this terraform brush stroke." Terraforming is replicated as the
    /// stream of brush applications (prefab name + position/size/angle/strength), replayed
    /// through the game's own brush pipeline on the receiver - see
    /// <see cref="TerrainSyncSystem"/>. Replaying strokes keeps the payload tiny compared
    /// to shipping heightmap regions; exact height equality is restored by the periodic
    /// world resync.
    /// </summary>
    public sealed class TerrainBrushCommand : ISimulationCommand
    {
        public const ushort Id = 6;

        public string BrushPrefabName;
        public float PosX, PosY, PosZ;
        public float Size;
        public float Angle;
        public float Strength;

        public ushort CommandId => Id;

        public void Write(NetworkWriter writer)
        {
            writer.WriteString(BrushPrefabName);
            writer.WriteFloat(PosX);
            writer.WriteFloat(PosY);
            writer.WriteFloat(PosZ);
            writer.WriteFloat(Size);
            writer.WriteFloat(Angle);
            writer.WriteFloat(Strength);
        }

        public void Read(NetworkReader reader)
        {
            BrushPrefabName = WireGuard.ReadName(reader);
            PosX = WireGuard.ReadCoordinate(reader);
            PosY = WireGuard.ReadCoordinate(reader);
            PosZ = WireGuard.ReadCoordinate(reader);
            Size = WireGuard.ReadFinite(reader);
            Angle = WireGuard.ReadFinite(reader);
            Strength = WireGuard.ReadFinite(reader);
            // A brush the size of the map or with absurd strength is an attack, not an edit.
            if (Size < 0f || Size > 10000f || Strength < -1000f || Strength > 1000f)
                throw new ProtocolException("Implausible brush parameters (size " + Size +
                                            ", strength " + Strength + ").");
        }

        public byte[] Encode()
        {
            var writer = new NetworkWriter(64);
            Write(writer);
            return writer.ToArray();
        }

        public static TerrainBrushCommand Decode(byte[] body)
        {
            var command = new TerrainBrushCommand();
            command.Read(new NetworkReader(body));
            return command;
        }
    }
}
