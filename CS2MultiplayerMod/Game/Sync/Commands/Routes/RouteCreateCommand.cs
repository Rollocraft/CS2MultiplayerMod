using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Sync;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>
    /// "A player created this transport line." Carries the complete route plus portable
    /// transport-stop identities; the receiver rebuilds the route-definition graph locally.
    /// </summary>
    public sealed class RouteCreateCommand : ISimulationCommand
    {
        public const ushort Id = 12;
        public const int MaxWaypoints = 512;
        public const int MaxEncodedBytes = 64 * 1024;

        public string PrefabName;
        public int RouteNumber;
        public bool IsComplete;
        public byte ColorR, ColorG, ColorB, ColorA;
        public RouteWaypointIntent[] Waypoints;

        public ushort CommandId => Id;

        public void Write(NetworkWriter writer)
        {
            RouteCommandCodec.ValidateRoute(PrefabName, RouteNumber, Waypoints, MaxWaypoints);
            writer.WriteString(PrefabName);
            writer.WriteInt(RouteNumber);
            writer.WriteBool(IsComplete);
            writer.WriteByte(ColorR); writer.WriteByte(ColorG); writer.WriteByte(ColorB); writer.WriteByte(ColorA);
            RouteCommandCodec.WriteWaypoints(writer, Waypoints);
        }

        public void Read(NetworkReader reader)
        {
            PrefabName = WireGuard.ReadName(reader);
            RouteNumber = reader.ReadInt();
            RouteCommandCodec.ValidateRouteNumber(RouteNumber);
            IsComplete = reader.ReadBool();
            ColorR = reader.ReadByte(); ColorG = reader.ReadByte(); ColorB = reader.ReadByte(); ColorA = reader.ReadByte();
            Waypoints = RouteCommandCodec.ReadWaypoints(reader, MaxWaypoints);
            RouteCommandCodec.RequireFullyRead(reader, "route-create");
        }

        public byte[] Encode()
        {
            var writer = new NetworkWriter(256);
            Write(writer);
            if (writer.Length > MaxEncodedBytes)
                throw new ProtocolException("Route-create command body " + writer.Length +
                                            " exceeds the " + MaxEncodedBytes + "-byte cap.");
            return writer.ToArray();
        }

        public static RouteCreateCommand Decode(byte[] body)
        {
            if (body == null) throw new ProtocolException("Null route-create command body.");
            if (body.Length > MaxEncodedBytes)
                throw new ProtocolException("Route-create command body " + body.Length +
                                            " exceeds the " + MaxEncodedBytes + "-byte cap.");
            var command = new RouteCreateCommand();
            command.Read(new NetworkReader(body));
            return command;
        }
    }
}
