namespace CS2MultiplayerMod.Core.Protocol.Messages
{
    /// <summary>
    /// A client's edit of an editable channel, in the channel's snapshot encoding; the next
    /// <see cref="StateSnapshotMessage"/> confirms it.
    /// </summary>
    public sealed class StateEditMessage : INetMessage
    {
        public int OriginPlayerId;
        public byte ChannelId;
        public byte[] Data;

        public StateEditMessage() { }

        public StateEditMessage(int originPlayerId, byte channelId, byte[] data)
        {
            OriginPlayerId = originPlayerId;
            ChannelId = channelId;
            Data = data ?? System.Array.Empty<byte>();
        }

        public MessageType Type => MessageType.StateEdit;

        public void Write(NetworkWriter writer)
        {
            writer.WriteInt(OriginPlayerId);
            writer.WriteByte(ChannelId);
            writer.WriteLengthPrefixedBytes(Data);
        }

        public void Read(NetworkReader reader)
        {
            OriginPlayerId = reader.ReadInt();
            ChannelId = reader.ReadByte();
            Data = reader.ReadRemainingLengthPrefixedBytes("State edit");
        }
    }
}
