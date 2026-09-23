namespace CS2MultiplayerMod.Core.Protocol.Messages
{
    /// <summary>
    /// No fields: the join passed every automatic check and waits for the host; the verdict comes as
    /// a <see cref="HandshakeResponse"/>.
    /// </summary>
    public sealed class HandshakePendingMessage : INetMessage
    {
        public HandshakePendingMessage() { }

        public MessageType Type => MessageType.HandshakePending;

        public void Write(NetworkWriter writer) { }

        public void Read(NetworkReader reader) { }
    }
}
