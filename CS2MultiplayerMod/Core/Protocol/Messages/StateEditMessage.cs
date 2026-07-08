namespace CS2MultiplayerMod.Core.Protocol.Messages
{
    /// <summary>
    /// Client's edit of player-editable state channel (taxes, policies, fees, ...).
    /// Body uses channel's snapshot encoding. Next <see cref="StateSnapshotMessage"/>
    /// confirms to everyone - host is single arbiter while every player can edit.
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
            writer.WriteInt(Data != null ? Data.Length : 0);
            if (Data != null && Data.Length > 0)
                writer.WriteBytes(Data, 0, Data.Length);
        }

        public void Read(NetworkReader reader)
        {
            OriginPlayerId = reader.ReadInt();
            ChannelId = reader.ReadByte();
            int length = reader.ReadInt();
            Data = length > 0 ? reader.ReadBytes(length) : System.Array.Empty<byte>();
        }
    }
}
