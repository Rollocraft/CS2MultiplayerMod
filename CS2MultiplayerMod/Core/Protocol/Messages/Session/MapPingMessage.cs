namespace CS2MultiplayerMod.Core.Protocol.Messages
{
    /// <summary>A temporary attention marker; identity is assigned by the host.</summary>
    public sealed class MapPingMessage : INetMessage
    {
        public int PlayerId;
        public string PlayerName = "Player";
        public float X, Y, Z;
        public MessageType Type => MessageType.MapPing;

        public void Write(NetworkWriter writer)
        {
            ValidatePosition(X, Y, Z);
            writer.WriteInt(PlayerId);
            writer.WriteString(WireGuard.SanitizePlayerName(PlayerName));
            writer.WriteFloat(X);
            writer.WriteFloat(Y);
            writer.WriteFloat(Z);
        }

        public void Read(NetworkReader reader)
        {
            PlayerId = reader.ReadInt();
            PlayerName = WireGuard.ReadName(reader);
            X = WireGuard.ReadCoordinate(reader);
            Y = WireGuard.ReadCoordinate(reader);
            Z = WireGuard.ReadCoordinate(reader);
        }

        public static void ValidatePosition(float x, float y, float z)
        {
            if (!Valid(x) || !Valid(y) || !Valid(z))
                throw new ProtocolException("Invalid map ping position.");
        }

        private static bool Valid(float value) => !float.IsNaN(value) &&
            !float.IsInfinity(value) && System.Math.Abs(value) <= WireGuard.MaxCoordinate;
    }
}
