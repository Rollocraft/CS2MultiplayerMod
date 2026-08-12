using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Sync;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>
    /// "This is what this street / district / transport line / building is called." Carries either
    /// the player's typed name, the auto-name draw the game made when the entity appeared, or both -
    /// each field is optional, so a rename never overwrites an auto-name and vice versa.
    ///
    /// A name has two independent sources. A typed name is held by the game's naming system and
    /// applied through it. An untouched entity is named from its prefab's name list by an index
    /// drawn from a wall-clock seed, so every machine draws a different one for the same new street:
    /// the draw itself has to travel. See <see cref="NameSyncSystem"/>.
    /// </summary>
    public sealed class EntityNameCommand : ISimulationCommand
    {
        public const ushort Id = 26;

        /// <summary>Road aggregate - the entity a road's name and label belong to.</summary>
        public const byte KindStreet = 1;
        public const byte KindDistrict = 2;
        public const byte KindRoute = 3;

        /// <summary>Anything placed with a transform: buildings, parks, stops, upgrades.</summary>
        public const byte KindObject = 4;

        /// <summary>The name field is free text a player typed, so it is sanitized, not rejected.</summary>
        public const int MaxCustomNameLength = 96;

        /// <summary>One index per name slot the prefab declares; real prefabs use one or two.</summary>
        public const int MaxRandomIndices = 8;

        /// <summary>Highest auto-name index worth believing (name lists are dozens long).</summary>
        public const int MaxRandomIndexValue = 65535;

        public const int MaxEncodedBytes = 1024;

        public byte TargetKind;
        public string TargetPrefabName;
        public float AnchorX, AnchorY, AnchorZ;

        /// <summary>When false the receiver leaves any typed name alone.</summary>
        public bool SetsCustomName;

        /// <summary>Empty with <see cref="SetsCustomName"/> set means "the player cleared it".</summary>
        public string CustomName;

        /// <summary>Empty means "no auto-name draw in this command".</summary>
        public int[] RandomIndices;

        public ushort CommandId => Id;

        public void Write(NetworkWriter writer)
        {
            writer.WriteByte(TargetKind);
            writer.WriteString(TargetPrefabName);
            writer.WriteFloat(AnchorX); writer.WriteFloat(AnchorY); writer.WriteFloat(AnchorZ);
            writer.WriteBool(SetsCustomName);
            writer.WriteString(CustomName ?? string.Empty);
            int count = RandomIndices != null ? RandomIndices.Length : 0;
            if (count > MaxRandomIndices)
                throw new ProtocolException("Entity-name command carries " + count +
                                            " auto-name indices, more than the " +
                                            MaxRandomIndices + " supported.");
            writer.WriteShort((short)count);
            for (int i = 0; i < count; i++) writer.WriteInt(RandomIndices[i]);
        }

        public void Read(NetworkReader reader)
        {
            TargetKind = reader.ReadByte();
            if (TargetKind < KindStreet || TargetKind > KindObject)
                throw new ProtocolException("Unknown name target kind: " + TargetKind + ".");
            TargetPrefabName = WireGuard.ReadName(reader);
            AnchorX = WireGuard.ReadCoordinate(reader);
            AnchorY = WireGuard.ReadCoordinate(reader);
            AnchorZ = WireGuard.ReadCoordinate(reader);
            SetsCustomName = reader.ReadBool();
            CustomName = WireGuard.SanitizeText(reader.ReadString(), MaxCustomNameLength);
            int count = WireGuard.ReadCount(reader, 4, MaxRandomIndices);
            RandomIndices = count == 0 ? System.Array.Empty<int>() : new int[count];
            for (int i = 0; i < count; i++)
            {
                int index = reader.ReadInt();
                // -1 is the game's own "this slot has no names" value; anything past the cap is
                // either corrupt or forged, and would leave the label showing a raw locale key.
                if (index < -1 || index > MaxRandomIndexValue)
                    throw new ProtocolException("Auto-name index " + index + " out of range.");
                RandomIndices[i] = index;
            }
            if (!SetsCustomName && count == 0)
                throw new ProtocolException("Entity-name command carries no name at all.");
        }

        public byte[] Encode()
        {
            var writer = new NetworkWriter(192);
            Write(writer);
            if (writer.Length > MaxEncodedBytes)
                throw new ProtocolException("Entity-name command body " + writer.Length +
                                            " exceeds the " + MaxEncodedBytes + "-byte cap.");
            return writer.ToArray();
        }

        public static EntityNameCommand Decode(byte[] body)
        {
            if (body == null) throw new ProtocolException("Null entity-name command body.");
            if (body.Length > MaxEncodedBytes)
                throw new ProtocolException("Entity-name command body " + body.Length +
                                            " exceeds the " + MaxEncodedBytes + "-byte cap.");
            var command = new EntityNameCommand();
            command.Read(new NetworkReader(body));
            return command;
        }
    }
}
