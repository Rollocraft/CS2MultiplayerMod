using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Sync;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>
    /// "A player bulldozed this road segment." Identified by prefab name + the segment's full
    /// cubic Bézier. The receiver deletes every local edge of that prefab whose endpoints lie on
    /// this curve, so a road the two machines subdivided differently still deletes completely - see
    /// <see cref="DeleteSyncSystem"/>.
    /// </summary>
    public sealed class NetDeleteCommand : ISimulationCommand
    {
        public const ushort Id = 4;

        public string PrefabName;
        // Cubic Bézier control points a → b → c → d (start, two handles, end).
        public float Ax, Ay, Az;
        public float Bx, By, Bz;
        public float Cx, Cy, Cz;
        public float Dx, Dy, Dz;

        public ushort CommandId => Id;

        public void Write(NetworkWriter writer)
        {
            writer.WriteString(PrefabName);
            writer.WriteFloat(Ax); writer.WriteFloat(Ay); writer.WriteFloat(Az);
            writer.WriteFloat(Bx); writer.WriteFloat(By); writer.WriteFloat(Bz);
            writer.WriteFloat(Cx); writer.WriteFloat(Cy); writer.WriteFloat(Cz);
            writer.WriteFloat(Dx); writer.WriteFloat(Dy); writer.WriteFloat(Dz);
        }

        public void Read(NetworkReader reader)
        {
            PrefabName = WireGuard.ReadName(reader);
            Ax = WireGuard.ReadCoordinate(reader); Ay = WireGuard.ReadCoordinate(reader); Az = WireGuard.ReadCoordinate(reader);
            Bx = WireGuard.ReadCoordinate(reader); By = WireGuard.ReadCoordinate(reader); Bz = WireGuard.ReadCoordinate(reader);
            Cx = WireGuard.ReadCoordinate(reader); Cy = WireGuard.ReadCoordinate(reader); Cz = WireGuard.ReadCoordinate(reader);
            Dx = WireGuard.ReadCoordinate(reader); Dy = WireGuard.ReadCoordinate(reader); Dz = WireGuard.ReadCoordinate(reader);
            if (reader.Remaining != 0)
                throw new ProtocolException("Trailing bytes in net-delete command: " + reader.Remaining + ".");
        }

        public byte[] Encode()
        {
            var writer = new NetworkWriter(96);
            Write(writer);
            return writer.ToArray();
        }

        public static NetDeleteCommand Decode(byte[] body)
        {
            var command = new NetDeleteCommand();
            command.Read(new NetworkReader(body));
            return command;
        }
    }
}
