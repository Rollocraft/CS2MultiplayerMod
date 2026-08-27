using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Sync;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>
    /// "A player relocated this object." The old position plus optional seed/attachment identity
    /// identifies the local entity, and the new transform plus attachment describes its destination
    /// - see <see cref="MoveSyncSystem"/>.
    ///
    /// For anything with owned geometry (a building's lot, driveways, installed upgrades), the
    /// receiver re-derives the whole relocation locally instead of moving the root alone. Roadside
    /// objects and buildings also carry their snapped net parent: the generator needs that control-
    /// point entity to update route lanes and both the old and new road compositions.
    /// </summary>
    public sealed class ObjectMoveCommand : ISimulationCommand
    {
        public const ushort Id = 8;

        public string PrefabName;
        public float OldX, OldY, OldZ;
        public float NewX, NewY, NewZ;
        public float RotX, RotY, RotZ, RotW;
        /// <summary>Control-point elevation: the height offset a bridge/elevated placement carries.</summary>
        public float Elevation;
        /// <summary>The moving tool's own seed; every per-definition seed is derived from it.</summary>
        public uint ToolRandomSeed;

        /// <summary>
        /// Stable identity of the existing object when it has one. Position remains the fallback for
        /// objects created without <c>PseudoRandomSeed</c>.
        /// </summary>
        public bool HasOriginalRandomSeed;
        public int OriginalRandomSeed;

        /// <summary>
        /// Set when the moved object is owned by another object - an installed upgrade or
        /// sub-building relocated from the building's upgrade list, which is the only relocation the
        /// base game offers. The host travels as prefab + position, the same identity
        /// <see cref="UpgradePlacementCommand"/> uses, because the moved entity is not a top-level
        /// object and cannot be found by position alone without risking a neighbour's upgrade.
        /// </summary>
        public bool HasOwnerIdentity;
        public string OwnerPrefabName;
        public float OwnerX, OwnerY, OwnerZ;

        /// <summary>
        /// Whether the sender could authoritatively classify the old/new attachment. "Known + None"
        /// is deliberately different from unknown: the former means the object is free-standing.
        /// </summary>
        public bool SourceAttachmentKnown;
        public ObjectAttachKind SourceAttachKind;
        public float SourceAttachX, SourceAttachY, SourceAttachZ;
        public bool DestinationAttachmentKnown;
        public ObjectAttachKind DestinationAttachKind;
        public float DestinationAttachX, DestinationAttachY, DestinationAttachZ;

        public ushort CommandId => Id;

        public void Write(NetworkWriter writer)
        {
            writer.WriteString(PrefabName);
            writer.WriteFloat(OldX); writer.WriteFloat(OldY); writer.WriteFloat(OldZ);
            writer.WriteFloat(NewX); writer.WriteFloat(NewY); writer.WriteFloat(NewZ);
            writer.WriteFloat(RotX); writer.WriteFloat(RotY); writer.WriteFloat(RotZ); writer.WriteFloat(RotW);
            writer.WriteFloat(Elevation);
            writer.WriteInt(unchecked((int)ToolRandomSeed));

            byte identityFlags = 0;
            if (HasOriginalRandomSeed) identityFlags |= 1;
            if (SourceAttachmentKnown) identityFlags |= 2;
            if (DestinationAttachmentKnown) identityFlags |= 4;
            if (HasOwnerIdentity) identityFlags |= 8;
            writer.WriteByte(identityFlags);
            if (HasOriginalRandomSeed) writer.WriteInt(OriginalRandomSeed);
            if (HasOwnerIdentity)
            {
                writer.WriteString(OwnerPrefabName);
                writer.WriteFloat(OwnerX); writer.WriteFloat(OwnerY); writer.WriteFloat(OwnerZ);
            }
            if (SourceAttachmentKnown)
                WriteAttachment(writer, SourceAttachKind,
                    SourceAttachX, SourceAttachY, SourceAttachZ);
            if (DestinationAttachmentKnown)
                WriteAttachment(writer, DestinationAttachKind,
                    DestinationAttachX, DestinationAttachY, DestinationAttachZ);
        }

        public void Read(NetworkReader reader)
        {
            PrefabName = WireGuard.ReadName(reader);
            OldX = WireGuard.ReadCoordinate(reader); OldY = WireGuard.ReadCoordinate(reader); OldZ = WireGuard.ReadCoordinate(reader);
            NewX = WireGuard.ReadCoordinate(reader); NewY = WireGuard.ReadCoordinate(reader); NewZ = WireGuard.ReadCoordinate(reader);
            RotX = WireGuard.ReadFinite(reader); RotY = WireGuard.ReadFinite(reader); RotZ = WireGuard.ReadFinite(reader); RotW = WireGuard.ReadFinite(reader);
            float rotationLengthSq = RotX * RotX + RotY * RotY + RotZ * RotZ + RotW * RotW;
            if (rotationLengthSq < 0.25f || rotationLengthSq > 2.25f)
                throw new ProtocolException("Implausible move rotation length " + rotationLengthSq + ".");
            Elevation = WireGuard.ReadCoordinate(reader);
            // The tool seed is opaque: every 32-bit value is legal input to the game's generator.
            ToolRandomSeed = unchecked((uint)reader.ReadInt());

            byte identityFlags = reader.ReadByte();
            if ((identityFlags & ~15) != 0)
                throw new ProtocolException("Unknown object-move identity flags " + identityFlags + ".");
            HasOriginalRandomSeed = (identityFlags & 1) != 0;
            SourceAttachmentKnown = (identityFlags & 2) != 0;
            DestinationAttachmentKnown = (identityFlags & 4) != 0;
            HasOwnerIdentity = (identityFlags & 8) != 0;
            if (HasOriginalRandomSeed) OriginalRandomSeed = reader.ReadInt();
            if (HasOwnerIdentity)
            {
                OwnerPrefabName = WireGuard.ReadName(reader);
                OwnerX = WireGuard.ReadCoordinate(reader);
                OwnerY = WireGuard.ReadCoordinate(reader);
                OwnerZ = WireGuard.ReadCoordinate(reader);
            }
            if (SourceAttachmentKnown)
                ReadAttachment(reader, out SourceAttachKind,
                    out SourceAttachX, out SourceAttachY, out SourceAttachZ);
            if (DestinationAttachmentKnown)
                ReadAttachment(reader, out DestinationAttachKind,
                    out DestinationAttachX, out DestinationAttachY, out DestinationAttachZ);
            if (reader.Remaining != 0)
                throw new ProtocolException("Trailing bytes in object-move command: " + reader.Remaining + ".");
        }

        public byte[] Encode()
        {
            var writer = new NetworkWriter(128);
            Write(writer);
            return writer.ToArray();
        }

        public static ObjectMoveCommand Decode(byte[] body)
        {
            var command = new ObjectMoveCommand();
            command.Read(new NetworkReader(body));
            return command;
        }

        private static void WriteAttachment(NetworkWriter writer, ObjectAttachKind kind,
            float x, float y, float z)
        {
            writer.WriteByte((byte)kind);
            if (kind == ObjectAttachKind.None) return;
            writer.WriteFloat(x);
            writer.WriteFloat(y);
            writer.WriteFloat(z);
        }

        private static void ReadAttachment(NetworkReader reader, out ObjectAttachKind kind,
            out float x, out float y, out float z)
        {
            byte wireKind = reader.ReadByte();
            if (wireKind > (byte)ObjectAttachKind.NetEdge)
                throw new ProtocolException("Unknown object-move attach kind " + wireKind + ".");
            kind = (ObjectAttachKind)wireKind;
            x = y = z = 0f;
            if (kind == ObjectAttachKind.None) return;
            x = WireGuard.ReadCoordinate(reader);
            y = WireGuard.ReadCoordinate(reader);
            z = WireGuard.ReadCoordinate(reader);
        }
    }
}
