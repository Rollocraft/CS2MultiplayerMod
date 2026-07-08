using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Sync;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>
    /// "A player edited this transport line" (added/removed stops, recolored). The anchor
    /// is the OLD first waypoint (still matching the receiver's line); the payload is the
    /// complete new state - see <see cref="RouteSyncSystem"/>.
    /// </summary>
    public sealed class RouteUpdateCommand : ISimulationCommand
    {
        public const ushort Id = 17;

        public string PrefabName;
        public float AnchorX, AnchorY, AnchorZ;
        public byte ColorR, ColorG, ColorB, ColorA;
        public float[] WaypointX, WaypointY, WaypointZ;

        public ushort CommandId => Id;

        public void Write(NetworkWriter writer)
        {
            writer.WriteString(PrefabName);
            writer.WriteFloat(AnchorX); writer.WriteFloat(AnchorY); writer.WriteFloat(AnchorZ);
            writer.WriteByte(ColorR); writer.WriteByte(ColorG); writer.WriteByte(ColorB); writer.WriteByte(ColorA);
            int count = WaypointX != null ? WaypointX.Length : 0;
            writer.WriteShort((short)count);
            for (int i = 0; i < count; i++)
            {
                writer.WriteFloat(WaypointX[i]);
                writer.WriteFloat(WaypointY[i]);
                writer.WriteFloat(WaypointZ[i]);
            }
        }

        public void Read(NetworkReader reader)
        {
            PrefabName = WireGuard.ReadName(reader);
            AnchorX = WireGuard.ReadCoordinate(reader); AnchorY = WireGuard.ReadCoordinate(reader); AnchorZ = WireGuard.ReadCoordinate(reader);
            ColorR = reader.ReadByte(); ColorG = reader.ReadByte(); ColorB = reader.ReadByte(); ColorA = reader.ReadByte();
            int count = WireGuard.ReadCount(reader, 12);
            WaypointX = new float[count]; WaypointY = new float[count]; WaypointZ = new float[count];
            for (int i = 0; i < count; i++)
            {
                WaypointX[i] = WireGuard.ReadCoordinate(reader);
                WaypointY[i] = WireGuard.ReadCoordinate(reader);
                WaypointZ[i] = WireGuard.ReadCoordinate(reader);
            }
        }

        public byte[] Encode()
        {
            var writer = new NetworkWriter(80 + (WaypointX != null ? WaypointX.Length * 12 : 0));
            Write(writer);
            return writer.ToArray();
        }

        public static RouteUpdateCommand Decode(byte[] body)
        {
            var command = new RouteUpdateCommand();
            command.Read(new NetworkReader(body));
            return command;
        }
    }
}
