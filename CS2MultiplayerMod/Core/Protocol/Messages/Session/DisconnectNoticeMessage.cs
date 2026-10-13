namespace CS2MultiplayerMod.Core.Protocol.Messages
{
    /// <summary>The host's reason for closing a client's connection, distinct from a network failure.</summary>
    public sealed class DisconnectNoticeMessage : INetMessage
    {
        public string Reason;

        /// <summary>The session simply ended (host left); reported as a normal end, not an error.</summary>
        public bool Graceful;

        public DisconnectNoticeMessage() { }

        public DisconnectNoticeMessage(string reason, bool graceful = false)
        {
            Reason = reason;
            Graceful = graceful;
        }

        public MessageType Type => MessageType.DisconnectNotice;

        public void Write(NetworkWriter writer)
        {
            writer.WriteString(Reason);
            writer.WriteBool(Graceful);
        }

        public void Read(NetworkReader reader)
        {
            Reason = reader.ReadString();
            Graceful = reader.ReadBool();
        }
    }
}
