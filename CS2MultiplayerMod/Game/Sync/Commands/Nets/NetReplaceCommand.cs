using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Sync;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>
    /// "A player replaced this road segment's TYPE in place" - drew a different net prefab over an
    /// existing edge (e.g. a two-lane road over a one-lane one). Unlike a composition
    /// <see cref="NetUpgradeCommand"/> (trees/sidewalks, same prefab) this changes the edge's
    /// <c>PrefabRef</c>; unlike a placement/delete the edge keeps its identity, so it surfaces only
    /// as an <c>Updated</c> tag - see <see cref="Systems.NetReplaceSyncSystem"/>.
    /// The command carries TWO full cubic Béziers: <c>OldAx</c>... is the segment's curve BEFORE
    /// the replacement; <c>Ax</c>... is the COMMITTED curve AFTER the replacement.
    /// </summary>
    public sealed class NetReplaceCommand : ISimulationCommand
    {
        public const ushort Id = 19;

        /// <summary>The prefab the segment was replaced WITH.</summary>
        public string PrefabName;

        // Cubic Bézier control points a → b → c → d (start, two handles, end) of the segment as
        // COMMITTED by the replacement — the geometry the receiver must end up with.
        public float Ax, Ay, Az;
        public float Bx, By, Bz;
        public float Cx, Cy, Cz;
        public float Dx, Dy, Dz;

        // The same segment's curve BEFORE the replacement (the sender's baseline) — the geometry
        // the receiver's edges still lie on, used to find them.
        public float OldAx, OldAy, OldAz;
        public float OldBx, OldBy, OldBz;
        public float OldCx, OldCy, OldCz;
        public float OldDx, OldDy, OldDz;

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
