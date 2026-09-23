namespace CS2MultiplayerMod.Core.Protocol.Messages
{
    /// <summary>Free text; also carries system notices such as joins and leaves.</summary>
    public sealed class ChatMessage : INetMessage
    {
        public string SenderName;
        public string Text;

        public ChatMessage() { }

        public ChatMessage(string senderName, string text)
        {
            SenderName = senderName;
            Text = text;
        }

        public MessageType Type => MessageType.Chat;

        public void Write(NetworkWriter writer)
        {
            writer.WriteString(SenderName);
            writer.WriteString(Text);
        }

        public void Read(NetworkReader reader)
        {
            SenderName = reader.ReadString();
            Text = reader.ReadString();
        }
    }
}
