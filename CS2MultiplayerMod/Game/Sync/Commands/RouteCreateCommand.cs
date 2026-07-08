using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Sync;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>
    /// "A player created this transport line." The route prefab travels by name, the
    /// line itself as the ordered waypoint positions; the receiver rebuilds it via the
    /// game's route-definition pipeline - see <see cref="RouteSyncSystem"/>.
    /// </summary>
    public sealed class RouteCreateCommand : ISimulationCommand
    {
        public const ushort Id = 12;

        public string PrefabName;
        public int RouteNumber;
        public byte ColorR, ColorG, ColorB, ColorA;
        public float[] WaypointX, WaypointY, WaypointZ;

        public ushort CommandId => Id;

        public void Write(NetworkWriter writer)
        {
            writer.WriteString(PrefabName);
            writer.WriteInt(RouteNumber);
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
            RouteNumber = reader.ReadInt();
            if (RouteNumber < 0 || RouteNumber > 100000)
                throw new ProtocolException("Implausible route number: " + RouteNumber + ".");
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
            var writer = new NetworkWriter(64 + (WaypointX != null ? WaypointX.Length * 12 : 0));
            Write(writer);
            return writer.ToArray();
        }

        public static RouteCreateCommand Decode(byte[] body)
        {
            var command = new RouteCreateCommand();
            command.Read(new NetworkReader(body));
            return command;
        }
    }
}
