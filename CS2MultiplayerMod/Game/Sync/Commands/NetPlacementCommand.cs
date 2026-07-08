using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Sync;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>
    /// "A player drew this road (net segment) here." A road segment is a cubic Bézier
    /// (four control points) of a named net prefab. The receiver rebuilds it through the
    /// game's net-course definition pipeline - see <see cref="NetSyncSystem"/>.
    /// </summary>
    public sealed class NetPlacementCommand : ISimulationCommand
    {
        public const ushort Id = 2;

        public string PrefabName;

        // Cubic Bézier control points a → b → c → d (start, two handles, end).
        public float Ax, Ay, Az;
        public float Bx, By, Bz;
        public float Cx, Cy, Cz;
        public float Dx, Dy, Dz;
        public float Length;

        public ushort CommandId => Id;

        public void Write(NetworkWriter w)
        {
            w.WriteString(PrefabName);
            w.WriteFloat(Ax); w.WriteFloat(Ay); w.WriteFloat(Az);
            w.WriteFloat(Bx); w.WriteFloat(By); w.WriteFloat(Bz);
            w.WriteFloat(Cx); w.WriteFloat(Cy); w.WriteFloat(Cz);
            w.WriteFloat(Dx); w.WriteFloat(Dy); w.WriteFloat(Dz);
            w.WriteFloat(Length);
        }

        public void Read(NetworkReader r)
        {
            PrefabName = WireGuard.ReadName(r);
            Ax = WireGuard.ReadCoordinate(r); Ay = WireGuard.ReadCoordinate(r); Az = WireGuard.ReadCoordinate(r);
            Bx = WireGuard.ReadCoordinate(r); By = WireGuard.ReadCoordinate(r); Bz = WireGuard.ReadCoordinate(r);
            Cx = WireGuard.ReadCoordinate(r); Cy = WireGuard.ReadCoordinate(r); Cz = WireGuard.ReadCoordinate(r);
            Dx = WireGuard.ReadCoordinate(r); Dy = WireGuard.ReadCoordinate(r); Dz = WireGuard.ReadCoordinate(r);
            Length = WireGuard.ReadFinite(r);
        }

        public byte[] Encode()
        {
            var w = new NetworkWriter(96);
            Write(w);
            return w.ToArray();
        }

        public static NetPlacementCommand Decode(byte[] body)
        {
            var c = new NetPlacementCommand();
            c.Read(new NetworkReader(body));
            return c;
        }
    }
}
