namespace CS2MultiplayerMod.Core.Protocol.Messages
{
    /// <summary>
    /// A final, reliable explanation sent by the host before it deliberately closes a
    /// client's connection. This keeps administrative disconnects distinct from network
    /// failures and gives the client a useful message to display.
    /// </summary>
    public sealed class DisconnectNoticeMessage : INetMessage
    {
        public string Reason;

        /// <summary>
        /// True when the session simply ended (the host left the game or returned to the
        /// main menu) rather than this player being removed. The client reports a graceful
        /// notice as a normal end of session instead of a connection error.
        /// </summary>
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
