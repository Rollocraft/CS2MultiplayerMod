using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Sync;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>A deleted line by prefab + number; the first waypoint is a guarded fallback.</summary>
    public sealed class RouteDeleteCommand : ISimulationCommand
    {
        public const ushort Id = 13;
        public const int MaxEncodedBytes = 512;

        public string PrefabName;
        public int RouteNumber;
        public float WaypointX, WaypointY, WaypointZ;

        public ushort CommandId => Id;

        public void Write(NetworkWriter writer)
        {
            RouteCommandCodec.ValidateName(PrefabName, "route prefab");
            RouteCommandCodec.ValidateRouteNumber(RouteNumber);
            RouteCommandCodec.ValidateAnchor(WaypointX, WaypointY, WaypointZ);
            writer.WriteString(PrefabName);
            writer.WriteInt(RouteNumber);
            writer.WriteFloat(WaypointX); writer.WriteFloat(WaypointY); writer.WriteFloat(WaypointZ);
        }

        public void Read(NetworkReader reader)
        {
            PrefabName = WireGuard.ReadName(reader);
            RouteNumber = reader.ReadInt();
            RouteCommandCodec.ValidateRouteNumber(RouteNumber);
            WaypointX = WireGuard.ReadCoordinate(reader); WaypointY = WireGuard.ReadCoordinate(reader); WaypointZ = WireGuard.ReadCoordinate(reader);
            RouteCommandCodec.RequireFullyRead(reader, "route-delete");
        }

        public byte[] Encode()
        {
            var writer = new NetworkWriter(48);
            Write(writer);
            if (writer.Length > MaxEncodedBytes)
                throw new ProtocolException("Route-delete command body exceeds the " +
                                            MaxEncodedBytes + "-byte cap.");
            return writer.ToArray();
        }

        public static RouteDeleteCommand Decode(byte[] body)
        {
            if (body == null) throw new ProtocolException("Null route-delete command body.");
            if (body.Length > MaxEncodedBytes)
                throw new ProtocolException("Route-delete command body exceeds the " +
                                            MaxEncodedBytes + "-byte cap.");
            var command = new RouteDeleteCommand();
            command.Read(new NetworkReader(body));
            return command;
        }
    }
}
