using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Sync;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>
    /// An in-place change of a segment's net prefab (unlike <see cref="NetUpgradeCommand"/>, which keeps
    /// it). The edge keeps its identity and surfaces only as <c>Updated</c>. Carries the curve before
    /// (<c>OldAx</c>...) and the committed curve after (<c>Ax</c>...).
    /// </summary>
    public sealed class NetReplaceCommand : ISimulationCommand
    {
        public const ushort Id = 19;

        /// <summary>The prefab the segment was replaced WITH.</summary>
        public string PrefabName;

        // Committed curve after the replacement: what the receiver must end with.
        public float Ax, Ay, Az;
        public float Bx, By, Bz;
        public float Cx, Cy, Cz;
        public float Dx, Dy, Dz;

        // Curve before the replacement: what the receiver's edges still lie on.
        public float OldAx, OldAy, OldAz;
        public float OldBx, OldBy, OldBz;
        public float OldCx, OldCy, OldCz;
        public float OldDx, OldDy, OldDz;
        // Direct mod geometry only matches the exact pre-edit edge, never a nearby road.
        public bool ExactGeometry;

        public ushort CommandId => Id;

        public void Write(NetworkWriter writer)
        {
            writer.WriteString(PrefabName);
            writer.WriteFloat(Ax); writer.WriteFloat(Ay); writer.WriteFloat(Az);
            writer.WriteFloat(Bx); writer.WriteFloat(By); writer.WriteFloat(Bz);
            writer.WriteFloat(Cx); writer.WriteFloat(Cy); writer.WriteFloat(Cz);
            writer.WriteFloat(Dx); writer.WriteFloat(Dy); writer.WriteFloat(Dz);
            writer.WriteFloat(OldAx); writer.WriteFloat(OldAy); writer.WriteFloat(OldAz);
            writer.WriteFloat(OldBx); writer.WriteFloat(OldBy); writer.WriteFloat(OldBz);
            writer.WriteFloat(OldCx); writer.WriteFloat(OldCy); writer.WriteFloat(OldCz);
            writer.WriteFloat(OldDx); writer.WriteFloat(OldDy); writer.WriteFloat(OldDz);
            writer.WriteBool(ExactGeometry);
        }

        public void Read(NetworkReader reader)
        {
            PrefabName = WireGuard.ReadName(reader);
            Ax = WireGuard.ReadCoordinate(reader); Ay = WireGuard.ReadCoordinate(reader); Az = WireGuard.ReadCoordinate(reader);
            Bx = WireGuard.ReadCoordinate(reader); By = WireGuard.ReadCoordinate(reader); Bz = WireGuard.ReadCoordinate(reader);
            Cx = WireGuard.ReadCoordinate(reader); Cy = WireGuard.ReadCoordinate(reader); Cz = WireGuard.ReadCoordinate(reader);
            Dx = WireGuard.ReadCoordinate(reader); Dy = WireGuard.ReadCoordinate(reader); Dz = WireGuard.ReadCoordinate(reader);
            OldAx = WireGuard.ReadCoordinate(reader); OldAy = WireGuard.ReadCoordinate(reader); OldAz = WireGuard.ReadCoordinate(reader);
            OldBx = WireGuard.ReadCoordinate(reader); OldBy = WireGuard.ReadCoordinate(reader); OldBz = WireGuard.ReadCoordinate(reader);
            OldCx = WireGuard.ReadCoordinate(reader); OldCy = WireGuard.ReadCoordinate(reader); OldCz = WireGuard.ReadCoordinate(reader);
            OldDx = WireGuard.ReadCoordinate(reader); OldDy = WireGuard.ReadCoordinate(reader); OldDz = WireGuard.ReadCoordinate(reader);
            ExactGeometry = reader.ReadBool();
        }

        public byte[] Encode()
        {
            var writer = new NetworkWriter(160);
            Write(writer);
            return writer.ToArray();
        }

        public static NetReplaceCommand Decode(byte[] body)
        {
            var command = new NetReplaceCommand();
            command.Read(new NetworkReader(body));
            return command;
        }
    }
}
