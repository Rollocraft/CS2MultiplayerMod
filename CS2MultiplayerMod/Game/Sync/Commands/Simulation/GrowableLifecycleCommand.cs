using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Sync;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>
    /// A zone-grown building's spawn, level change, state change or removal; the choice is a per-machine
    /// random draw, so it travels. One command id keeps them ordered: a remove that overtook its spawn
    /// would leave the building forever.
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

        /// <summary>Host-assigned and monotonic; keys the receiver's idempotence window.</summary>
        public uint Sequence;

        /// <summary>
        /// Spawn and level: the prefab to become. Remove and state: the prefab standing there, which separates
        /// buildings sharing a lot corner.
        /// </summary>
        public string PrefabName;

        /// <summary>The building's transform position; lot and block indices are not portable.</summary>
        public float AnchorX, AnchorY, AnchorZ;

        public float RotX, RotY, RotZ, RotW;

        /// <summary>Seeds <c>PseudoRandomSeed</c>, which picks the visual variant.</summary>
        public ushort RandomSeed;

        public byte Flags;

        /// <summary>
        /// Exact construction clock: speed is a per-machine draw. State updates carry it too, so a lost spawn
        /// converges and host completion is authoritative.
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

            // Spawn and level need a prefab; a nameless remove would match any nearby building.
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
