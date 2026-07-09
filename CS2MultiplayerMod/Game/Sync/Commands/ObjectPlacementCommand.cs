using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Sync;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>How a placed object hangs off the net. Wire values are fixed.</summary>
    public enum ObjectAttachKind : byte
    {
        /// <summary>A free-standing object: buildings, props, trees.</summary>
        None = 0,

        /// <summary>
        /// A net object parented to a road node - roundabout central islands. The node
        /// travels as a world position; entity indices differ per machine.
        /// </summary>
        NetNode = 1,

        /// <summary>
        /// A net object parented to a road edge - turn restrictions, signs, markings. Travels
        /// as the point on the edge's centreline the object hangs at, so a receiver that
        /// subdivided the road differently still finds the piece under it.
        /// </summary>
        NetEdge = 2,
    }

    /// <summary>
    /// "A player placed this object here." Identifies the prefab by its stable name
    /// (entity indices differ per machine, names do not) plus a world transform. The
    /// receiver resolves the name back to a local prefab and lets the game's own
    /// object-creation systems realize it - see <see cref="BuildSyncSystem"/>.
    ///
    /// Objects that attach to the net carry an anchor on their parent as well. That link is
    /// what turns a roundabout island into an actual roundabout, or a sign into a real turn
    /// restriction, and it cannot be recovered from the object's own transform.
    /// </summary>
    public sealed class ObjectPlacementCommand : ISimulationCommand
    {
        public const ushort Id = 1;

        public string PrefabName;
        public float PosX, PosY, PosZ;
        public float RotX, RotY, RotZ, RotW;

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

            byte kind = reader.ReadByte();
            if (kind > (byte)ObjectAttachKind.NetEdge)
                throw new ProtocolException("Unknown object attach kind " + kind + ".");
            AttachKind = (ObjectAttachKind)kind;
            if (AttachKind == ObjectAttachKind.None) return;

            AttachX = WireGuard.ReadCoordinate(reader);
            AttachY = WireGuard.ReadCoordinate(reader);
            AttachZ = WireGuard.ReadCoordinate(reader);
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
    }
}
