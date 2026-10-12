namespace CS2MultiplayerMod.Core.Protocol.Messages
{
    /// <summary>
    /// Camera focus and eye position plus bounded hover outlines, relayed by the host. Lossy: only the
    /// latest value matters.
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
        public PlayerHoverShape[] Hover = System.Array.Empty<PlayerHoverShape>();

        public PlayerStateMessage() { }

        public PlayerStateMessage(int playerId,
            float posX, float posY, float posZ,
            float eyeX, float eyeY, float eyeZ, float yaw, PlayerHoverShape[] hover = null)
        {
            PlayerId = playerId;
            PosX = posX;
            PosY = posY;
            PosZ = posZ;
            EyeX = eyeX;
            EyeY = eyeY;
            EyeZ = eyeZ;
            Yaw = yaw;
            Hover = hover ?? System.Array.Empty<PlayerHoverShape>();
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
            if (Hover == null || Hover.Length > PlayerHoverShape.MaxShapes)
                throw new ProtocolException("Too many hover shapes.");
            writer.WriteByte((byte)Hover.Length);
            foreach (PlayerHoverShape shape in Hover)
            {
                shape.Validate();
                shape.Write(writer);
            }
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
            int count = reader.ReadByte();
            if (count > PlayerHoverShape.MaxShapes || count * PlayerHoverShape.WireSize > reader.Remaining)
                throw new ProtocolException("Invalid hover shape count.");
            Hover = count == 0 ? System.Array.Empty<PlayerHoverShape>() : new PlayerHoverShape[count];
            for (int i = 0; i < count; i++) Hover[i] = PlayerHoverShape.Read(reader);
        }
    }
}
