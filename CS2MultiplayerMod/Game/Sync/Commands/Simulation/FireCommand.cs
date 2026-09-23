using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Sync;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    public enum FireOp : byte
    {
        /// <summary>A building or tree caught fire on the host: a start, a spread or a lightning strike.</summary>
        Ignite = 1,

        /// <summary>The host's fire on that target is out.</summary>
        Extinguish = 2,
    }

    /// <summary>
    /// Host to clients: one building or tree whose fire started or ended. Clients roll none of their own;
    /// each machine still runs the burn, the damage and the fire engines itself.
    /// </summary>
    public sealed class FireCommand : ISimulationCommand
    {
        public const ushort Id = 33;
        public const int MaxEncodedBytes = 512;

        /// <summary>The game's own ceiling on a fire's intensity.</summary>
        public const float MaxIntensity = 100f;

        public FireOp Op;

        /// <summary>The burning building or tree, found on the receiver by prefab and position.</summary>
        public string TargetPrefab;
        public float X, Y, Z;

        // --- Ignite ---

        /// <summary>The fire (or storm) event the ignition belongs to, resolved by prefab name.</summary>
        public string EventPrefab;

        /// <summary>
        /// The host's identity for that event, so later spreads join the fire the receiver created for the
        /// first ignition. Meaningful on the host only.
        /// </summary>
        public long EventKey;

        public float Intensity;

        public ushort CommandId => Id;

        public void Write(NetworkWriter writer)
        {
            ValidateForWrite();
            writer.WriteByte((byte)Op);
            writer.WriteString(TargetPrefab);
            writer.WriteFloat(X);
            writer.WriteFloat(Y);
            writer.WriteFloat(Z);
            if (Op != FireOp.Ignite) return;
            writer.WriteString(EventPrefab);
            writer.WriteLong(EventKey);
            writer.WriteFloat(Intensity);
        }

        public void Read(NetworkReader reader)
        {
            byte op = reader.ReadByte();
            if (op != (byte)FireOp.Ignite && op != (byte)FireOp.Extinguish)
                throw new ProtocolException("Unknown fire op " + op + ".");
            Op = (FireOp)op;
            TargetPrefab = WireGuard.ReadName(reader);
            X = WireGuard.ReadCoordinate(reader);
            Y = WireGuard.ReadCoordinate(reader);
            Z = WireGuard.ReadCoordinate(reader);
            if (Op == FireOp.Ignite)
            {
                EventPrefab = WireGuard.ReadName(reader);
                EventKey = reader.ReadLong();
                Intensity = WireGuard.ReadFinite(reader);
                ValidateIntensity(Intensity);
            }

            if (reader.Remaining != 0)
                throw new ProtocolException("Trailing bytes in fire command: " + reader.Remaining + ".");
        }

        public byte[] Encode()
        {
            var writer = new NetworkWriter(128);
            Write(writer);
            if (writer.Length > MaxEncodedBytes)
                throw new ProtocolException("Fire command exceeds the " + MaxEncodedBytes + "-byte cap.");
            return writer.ToArray();
        }

        public static FireCommand Decode(byte[] body)
        {
            if (body == null)
                throw new ProtocolException("Missing fire command body.");
            if (body.Length > MaxEncodedBytes)
                throw new ProtocolException("Fire command exceeds the " + MaxEncodedBytes + "-byte cap.");
            var command = new FireCommand();
            command.Read(new NetworkReader(body));
            return command;
        }

        private void ValidateForWrite()
        {
            if (Op != FireOp.Ignite && Op != FireOp.Extinguish)
                throw new ProtocolException("Unknown fire op " + (byte)Op + ".");
            ValidateName(TargetPrefab, "target");
            ValidateCoordinate(X);
            ValidateCoordinate(Y);
            ValidateCoordinate(Z);
            if (Op != FireOp.Ignite) return;
            ValidateName(EventPrefab, "event");
            ValidateIntensity(Intensity);
        }

        private static void ValidateName(string name, string label)
        {
            if (string.IsNullOrEmpty(name) || name.Length > WireGuard.MaxNameLength)
                throw new ProtocolException("Invalid fire " + label + " prefab name.");
            for (int i = 0; i < name.Length; i++)
                if (char.IsControl(name[i]))
                    throw new ProtocolException("Control character in fire " + label + " prefab name.");
        }

        private static void ValidateCoordinate(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) ||
                value < -WireGuard.MaxCoordinate || value > WireGuard.MaxCoordinate)
                throw new ProtocolException("Invalid fire target coordinate.");
        }

        private static void ValidateIntensity(float value)
        {
            if (float.IsNaN(value) || value < 0f || value > MaxIntensity)
                throw new ProtocolException("Implausible fire intensity: " + value + ".");
        }
    }
}
