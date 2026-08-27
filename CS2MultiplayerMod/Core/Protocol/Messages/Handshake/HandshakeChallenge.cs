namespace CS2MultiplayerMod.Core.Protocol.Messages
{
    /// <summary>
    /// Host's first message to new connection: one-time random nonce. Client answers
    /// with <see cref="HandshakeRequest"/> whose proof is HMAC-SHA256(password, nonce +
    /// TLS cert hash). Raw password never crosses wire; replayed handshakes are invalid.
    /// </summary>
    public sealed class HandshakeChallenge : INetMessage
    {
        public int ProtocolVersion;
        public bool PasswordRequired;
        public byte[] Nonce;

        public HandshakeChallenge() { }

        public HandshakeChallenge(int protocolVersion, bool passwordRequired, byte[] nonce)
        {
            ProtocolVersion = protocolVersion;
            PasswordRequired = passwordRequired;
            Nonce = nonce ?? System.Array.Empty<byte>();
        }

        public MessageType Type => MessageType.HandshakeChallenge;

        public void Write(NetworkWriter writer)
        {
            writer.WriteInt(ProtocolVersion);
            writer.WriteBool(PasswordRequired);
            writer.WriteInt(Nonce != null ? Nonce.Length : 0);
            if (Nonce != null && Nonce.Length > 0)
                writer.WriteBytes(Nonce, 0, Nonce.Length);
        }

        public void Read(NetworkReader reader)
        {
            ProtocolVersion = reader.ReadInt();
            PasswordRequired = reader.ReadBool();
            int length = reader.ReadInt();
            if (length < 0 || length > 64)
                throw new ProtocolException("Implausible nonce length: " + length + ".");
            Nonce = length > 0 ? reader.ReadBytes(length) : System.Array.Empty<byte>();
        }
    }
}
