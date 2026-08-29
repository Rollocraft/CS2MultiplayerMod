using System;
using CS2MultiplayerMod.Core.Networking;

namespace CS2MultiplayerMod.Core.Protocol
{
    /// <summary>
    /// Bitmask Run-Length Encoding (RLE) codec for compressing large 2D zoning block grids
    /// into compact byte arrays.
    /// High performance direct array traversal with zero-allocation overloads.
    /// </summary>
    public static class ZoneRleCodec
    {
        public static byte[] Encode(byte[] rawZones)
        {
            if (rawZones == null || rawZones.Length == 0) return Array.Empty<byte>();

            // In worst case (no consecutive duplicates), RLE output is 2 * input length.
            int maxLen = rawZones.Length * 2;
            byte[] temp = BufferPool.Rent(maxLen);
            try
            {
                Encode(rawZones, 0, rawZones.Length, temp, out int bytesWritten);
                byte[] result = new byte[bytesWritten];
                Buffer.BlockCopy(temp, 0, result, 0, bytesWritten);
                return result;
            }
            finally
            {
                BufferPool.Return(temp);
            }
        }

        public static void Encode(byte[] rawZones, int offset, int length, byte[] destination, out int bytesWritten)
        {
            bytesWritten = 0;
            if (rawZones == null || length <= 0 || destination == null) return;

            int end = offset + length;
            int i = offset;
            int outIdx = 0;

            while (i < end)
            {
                byte current = rawZones[i];
                byte count = 1;
                while (i + count < end && rawZones[i + count] == current && count < 255)
                {
                    count++;
                }

                destination[outIdx++] = count;
                destination[outIdx++] = current;
                i += count;
            }

            bytesWritten = outIdx;
        }

        public static byte[] Decode(byte[] compressed, int expectedLength)
        {
            if (compressed == null || compressed.Length == 0 || expectedLength <= 0) return Array.Empty<byte>();

            var output = new byte[expectedLength];
            Decode(compressed, 0, compressed.Length, output, out _);
            return output;
        }

        public static void Decode(byte[] compressed, int compressedOffset, int compressedLength, byte[] destination, out int bytesWritten)
        {
            bytesWritten = 0;
            if (compressed == null || compressedLength < 2 || destination == null) return;

            int inIdx = compressedOffset;
            int inEnd = compressedOffset + compressedLength;
            int outIdx = 0;
            int destLen = destination.Length;

            while (inIdx + 1 < inEnd && outIdx < destLen)
            {
                byte count = compressed[inIdx++];
                byte val = compressed[inIdx++];

                int writeCount = Math.Min((int)count, destLen - outIdx);
                for (int j = 0; j < writeCount; j++)
                {
                    destination[outIdx++] = val;
                }
            }

            bytesWritten = outIdx;
        }
    }
}
