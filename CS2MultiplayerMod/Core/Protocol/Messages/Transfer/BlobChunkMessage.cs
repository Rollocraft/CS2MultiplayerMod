namespace CS2MultiplayerMod.Core.Protocol.Messages
{
    /// <summary>
    /// One slice of a named blob (e.g. a savegame), reassembled by <see cref="Channel"/> until
    /// <see cref="Last"/>; <see cref="TotalBytes"/> sizes the buffer and progress.
    /// </summary>
    public sealed class BlobChunkMessage : INetMessage
    {
        public string Channel;
        /// <summary>The recovery epoch for world snapshots; zero for uncoordinated blobs.</summary>
        public long TransferId;
        public int TotalBytes;
        public bool Last;
        public byte[] Data;
        private int _dataCount = -1;

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

        public BlobChunkMessage(string channel, long transferId, int totalBytes, bool last,
            byte[] data, int count) : this(channel, transferId, totalBytes, last, data)
        {
            if (count < 0 || count > Data.Length) throw new System.ArgumentOutOfRangeException(nameof(count));
            _dataCount = count;
        }

        public MessageType Type => MessageType.BlobChunk;

        public void Write(NetworkWriter writer)
        {
            writer.WriteString(Channel);
            writer.WriteLong(TransferId);
            writer.WriteInt(TotalBytes);
            writer.WriteBool(Last);
            int count = _dataCount >= 0 ? _dataCount : (Data != null ? Data.Length : 0);
            writer.WriteInt(count);
            if (count > 0) writer.WriteBytes(Data, 0, count);
        }

        public void Read(NetworkReader reader)
        {
            Channel = reader.ReadString();
            TransferId = reader.ReadLong();
            TotalBytes = reader.ReadInt();
            Last = reader.ReadBool();
            _dataCount = -1;
            int length = reader.ReadInt();
            if (length < 0 || length > ProtocolConstants.BlobChunkBytes)
                throw new ProtocolException("Invalid blob chunk length.");
            Data = length > 0 ? reader.ReadBytes(length) : System.Array.Empty<byte>();
        }
    }
}
