using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Sync;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>"A player deleted this transport line" - matched by prefab + first waypoint.</summary>
    public sealed class RouteDeleteCommand : ISimulationCommand
    {
        public const ushort Id = 13;

        public string PrefabName;
        public float WaypointX, WaypointY, WaypointZ;

        public ushort CommandId => Id;

        public void Write(NetworkWriter writer)
        {
            writer.WriteString(PrefabName);
            writer.WriteFloat(WaypointX); writer.WriteFloat(WaypointY); writer.WriteFloat(WaypointZ);
        }

        public void Read(NetworkReader reader)
        {
            PrefabName = WireGuard.ReadName(reader);
            WaypointX = WireGuard.ReadCoordinate(reader); WaypointY = WireGuard.ReadCoordinate(reader); WaypointZ = WireGuard.ReadCoordinate(reader);
        }

        public byte[] Encode()
        {
            var writer = new NetworkWriter(48);
            Write(writer);
            return writer.ToArray();
        }

        public static RouteDeleteCommand Decode(byte[] body)
        {
            var command = new RouteDeleteCommand();
            command.Read(new NetworkReader(body));
            return command;
        }
    }
}
