using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Sync;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>
    /// "Something caught fire, here, this strongly." Carries a fire start the receiving
    /// game cannot derive for itself - which building or tree ignited and how hard -
    /// and never the burn itself. Each machine then runs the fire with its own
    /// simulation (escalation, spread, rescue, extinguish), so one small message covers
    /// a fire of any length. Same start-only shape as <see cref="DisasterEventCommand"/>.
    ///
    /// Only starts travel, in both directions: every machine rolls its own ignitions
    /// and reports them, so both cities converge on the union of fires. Ends stay local:
    /// each simulation extinguishes on its own clock, and the damage left behind is
    /// already host-authoritative through the growable condition sync.
    /// </summary>
    public sealed class FireIgniteCommand : ISimulationCommand
    {
        public const ushort Id = 33;
        public const int MaxEncodedBytes = 256;

        /// <summary>
        /// Ceiling on the ignition intensity. The game's own range is small; this only
        /// stops a forged unsurvivable inferno, it never constrains a real fire.
        /// </summary>
        public const float MaxIntensityValue = 1000f;

        /// <summary>Name of the ignited building or tree prefab, resolved locally by the receiver.</summary>
        public string PrefabName;

        /// <summary>World position of the ignited target (buildings do not move).</summary>
        public float X, Y, Z;

        /// <summary>Ignition strength as the sender's simulation rolled it.</summary>
        public float Intensity;

        public ushort CommandId => Id;

        public void Write(NetworkWriter writer)
        {
            ValidateForWrite();
            writer.WriteString(PrefabName);
            writer.WriteFloat(X); writer.WriteFloat(Y); writer.WriteFloat(Z);
            writer.WriteFloat(Intensity);
        }

        public void Read(NetworkReader reader)
        {
            PrefabName = WireGuard.ReadName(reader);
            X = WireGuard.ReadCoordinate(reader);
            Y = WireGuard.ReadCoordinate(reader);
            Z = WireGuard.ReadCoordinate(reader);
            Intensity = ReadIntensity(reader);

            if (reader.Remaining != 0)
                throw new ProtocolException("Trailing bytes in fire ignite: " + reader.Remaining + ".");
        }

        public byte[] Encode()
        {
            var writer = new NetworkWriter(96);
            Write(writer);
            if (writer.Length > MaxEncodedBytes)
                throw new ProtocolException("Fire ignite exceeds the " + MaxEncodedBytes + "-byte cap.");
            return writer.ToArray();
        }

        public static FireIgniteCommand Decode(byte[] body)
        {
            if (body == null)
                throw new ProtocolException("Missing fire ignite body.");
            if (body.Length > MaxEncodedBytes)
                throw new ProtocolException("Fire ignite exceeds the " + MaxEncodedBytes + "-byte cap.");
            var command = new FireIgniteCommand();
            command.Read(new NetworkReader(body));
            return command;
        }

        private void ValidateForWrite()
        {
            if (string.IsNullOrEmpty(PrefabName) || PrefabName.Length > WireGuard.MaxNameLength)
                throw new ProtocolException("Invalid fire target prefab name.");
            for (int i = 0; i < PrefabName.Length; i++)
                if (char.IsControl(PrefabName[i]))
                    throw new ProtocolException("Control character in fire target prefab name.");
            ValidateCoordinate(X, "X");
            ValidateCoordinate(Y, "Y");
            ValidateCoordinate(Z, "Z");
            ValidateIntensity(Intensity);
        }

        private static float ReadIntensity(NetworkReader reader)
        {
            float value = WireGuard.ReadFinite(reader);
            ValidateIntensity(value);
            return value;
        }

        private static void ValidateIntensity(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) ||
                value < 0f || value > MaxIntensityValue)
                throw new ProtocolException("Implausible fire intensity: " + value + ".");
        }

        private static void ValidateCoordinate(float value, string label)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) ||
                value < -WireGuard.MaxCoordinate || value > WireGuard.MaxCoordinate)
                throw new ProtocolException("Invalid fire coordinate " + label + ".");
        }
    }
}
