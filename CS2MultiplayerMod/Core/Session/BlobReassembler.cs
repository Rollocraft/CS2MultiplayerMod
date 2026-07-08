using System.IO;
using CS2MultiplayerMod.Core.Protocol;

namespace CS2MultiplayerMod.Core.Session
{
    /// <summary>
    /// Accumulates the chunks of one incoming blob until the final chunk arrives, then
    /// yields the complete byte array. Every invariant a hostile sender could violate
    /// is checked here: the announced total must never change between chunks, no chunk
    /// may exceed the wire chunk size, the chunk count must match the announced total,
    /// and the byte count must land exactly on it. Kept tiny and game-free so the
    /// transfer logic is unit-testable.
    /// </summary>
    internal sealed class BlobReassembler
    {
        private readonly MemoryStream _buffer = new MemoryStream();

        public BlobReassembler(int expectedBytes, long nowMs)
        {
            ExpectedBytes = expectedBytes;
            LastChunkAtMs = nowMs;
        }

        public int ExpectedBytes { get; }
        public int ReceivedBytes { get { return (int)_buffer.Length; } }
        public int ChunkCount { get; private set; }

        /// <summary>When most recent chunk arrived - lets owner expire stalled transfers.</summary>
        public long LastChunkAtMs { get; private set; }

        /// <summary>Maximum chunks this blob may consist of, derived from its announced size.</summary>
        public int MaxChunks
        {
            get { return (ExpectedBytes / ProtocolConstants.BlobChunkBytes) + 2; }
        }

        /// <summary>
        /// Add one chunk. Throws <see cref="ProtocolException"/> on any inconsistency;
        /// the caller drops the whole blob (and may disconnect the sender).
        /// </summary>
        public void Append(int announcedTotal, byte[] data, long nowMs)
        {
            if (announcedTotal != ExpectedBytes)
                throw new ProtocolException("Blob total changed mid-transfer: " +
                                            ExpectedBytes + " -> " + announcedTotal + ".");

            int length = data != null ? data.Length : 0;
            if (length > ProtocolConstants.BlobChunkBytes)
                throw new ProtocolException("Blob chunk of " + length + " bytes exceeds the " +
                                            ProtocolConstants.BlobChunkBytes + "-byte chunk cap.");

            ChunkCount++;
            if (ChunkCount > MaxChunks)
                throw new ProtocolException("Blob exceeded its maximum of " + MaxChunks + " chunks.");

            if (length > 0) _buffer.Write(data, 0, length);
            if (ReceivedBytes > ExpectedBytes)
                throw new ProtocolException("Blob received " + ReceivedBytes +
                                            " bytes, more than the announced " + ExpectedBytes + ".");

            LastChunkAtMs = nowMs;
        }

        /// <summary>
        /// Finish the transfer: only valid when the byte count matches the announcement
        /// exactly. A short or padded blob is a protocol violation, not a best effort.
        /// </summary>
        public byte[] Complete()
        {
            if (ReceivedBytes != ExpectedBytes)
                throw new ProtocolException("Blob ended at " + ReceivedBytes + "/" + ExpectedBytes + " bytes.");
            return _buffer.ToArray();
        }
    }
}
