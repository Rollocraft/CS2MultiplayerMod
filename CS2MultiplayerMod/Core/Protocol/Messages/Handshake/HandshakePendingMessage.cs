namespace CS2MultiplayerMod.Core.Protocol.Messages
{
    /// <summary>
    /// Host -> client acknowledgement that the join request reached the host and passed
    /// every automatic check, and is now waiting for the host to approve it by hand. It
    /// carries no fields: it only flips the client into its "awaiting approval" state so
    /// the join screen shows a matching message. The eventual accept/reject arrives as a
    /// <see cref="HandshakeResponse"/>.
    /// </summary>
    public sealed class HandshakePendingMessage : INetMessage
    {
        public HandshakePendingMessage() { }

        public MessageType Type => MessageType.HandshakePending;

        public void Write(NetworkWriter writer) { }

        public void Read(NetworkReader reader) { }
    }
}
