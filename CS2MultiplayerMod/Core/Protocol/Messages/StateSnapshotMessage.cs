namespace CS2MultiplayerMod.Core.Protocol.Messages
{
    /// <summary>
    /// Host-authoritative replicated state slice. ChannelId selects game-side synchronizer
    /// (money, population, ...). Core treats body as opaque to keep protocol stable
    /// as state channels are added in game layer.
    /// </summary>
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
            writer.WriteInt(Data != null ? Data.Length : 0);
            if (Data != null && Data.Length > 0)
                writer.WriteBytes(Data, 0, Data.Length);
        }

        public void Read(NetworkReader reader)
        {
            ChannelId = reader.ReadByte();
            int length = reader.ReadInt();
            Data = length > 0 ? reader.ReadBytes(length) : System.Array.Empty<byte>();
        }
    }
}
