using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Sync;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>
    /// "A player edited this transport line" (stops, color, or number). The old route number
    /// and first waypoint identify the receiver's copy; the payload is the complete new state.
    /// </summary>
    public sealed class RouteUpdateCommand : ISimulationCommand
    {
        public const ushort Id = 17;
        public const int MaxWaypoints = RouteCreateCommand.MaxWaypoints;
        public const int MaxEncodedBytes = RouteCreateCommand.MaxEncodedBytes;

        public string PrefabName;
        public float AnchorX, AnchorY, AnchorZ;
        public int AnchorRouteNumber;
        public int RouteNumber;
        public bool IsComplete;
        public byte ColorR, ColorG, ColorB, ColorA;
        public RouteWaypointIntent[] Waypoints;

        public ushort CommandId => Id;

        public void Write(NetworkWriter writer)
        {
            RouteCommandCodec.ValidateRoute(PrefabName, RouteNumber, Waypoints, MaxWaypoints);
            RouteCommandCodec.ValidateRouteNumber(AnchorRouteNumber);
            RouteCommandCodec.ValidateAnchor(AnchorX, AnchorY, AnchorZ);
            writer.WriteString(PrefabName);
            writer.WriteFloat(AnchorX); writer.WriteFloat(AnchorY); writer.WriteFloat(AnchorZ);
            writer.WriteInt(AnchorRouteNumber);
            writer.WriteInt(RouteNumber);
            writer.WriteBool(IsComplete);
            writer.WriteByte(ColorR); writer.WriteByte(ColorG); writer.WriteByte(ColorB); writer.WriteByte(ColorA);
            RouteCommandCodec.WriteWaypoints(writer, Waypoints);
        }

        public void Read(NetworkReader reader)
        {
            PrefabName = WireGuard.ReadName(reader);
            AnchorX = WireGuard.ReadCoordinate(reader); AnchorY = WireGuard.ReadCoordinate(reader); AnchorZ = WireGuard.ReadCoordinate(reader);
            AnchorRouteNumber = reader.ReadInt();
            RouteCommandCodec.ValidateRouteNumber(AnchorRouteNumber);
            RouteNumber = reader.ReadInt();
            RouteCommandCodec.ValidateRouteNumber(RouteNumber);
            IsComplete = reader.ReadBool();
            ColorR = reader.ReadByte(); ColorG = reader.ReadByte(); ColorB = reader.ReadByte(); ColorA = reader.ReadByte();
            Waypoints = RouteCommandCodec.ReadWaypoints(reader, MaxWaypoints);
            RouteCommandCodec.RequireFullyRead(reader, "route-update");
        }

        public byte[] Encode()
        {
            var writer = new NetworkWriter(256);
            Write(writer);
            if (writer.Length > MaxEncodedBytes)
                throw new ProtocolException("Route-update command body " + writer.Length +
                                            " exceeds the " + MaxEncodedBytes + "-byte cap.");
            return writer.ToArray();
        }

        public static RouteUpdateCommand Decode(byte[] body)
        {
            if (body == null) throw new ProtocolException("Null route-update command body.");
            if (body.Length > MaxEncodedBytes)
                throw new ProtocolException("Route-update command body " + body.Length +
                                            " exceeds the " + MaxEncodedBytes + "-byte cap.");
            var command = new RouteUpdateCommand();
            command.Read(new NetworkReader(body));
            return command;
        }
    }
}
