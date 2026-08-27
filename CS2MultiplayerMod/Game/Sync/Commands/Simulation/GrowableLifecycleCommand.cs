using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Sync;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>
    /// One event in a zone-grown building's life: it appeared, it changed level, its condition
    /// changed, or it went away. Zoned buildings are chosen from a per-machine random stream, so
    /// nothing about them can be re-derived by a peer - the choice itself has to travel.
    ///
    /// All four events share one command id so they arrive in the order the host produced them:
    /// a remove that overtook its own spawn would leave a building standing forever.
    /// </summary>
    public sealed class GrowableLifecycleCommand : ISimulationCommand
    {
        public const ushort Id = 27;

        /// <summary>A building grew on a vacant lot.</summary>
        public const byte OpSpawn = 1;

        /// <summary>An existing building is becoming <see cref="PrefabName"/> (level up or down).</summary>
        public const byte OpLevel = 2;

        /// <summary>The building at the anchor is gone.</summary>
        public const byte OpRemove = 3;

        /// <summary>Condition/abandonment changed without the building appearing or leaving.</summary>
        public const byte OpState = 4;

        public const byte StateAbandoned = 1 << 0;
        public const byte StateCondemned = 1 << 1;
        public const byte StateDestroyed = 1 << 2;

        /// <summary>Set on a spawn whose building starts behind construction scaffolding.</summary>
        public const byte FlagUnderConstruction = 1 << 0;

        public const int MaxEncodedBytes = 512;

        public byte Op;

        /// <summary>
        /// Host-assigned, monotonic. The receiver's idempotence window is keyed on this, so a
        /// redelivered command is recognised without having to compare its contents.
        /// </summary>
        public uint Sequence;

        /// <summary>
        /// Spawn and level carry the prefab to become; remove and state carry the prefab standing
        /// there now, which is what disambiguates two buildings sharing a lot corner.
        /// </summary>
        public string PrefabName;

        /// <summary>
        /// World position of the building's own transform. Lot and block indices are rebuilt from
        /// each machine's own road geometry and are not portable; the position is.
        /// </summary>
        public float AnchorX, AnchorY, AnchorZ;

        public float RotX, RotY, RotZ, RotW;

        /// <summary>
        /// Seeds the created building's <c>PseudoRandomSeed</c>, which is what picks the visual
        /// variant. Without it the same prefab renders as a different house on each machine.
        /// </summary>
        public ushort RandomSeed;

        public byte Flags;

        /// <summary>
        /// Exact native construction clock. Speed is a per-machine random draw, so carrying only
        /// <see cref="FlagUnderConstruction"/> makes roughly half the clients finish after the
        /// host and half before it. State updates also carry these fields so a lost/late spawn
        /// still converges and host completion is authoritative.
        /// </summary>
        public byte ConstructionProgress;
        public byte ConstructionSpeed;

        /// <summary>Level-up progress; carried so a corrected building does not restart at zero.</summary>
        public int Condition;

        public byte StateFlags;

        public ushort CommandId => Id;

        public void Write(NetworkWriter writer)
        {
            writer.WriteByte(Op);
            writer.WriteInt((int)Sequence);
            writer.WriteString(PrefabName ?? string.Empty);
            writer.WriteFloat(AnchorX); writer.WriteFloat(AnchorY); writer.WriteFloat(AnchorZ);
            if (Op == OpSpawn)
            {
                writer.WriteFloat(RotX); writer.WriteFloat(RotY);
                writer.WriteFloat(RotZ); writer.WriteFloat(RotW);
                writer.WriteShort(unchecked((short)RandomSeed));
            }
            if (Op == OpSpawn || Op == OpLevel || Op == OpState)
            {
                writer.WriteByte(Flags);
                writer.WriteByte(ConstructionProgress);
                writer.WriteByte(ConstructionSpeed);
                writer.WriteInt(Condition);
                writer.WriteByte(StateFlags);
            }
        }

        public void Read(NetworkReader reader)
        {
            Op = reader.ReadByte();
            if (Op < OpSpawn || Op > OpState)
                throw new ProtocolException("Unknown growable lifecycle op: " + Op + ".");
            Sequence = unchecked((uint)reader.ReadInt());
            PrefabName = WireGuard.ReadName(reader);
            AnchorX = WireGuard.ReadCoordinate(reader);
            AnchorY = WireGuard.ReadCoordinate(reader);
            AnchorZ = WireGuard.ReadCoordinate(reader);

            // A spawn and a level change name the prefab to build; without one there is nothing
            // to create, and a nameless remove would match any building near the anchor.
            if ((Op == OpSpawn || Op == OpLevel) && string.IsNullOrEmpty(PrefabName))
                throw new ProtocolException("Growable " +
                    (Op == OpSpawn ? "spawn" : "level change") + " carries no prefab.");

            if (Op == OpSpawn)
            {
                RotX = WireGuard.ReadFinite(reader);
                RotY = WireGuard.ReadFinite(reader);
                RotZ = WireGuard.ReadFinite(reader);
                RotW = WireGuard.ReadFinite(reader);
                RandomSeed = unchecked((ushort)reader.ReadShort());
            }
            if (Op == OpSpawn || Op == OpLevel || Op == OpState)
            {
                Flags = reader.ReadByte();
                if ((Flags & ~FlagUnderConstruction) != 0)
                    throw new ProtocolException("Unknown growable lifecycle flags: " + Flags + ".");
                ConstructionProgress = reader.ReadByte();
                ConstructionSpeed = reader.ReadByte();
                Condition = reader.ReadInt();
                StateFlags = reader.ReadByte();
                if ((StateFlags & ~(StateAbandoned | StateCondemned | StateDestroyed)) != 0)
                    throw new ProtocolException("Unknown growable state flags: " + StateFlags + ".");
            }
        }

        public byte[] Encode()
        {
            var writer = new NetworkWriter(128);
            Write(writer);
            if (writer.Length > MaxEncodedBytes)
                throw new ProtocolException("Growable lifecycle command body " + writer.Length +
                                            " exceeds the " + MaxEncodedBytes + "-byte cap.");
            return writer.ToArray();
        }

        public static GrowableLifecycleCommand Decode(byte[] body)
        {
            if (body == null) throw new ProtocolException("Null growable lifecycle command body.");
            if (body.Length > MaxEncodedBytes)
                throw new ProtocolException("Growable lifecycle command body " + body.Length +
                                            " exceeds the " + MaxEncodedBytes + "-byte cap.");
            var command = new GrowableLifecycleCommand();
            command.Read(new NetworkReader(body));
            return command;
        }

        public static string OpName(byte op) =>
            op == OpSpawn ? "spawn" :
            op == OpLevel ? "level" :
            op == OpRemove ? "remove" :
            op == OpState ? "state" : "op" + op;
    }
}
