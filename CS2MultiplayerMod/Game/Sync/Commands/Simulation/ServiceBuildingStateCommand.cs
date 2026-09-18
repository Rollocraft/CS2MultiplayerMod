using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Sync;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>
    /// "This service building's state changed." Carries the abandonment, condemnation or
    /// destruction markers of a NON-spawnable building - power plants, fire stations,
    /// hospitals, signature buildings - the part of building state no other pipeline owns:
    /// growable lifecycle sync only watches spawnables, and delete sync only watches removals.
    /// A burned-down power plant that stays pristine on the peer is exactly the divergence
    /// this closes: the ruin provides no coverage there while it provides none here either.
    ///
    /// Marker gain and marker loss both travel (repairs converge too); removal itself stays
    /// with delete sync, which replicates every non-spawnable removal already.
    /// </summary>
    public sealed class ServiceBuildingStateCommand : ISimulationCommand
    {
        public const ushort Id = 34;
        public const int MaxEncodedBytes = 256;

        public const byte StateAbandoned = 1 << 0;
        public const byte StateCondemned = 1 << 1;
        public const byte StateDestroyed = 1 << 2;

        /// <summary>Name of the standing building's prefab, resolved locally by the receiver.</summary>
        public string PrefabName;

        /// <summary>World position of the building (service buildings do not move).</summary>
        public float X, Y, Z;

        /// <summary>Resulting marker set; zero means "all markers cleared".</summary>
        public byte StateFlags;

        public ushort CommandId => Id;

        public void Write(NetworkWriter writer)
        {
            ValidateForWrite();
            writer.WriteString(PrefabName);
            writer.WriteFloat(X); writer.WriteFloat(Y); writer.WriteFloat(Z);
            writer.WriteByte(StateFlags);
        }

        public void Read(NetworkReader reader)
        {
            PrefabName = WireGuard.ReadName(reader);
            X = WireGuard.ReadCoordinate(reader);
            Y = WireGuard.ReadCoordinate(reader);
            Z = WireGuard.ReadCoordinate(reader);
            StateFlags = ReadStateFlags(reader);

            if (reader.Remaining != 0)
                throw new ProtocolException("Trailing bytes in service building state: " +
                    reader.Remaining + ".");
        }

        public byte[] Encode()
        {
            var writer = new NetworkWriter(64);
            Write(writer);
            if (writer.Length > MaxEncodedBytes)
                throw new ProtocolException("Service building state exceeds the " +
                    MaxEncodedBytes + "-byte cap.");
            return writer.ToArray();
        }

        public static ServiceBuildingStateCommand Decode(byte[] body)
        {
            if (body == null)
                throw new ProtocolException("Missing service building state body.");
            if (body.Length > MaxEncodedBytes)
                throw new ProtocolException("Service building state exceeds the " +
                    MaxEncodedBytes + "-byte cap.");
            var command = new ServiceBuildingStateCommand();
            command.Read(new NetworkReader(body));
            return command;
        }

        private void ValidateForWrite()
        {
            if (string.IsNullOrEmpty(PrefabName) || PrefabName.Length > WireGuard.MaxNameLength)
                throw new ProtocolException("Invalid service building prefab name.");
            for (int i = 0; i < PrefabName.Length; i++)
                if (char.IsControl(PrefabName[i]))
                    throw new ProtocolException("Control character in service building prefab name.");
            ValidateCoordinate(X, "X");
            ValidateCoordinate(Y, "Y");
            ValidateCoordinate(Z, "Z");
            ValidateStateFlags(StateFlags);
        }

        private static byte ReadStateFlags(NetworkReader reader)
        {
            byte value = reader.ReadByte();
            ValidateStateFlags(value);
            return value;
        }

        private static void ValidateStateFlags(byte value)
        {
            if ((value & ~(StateAbandoned | StateCondemned | StateDestroyed)) != 0)
                throw new ProtocolException("Unknown service building state flags: " + value + ".");
        }

        private static void ValidateCoordinate(float value, string label)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) ||
                value < -WireGuard.MaxCoordinate || value > WireGuard.MaxCoordinate)
                throw new ProtocolException("Invalid service building coordinate " + label + ".");
        }
    }
}
