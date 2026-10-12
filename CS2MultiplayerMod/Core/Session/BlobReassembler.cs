using System;
using CS2MultiplayerMod.Core.Protocol;

namespace CS2MultiplayerMod.Core.Session
{
    /// <summary>
    /// Accumulates one blob's chunks. Checks a fixed announced total, chunk size, chunk count, and an
    /// exact final byte count.
    /// </summary>
    internal sealed class BlobReassembler
    {
        private readonly byte[] _buffer;

        public BlobReassembler(int expectedBytes, long nowMs)
        {
            if (expectedBytes <= 0) throw new ProtocolException("Invalid blob size.");
            _buffer = new byte[expectedBytes];
            ExpectedBytes = expectedBytes;
            LastChunkAtMs = nowMs;
        }

        public int ExpectedBytes { get; }
        public int ReceivedBytes { get; private set; }
        public int ChunkCount { get; private set; }

        /// <summary>When most recent chunk arrived - lets owner expire stalled transfers.</summary>
        public long LastChunkAtMs { get; private set; }

        /// <summary>Maximum chunks this blob may consist of, derived from its announced size.</summary>
        public int MaxChunks => (ExpectedBytes / ProtocolConstants.BlobChunkBytes) + 2;

        /// <summary>Throws <see cref="ProtocolException"/> on any inconsistency; the caller drops the blob.</summary>
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

            if (length > ExpectedBytes - ReceivedBytes)
                throw new ProtocolException("Blob received " + ReceivedBytes +
                                            " bytes, more than the announced " + ExpectedBytes + ".");

            if (length > 0) Buffer.BlockCopy(data, 0, _buffer, ReceivedBytes, length);
            ReceivedBytes += length;
            LastChunkAtMs = nowMs;
        }

        /// <summary>Only on an exact byte count; a short or padded blob is a violation.</summary>
        public byte[] Complete()
        {
            if (ReceivedBytes != ExpectedBytes)
                throw new ProtocolException("Blob ended at " + ReceivedBytes + "/" + ExpectedBytes + " bytes.");
            return _buffer;
        }
    }
}
