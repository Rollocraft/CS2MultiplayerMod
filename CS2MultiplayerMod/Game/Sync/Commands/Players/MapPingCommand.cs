using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Sync;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>
    /// "Look here." A transient beacon a player drops on the map for the others, with an
    /// optional short note. It changes nothing in the city, so it is never replayed, never
    /// snapshotted, and losing one is not a reason to do anything at all.
    ///
    /// It travels as a command rather than as chat text so the sender's identity comes from
    /// the message envelope the session already authenticates. A ping encoded into a chat line
    /// would be authored by whoever typed it, which means anyone could drop a marker signed
    /// with someone else's name - and every chat line would have to be parsed to find out
    /// whether it was one.
    /// </summary>
    public sealed class MapPingCommand : ISimulationCommand
    {
        public const ushort Id = 29;

        /// <summary>Free text the sender typed; sanitized rather than rejected, like chat.</summary>
        public const int MaxLabelLength = 48;

        public const int MaxEncodedBytes = 128;

        public float X, Y, Z;
        public string Label;

        public ushort CommandId => Id;

        public void Write(NetworkWriter writer)
        {
            writer.WriteFloat(X);
            writer.WriteFloat(Y);
            writer.WriteFloat(Z);
            writer.WriteString(Label ?? string.Empty);
        }

        public void Read(NetworkReader reader)
        {
            X = WireGuard.ReadCoordinate(reader);
            Y = WireGuard.ReadCoordinate(reader);
            Z = WireGuard.ReadCoordinate(reader);
            Label = WireGuard.SanitizeText(reader.ReadString(), MaxLabelLength);
            if (reader.Remaining != 0)
                throw new ProtocolException("Trailing bytes in map-ping command.");
        }

        public byte[] Encode()
        {
            var writer = new NetworkWriter(MaxEncodedBytes);
            Write(writer);
            if (writer.Length > MaxEncodedBytes)
                throw new ProtocolException("Map-ping command exceeds its size limit.");
            return writer.ToArray();
        }

        public static MapPingCommand Decode(byte[] body)
        {
            if (body == null || body.Length > MaxEncodedBytes)
                throw new ProtocolException("Map-ping command exceeds its size limit.");
            var command = new MapPingCommand();
            command.Read(new NetworkReader(body));
            return command;
        }
    }
}
