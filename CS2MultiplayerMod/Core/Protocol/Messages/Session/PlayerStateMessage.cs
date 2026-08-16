namespace CS2MultiplayerMod.Core.Protocol.Messages
{
    /// <summary>
    /// Player's camera focus (ground) and eye position (air) so others draw where they're
    /// looking and flying height. Sent frequently, relayed by host. Lossy by design -
    /// only latest value matters, dropped updates harmless.
    /// </summary>
    public sealed class PlayerStateMessage : INetMessage
    {
        public int PlayerId;
        // Camera focus point on the ground (the spot the player is looking at).
        public float PosX;
        public float PosY;
        public float PosZ;
        // Camera eye position in the air (where the player actually is).
        public float EyeX;
        public float EyeY;
        public float EyeZ;
        public float Yaw;

        public PlayerStateMessage() { }

        public PlayerStateMessage(int playerId,
            float posX, float posY, float posZ,
            float eyeX, float eyeY, float eyeZ, float yaw)
        {
            PlayerId = playerId;
            PosX = posX;
            PosY = posY;
            PosZ = posZ;
            EyeX = eyeX;
            EyeY = eyeY;
            EyeZ = eyeZ;
            Yaw = yaw;
        }

        public MessageType Type => MessageType.PlayerState;

        public void Write(NetworkWriter writer)
        {
            writer.WriteInt(PlayerId);
            writer.WriteFloat(PosX);
            writer.WriteFloat(PosY);
            writer.WriteFloat(PosZ);
            writer.WriteFloat(EyeX);
            writer.WriteFloat(EyeY);
            writer.WriteFloat(EyeZ);
            writer.WriteFloat(Yaw);
        }

        public void Read(NetworkReader reader)
        {
            PlayerId = reader.ReadInt();
            PosX = WireGuard.ReadCoordinate(reader);
            PosY = WireGuard.ReadCoordinate(reader);
            PosZ = WireGuard.ReadCoordinate(reader);
            EyeX = WireGuard.ReadCoordinate(reader);
            EyeY = WireGuard.ReadCoordinate(reader);
            EyeZ = WireGuard.ReadCoordinate(reader);
            Yaw = WireGuard.ReadFinite(reader);
        }
    }
}
