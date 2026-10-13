using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Sync;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>How a placed object hangs off the net. Wire values are fixed.</summary>
    public enum ObjectAttachKind : byte
    {
        /// <summary>A free-standing object: buildings, props, trees.</summary>
        None = 0,

        /// <summary>Parented to a road node (roundabout islands), sent as its world position.</summary>
        NetNode = 1,

        /// <summary>
        /// Parented to a road edge (turn restrictions, signs), sent as the centreline point it hangs at so
        /// differently subdivided roads still match.
        /// </summary>
        NetEdge = 2,
    }

    /// <summary>
    /// A placed object by prefab name and world transform, realized by the game's own creation systems.
    /// Net-attached objects also carry their parent anchor, which makes an island a roundabout and a
    /// sign a restriction.
    /// </summary>
    public sealed class ObjectPlacementCommand : ISimulationCommand
    {
        public const ushort Id = 1;

        public string PrefabName;
        public float PosX, PosY, PosZ;
        public float RotX, RotY, RotZ, RotW;
        public int RandomSeed;
        public float Age;

        public ObjectAttachKind AttachKind;
        public float AttachX, AttachY, AttachZ;

        public ushort CommandId => Id;

        public void Write(NetworkWriter writer)
        {
            writer.WriteString(PrefabName);
            writer.WriteFloat(PosX);
            writer.WriteFloat(PosY);
            writer.WriteFloat(PosZ);
            writer.WriteFloat(RotX);
            writer.WriteFloat(RotY);
            writer.WriteFloat(RotZ);
            writer.WriteFloat(RotW);
            writer.WriteInt(RandomSeed);
            writer.WriteFloat(Age);
            writer.WriteByte((byte)AttachKind);
            if (AttachKind == ObjectAttachKind.None) return;
            writer.WriteFloat(AttachX);
            writer.WriteFloat(AttachY);
            writer.WriteFloat(AttachZ);
        }

        public void Read(NetworkReader reader)
        {
            PrefabName = WireGuard.ReadName(reader);
            PosX = WireGuard.ReadCoordinate(reader);
            PosY = WireGuard.ReadCoordinate(reader);
            PosZ = WireGuard.ReadCoordinate(reader);
            RotX = WireGuard.ReadFinite(reader);
            RotY = WireGuard.ReadFinite(reader);
            RotZ = WireGuard.ReadFinite(reader);
            RotW = WireGuard.ReadFinite(reader);
            ValidateRotation(RotX, RotY, RotZ, RotW);
            RandomSeed = reader.ReadInt();
            if (RandomSeed < 0 || RandomSeed > ushort.MaxValue)
                throw new ProtocolException("Object random seed is outside ushort range.");
            Age = WireGuard.ReadFinite(reader);
            if (Age < 0f || Age > 1f)
                throw new ProtocolException("Object age is outside [0,1].");

            byte kind = reader.ReadByte();
            if (kind > (byte)ObjectAttachKind.NetEdge)
                throw new ProtocolException("Unknown object attach kind " + kind + ".");
            AttachKind = (ObjectAttachKind)kind;
            if (AttachKind != ObjectAttachKind.None)
            {
                AttachX = WireGuard.ReadCoordinate(reader);
                AttachY = WireGuard.ReadCoordinate(reader);
                AttachZ = WireGuard.ReadCoordinate(reader);
            }
            if (reader.Remaining != 0)
                throw new ProtocolException("Trailing bytes in object-placement command: " +
                                            reader.Remaining + ".");
        }

        public byte[] Encode()
        {
            var writer = new NetworkWriter(80);
            Write(writer);
            return writer.ToArray();
        }

        public static ObjectPlacementCommand Decode(byte[] body)
        {
            var command = new ObjectPlacementCommand();
            command.Read(new NetworkReader(body));
            return command;
        }

        private static void ValidateRotation(float x, float y, float z, float w)
        {
            float lengthSq = x * x + y * y + z * z + w * w;
            if (lengthSq < 0.25f || lengthSq > 2.25f)
                throw new ProtocolException("Implausible object rotation length " + lengthSq + ".");
        }
    }
}
