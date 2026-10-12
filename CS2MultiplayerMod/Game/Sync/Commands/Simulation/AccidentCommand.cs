using System.Collections.Generic;
using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Sync;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>
    /// Host to clients: a traffic accident the host's game rolled, as its event prefab and the road(s) it
    /// happens on. Clients roll none of their own; the crash itself runs with each machine's own traffic.
    /// </summary>
    public sealed class AccidentCommand : ISimulationCommand
    {
        public const ushort Id = 34;
        public const int MaxEncodedBytes = 2048;

        /// <summary>An accident site is one road; a few spare slots cover a crash across a junction.</summary>
        public const int MaxRoads = 8;

        public struct Road
        {
            /// <summary>The road's net prefab.</summary>
            public string Prefab;

            /// <summary>The midpoint of the road segment's curve.</summary>
            public float X, Y, Z;
        }

        /// <summary>The traffic-accident event prefab, resolved by name.</summary>
        public string EventPrefab;

        public readonly List<Road> Roads = new List<Road>();

        public ushort CommandId => Id;

        public void Write(NetworkWriter writer)
        {
            ValidateForWrite();
            writer.WriteString(EventPrefab);
            writer.WriteByte((byte)Roads.Count);
            for (int i = 0; i < Roads.Count; i++)
            {
                writer.WriteString(Roads[i].Prefab);
                writer.WriteFloat(Roads[i].X);
                writer.WriteFloat(Roads[i].Y);
                writer.WriteFloat(Roads[i].Z);
            }
        }

        public void Read(NetworkReader reader)
        {
            EventPrefab = WireGuard.ReadName(reader);
            int count = reader.ReadByte();
            if (count == 0 || count > MaxRoads)
                throw new ProtocolException("Accident road count " + count + " outside 1.." + MaxRoads + ".");
            Roads.Clear();
            for (int i = 0; i < count; i++)
            {
                Roads.Add(new Road
                {
                    Prefab = WireGuard.ReadName(reader),
                    X = WireGuard.ReadCoordinate(reader),
                    Y = WireGuard.ReadCoordinate(reader),
                    Z = WireGuard.ReadCoordinate(reader),
                });
            }

            if (reader.Remaining != 0)
                throw new ProtocolException("Trailing bytes in accident command: " + reader.Remaining + ".");
        }

        public byte[] Encode()
        {
            var writer = new NetworkWriter(128);
            Write(writer);
            if (writer.Length > MaxEncodedBytes)
                throw new ProtocolException("Accident command exceeds the " + MaxEncodedBytes + "-byte cap.");
            return writer.ToArray();
        }

        public static AccidentCommand Decode(byte[] body)
        {
            if (body == null)
                throw new ProtocolException("Missing accident command body.");
            if (body.Length > MaxEncodedBytes)
                throw new ProtocolException("Accident command exceeds the " + MaxEncodedBytes + "-byte cap.");
            var command = new AccidentCommand();
            command.Read(new NetworkReader(body));
            return command;
        }

        private void ValidateForWrite()
        {
            ValidateName(EventPrefab, "event");
            if (Roads.Count == 0 || Roads.Count > MaxRoads)
                throw new ProtocolException("Accident road count " + Roads.Count + " outside 1.." + MaxRoads + ".");
            for (int i = 0; i < Roads.Count; i++)
            {
                ValidateName(Roads[i].Prefab, "road");
                ValidateCoordinate(Roads[i].X);
                ValidateCoordinate(Roads[i].Y);
                ValidateCoordinate(Roads[i].Z);
            }
        }

        private static void ValidateName(string name, string label)
        {
            if (string.IsNullOrEmpty(name) || name.Length > WireGuard.MaxNameLength)
                throw new ProtocolException("Invalid accident " + label + " prefab name.");
            for (int i = 0; i < name.Length; i++)
                if (char.IsControl(name[i]))
                    throw new ProtocolException("Control character in accident " + label + " prefab name.");
        }

        private static void ValidateCoordinate(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) ||
                value < -WireGuard.MaxCoordinate || value > WireGuard.MaxCoordinate)
                throw new ProtocolException("Invalid accident road coordinate.");
        }
    }
}
