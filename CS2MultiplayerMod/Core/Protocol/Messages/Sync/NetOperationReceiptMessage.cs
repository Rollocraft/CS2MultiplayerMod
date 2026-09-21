namespace CS2MultiplayerMod.Core.Protocol.Messages
{
    /// <summary>Client -> host receipt for the local realization of one atomic net operation.</summary>
    public sealed class NetOperationReceiptMessage : INetMessage
    {
        public int OriginPlayerId;
        public long OperationId;
        public bool Applied;
        public string Detail;
        public MessageType Type => MessageType.NetOperationReceipt;

        public NetOperationReceiptMessage() { }
        public NetOperationReceiptMessage(int originPlayerId, long operationId, bool applied, string detail = null)
        { OriginPlayerId = originPlayerId; OperationId = operationId; Applied = applied; Detail = detail; }
        public void Write(NetworkWriter writer)
        {
            writer.WriteInt(OriginPlayerId); writer.WriteLong(OperationId); writer.WriteBool(Applied);
            writer.WriteString(WireGuard.SanitizeText(Detail, 256));
        }
        public void Read(NetworkReader reader)
        {
            OriginPlayerId = reader.ReadInt(); OperationId = reader.ReadLong(); Applied = reader.ReadBool();
            Detail = WireGuard.SanitizeText(reader.ReadString(), 256);
            if (OperationId <= 0) throw new ProtocolException("Invalid net-operation receipt id.");
        }
    }
}
