using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Sync;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>Which family of disaster event a <see cref="DisasterEventCommand"/> describes.</summary>
    public enum DisasterKind : byte
    {
        /// <summary>Tornado, hailstorm, thunderstorm - a moving hotspot over a fixed area.</summary>
        WeatherPhenomenon = 1,

        /// <summary>Tsunami / sea-level surge - a global water-height curve.</summary>
        WaterLevelChange = 2,
    }

    /// <summary>
    /// "A natural disaster started, here, like this." Carries only the state the receiving game
    /// cannot derive for itself - where, how big, how long, how strong - and never the per-frame
    /// path. Each machine then runs the event with its own simulation, so one small message covers
    /// a disaster of any length.
    ///
    /// Timings are frame counts relative to the receiver's own <c>frameIndex</c>, never absolute
    /// frames: each machine keeps an independent simulation frame counter (the in-game clock is
    /// aligned by re-anchoring <c>TimeData.m_FirstFrame</c> instead), so a sender's absolute
    /// start/end frame would land anywhere on the receiver.
    /// </summary>
    public sealed class DisasterEventCommand : ISimulationCommand
    {
        public const ushort Id = 24;
        public const int MaxEncodedBytes = 512;

        /// <summary>
        /// Ceiling on the start delay and the duration: one in-game day of simulation frames.
        /// Real events run for seconds to minutes; this only stops a forged "never ends" event.
        /// </summary>
        public const int MaxFrames = 262144;

        /// <summary>Ceiling on radii and timers, in metres and seconds respectively.</summary>
        public const float MaxExtent = 100000f;

        /// <summary>Ceiling on the water-surge intensity (the game's own range is 0..1).</summary>
        public const float MaxIntensityValue = 1000f;

        public DisasterKind Kind;

        /// <summary>Name of the event prefab, resolved to a local prefab entity by the receiver.</summary>
        public string PrefabName;

        /// <summary>Frames until the event turns active (the disaster-warning window).</summary>
        public int StartDelayFrames;

        /// <summary>Frames the event stays active once started.</summary>
        public int DurationFrames;

        // --- WeatherPhenomenon ---
        public float PhenomenonX, PhenomenonY, PhenomenonZ;
        public float HotspotX, HotspotY, HotspotZ;
        public float PhenomenonRadius, HotspotRadius, LightningTimer;

        // --- WaterLevelChange ---
        public float MaxIntensity, DangerHeight, DirectionX, DirectionZ;

        public ushort CommandId => Id;

        public void Write(NetworkWriter writer)
        {
            ValidateForWrite();
            writer.WriteByte((byte)Kind);
            writer.WriteString(PrefabName);
            writer.WriteInt(StartDelayFrames);
            writer.WriteInt(DurationFrames);
            if (Kind == DisasterKind.WeatherPhenomenon)
            {
                writer.WriteFloat(PhenomenonX);
                writer.WriteFloat(PhenomenonY);
                writer.WriteFloat(PhenomenonZ);
                writer.WriteFloat(HotspotX);
                writer.WriteFloat(HotspotY);
                writer.WriteFloat(HotspotZ);
                writer.WriteFloat(PhenomenonRadius);
                writer.WriteFloat(HotspotRadius);
                writer.WriteFloat(LightningTimer);
            }
            else
            {
                writer.WriteFloat(MaxIntensity);
                writer.WriteFloat(DangerHeight);
                writer.WriteFloat(DirectionX);
                writer.WriteFloat(DirectionZ);
            }
        }

        public void Read(NetworkReader reader)
        {
            byte kind = reader.ReadByte();
            if (kind != (byte)DisasterKind.WeatherPhenomenon && kind != (byte)DisasterKind.WaterLevelChange)
                throw new ProtocolException("Unknown disaster kind " + kind + ".");
            Kind = (DisasterKind)kind;

            PrefabName = WireGuard.ReadName(reader);
            StartDelayFrames = ReadFrames(reader, "start delay");
            DurationFrames = ReadFrames(reader, "duration");

            if (Kind == DisasterKind.WeatherPhenomenon)
            {
                PhenomenonX = WireGuard.ReadCoordinate(reader);
                PhenomenonY = WireGuard.ReadCoordinate(reader);
                PhenomenonZ = WireGuard.ReadCoordinate(reader);
                HotspotX = WireGuard.ReadCoordinate(reader);
                HotspotY = WireGuard.ReadCoordinate(reader);
                HotspotZ = WireGuard.ReadCoordinate(reader);
                PhenomenonRadius = ReadExtent(reader, "phenomenon radius");
                HotspotRadius = ReadExtent(reader, "hotspot radius");
                LightningTimer = ReadExtent(reader, "lightning timer");
            }
            else
            {
                MaxIntensity = ReadIntensity(reader);
                DangerHeight = WireGuard.ReadCoordinate(reader);
                DirectionX = ReadDirectionComponent(reader);
                DirectionZ = ReadDirectionComponent(reader);
            }

            if (reader.Remaining != 0)
                throw new ProtocolException("Trailing bytes in disaster event: " + reader.Remaining + ".");
        }

        public byte[] Encode()
        {
            var writer = new NetworkWriter(96);
            Write(writer);
            if (writer.Length > MaxEncodedBytes)
                throw new ProtocolException("Disaster event exceeds the " + MaxEncodedBytes + "-byte cap.");
            return writer.ToArray();
        }

        public static DisasterEventCommand Decode(byte[] body)
        {
            if (body == null)
                throw new ProtocolException("Missing disaster event body.");
            if (body.Length > MaxEncodedBytes)
                throw new ProtocolException("Disaster event exceeds the " + MaxEncodedBytes + "-byte cap.");
            var command = new DisasterEventCommand();
            command.Read(new NetworkReader(body));
            return command;
        }

        private void ValidateForWrite()
        {
            if (Kind != DisasterKind.WeatherPhenomenon && Kind != DisasterKind.WaterLevelChange)
                throw new ProtocolException("Unknown disaster kind " + (byte)Kind + ".");
            if (string.IsNullOrEmpty(PrefabName) || PrefabName.Length > WireGuard.MaxNameLength)
                throw new ProtocolException("Invalid disaster prefab name.");
            for (int i = 0; i < PrefabName.Length; i++)
                if (char.IsControl(PrefabName[i]))
                    throw new ProtocolException("Control character in disaster prefab name.");

            ValidateFrames(StartDelayFrames, "start delay");
            ValidateFrames(DurationFrames, "duration");

            if (Kind == DisasterKind.WeatherPhenomenon)
            {
                ValidateCoordinate(PhenomenonX, "phenomenon X");
                ValidateCoordinate(PhenomenonY, "phenomenon Y");
                ValidateCoordinate(PhenomenonZ, "phenomenon Z");
                ValidateCoordinate(HotspotX, "hotspot X");
                ValidateCoordinate(HotspotY, "hotspot Y");
                ValidateCoordinate(HotspotZ, "hotspot Z");
                ValidateExtent(PhenomenonRadius, "phenomenon radius");
                ValidateExtent(HotspotRadius, "hotspot radius");
                ValidateExtent(LightningTimer, "lightning timer");
            }
            else
            {
                if (float.IsNaN(MaxIntensity) || float.IsInfinity(MaxIntensity) ||
                    MaxIntensity < 0f || MaxIntensity > MaxIntensityValue)
                    throw new ProtocolException("Implausible water-surge intensity.");
                ValidateCoordinate(DangerHeight, "danger height");
                ValidateDirectionComponent(DirectionX);
                ValidateDirectionComponent(DirectionZ);
            }
        }

        private static int ReadFrames(NetworkReader reader, string label)
        {
            int value = reader.ReadInt();
            ValidateFrames(value, label);
            return value;
        }

        private static void ValidateFrames(int value, string label)
        {
            if (value < 0 || value > MaxFrames)
                throw new ProtocolException("Disaster " + label + " of " + value +
                                            " frame(s) outside 0.." + MaxFrames + ".");
        }

        private static float ReadExtent(NetworkReader reader, string label)
        {
            float value = WireGuard.ReadFinite(reader);
            ValidateExtent(value, label);
            return value;
        }

        private static void ValidateExtent(float value, string label)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) || value < 0f || value > MaxExtent)
                throw new ProtocolException("Implausible disaster " + label + ": " + value + ".");
        }

        private static float ReadIntensity(NetworkReader reader)
        {
            float value = WireGuard.ReadFinite(reader);
            if (value < 0f || value > MaxIntensityValue)
                throw new ProtocolException("Implausible water-surge intensity: " + value + ".");
            return value;
        }

        private static float ReadDirectionComponent(NetworkReader reader)
        {
            float value = WireGuard.ReadFinite(reader);
            ValidateDirectionComponent(value);
            return value;
        }

        private static void ValidateDirectionComponent(float value)
        {
            // The game stores a unit-ish direction; anything far outside that is forged.
            if (float.IsNaN(value) || float.IsInfinity(value) || value < -16f || value > 16f)
                throw new ProtocolException("Implausible water-surge direction component: " + value + ".");
        }

        private static void ValidateCoordinate(float value, string label)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) ||
                value < -WireGuard.MaxCoordinate || value > WireGuard.MaxCoordinate)
                throw new ProtocolException("Invalid disaster " + label + " coordinate.");
        }
    }
}
