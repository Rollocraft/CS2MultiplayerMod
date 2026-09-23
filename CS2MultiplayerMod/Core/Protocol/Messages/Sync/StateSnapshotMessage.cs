namespace CS2MultiplayerMod.Core.Protocol.Messages
{
    /// <summary>A host state slice; ChannelId picks the game-side channel, the body is opaque to Core.</summary>
    public sealed class StateSnapshotMessage : INetMessage
    {
        public byte ChannelId;
        public byte[] Data;

        public StateSnapshotMessage() { }

        public StateSnapshotMessage(byte channelId, byte[] data)
        {
            ChannelId = channelId;
            Data = data ?? System.Array.Empty<byte>();
        }

        public MessageType Type => MessageType.StateSnapshot;

        public void Write(NetworkWriter writer)
        {
            writer.WriteByte(ChannelId);
            writer.WriteLengthPrefixedBytes(Data);
        }

        public void Read(NetworkReader reader)
        {
            ChannelId = reader.ReadByte();
            Data = reader.ReadRemainingLengthPrefixedBytes("State snapshot");
        }
    }
}
