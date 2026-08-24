namespace CS2MultiplayerMod.Core.Protocol.Messages
{
    /// <summary>
    /// One slice of a large named byte stream (a "blob"), e.g. a savegame for map sync.
    /// Blobs are split into chunks because a whole save can be many megabytes; the
    /// receiver reassembles them by <see cref="Channel"/> and is notified once
    /// <see cref="Last"/> arrives. <see cref="TotalBytes"/> lets the receiver show
    /// progress and pre-size its buffer.
    /// </summary>
    public sealed class BlobChunkMessage : INetMessage
    {
        public string Channel;
        /// <summary>
        /// Identity of this transfer. World snapshots use their recovery epoch; zero is
        /// reserved for ordinary, uncoordinated blobs.
        /// </summary>
        public long TransferId;
        public int TotalBytes;
        public bool Last;
        public byte[] Data;

        public BlobChunkMessage() { }

        public BlobChunkMessage(string channel, int totalBytes, bool last, byte[] data)
            : this(channel, 0, totalBytes, last, data)
        {
        }

        public BlobChunkMessage(string channel, long transferId, int totalBytes, bool last, byte[] data)
        {
            Channel = channel;
            TransferId = transferId;
            TotalBytes = totalBytes;
            Last = last;
            Data = data ?? System.Array.Empty<byte>();
        }

        public MessageType Type => MessageType.BlobChunk;

        public void Write(NetworkWriter writer)
        {
            writer.WriteString(Channel);
            writer.WriteLong(TransferId);
            writer.WriteInt(TotalBytes);
            writer.WriteBool(Last);
            writer.WriteInt(Data != null ? Data.Length : 0);
            if (Data != null && Data.Length > 0)
                writer.WriteBytes(Data, 0, Data.Length);
        }

        public void Read(NetworkReader reader)
        {
            Channel = reader.ReadString();
            TransferId = reader.ReadLong();
            TotalBytes = reader.ReadInt();
            Last = reader.ReadBool();
            int length = reader.ReadInt();
            Data = length > 0 ? reader.ReadBytes(length) : System.Array.Empty<byte>();
        }
    }
}
