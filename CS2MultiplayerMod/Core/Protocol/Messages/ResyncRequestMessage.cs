namespace CS2MultiplayerMod.Core.Protocol.Messages
{
    /// <summary>
    /// Client -> host: "stream current world now." Sent when player runs <c>/sync</c>
    /// due to suspected city drift. Host saves and streams live world - periodic
    /// resync but on demand.
    /// </summary>
    public sealed class ResyncRequestMessage : INetMessage
    {
        public int OriginPlayerId;

        public ResyncRequestMessage() { }

        public ResyncRequestMessage(int originPlayerId)
        {
            OriginPlayerId = originPlayerId;
        }

        public MessageType Type => MessageType.ResyncRequest;

        public void Write(NetworkWriter writer) => writer.WriteInt(OriginPlayerId);

        public void Read(NetworkReader reader) => OriginPlayerId = reader.ReadInt();
    }
}
