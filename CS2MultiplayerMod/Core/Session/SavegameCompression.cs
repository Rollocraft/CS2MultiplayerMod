using System;
using System.IO;
using System.IO.Compression;

namespace CS2MultiplayerMod.Core.Session
{
    /// <summary>
    /// Fast Deflate stream compression for large savegame blobs during initial joins and /sync.
    /// Prefixes compressed blobs with a 4-byte magic signature ("CSMZ") and original uncompressed
    /// length for transparent, backward-compatible decompression on receiving clients.
    /// </summary>
    public static class SavegameCompression
    {
        private static readonly byte[] Magic = new byte[] { 0x43, 0x53, 0x4D, 0x5A }; // "CSMZ"

        public static byte[] Compress(byte[] rawData)
        {
            if (rawData == null || rawData.Length == 0) return rawData;
            try
            {
                using (var output = new MemoryStream(rawData.Length / 2))
                {
                    output.Write(Magic, 0, 4);
                    // Write uncompressed length (little endian 32-bit int)
                    output.WriteByte((byte)(rawData.Length & 0xFF));
                    output.WriteByte((byte)((rawData.Length >> 8) & 0xFF));
                    output.WriteByte((byte)((rawData.Length >> 16) & 0xFF));
                    output.WriteByte((byte)((rawData.Length >> 24) & 0xFF));

                    using (var deflate = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true))
                    {
                        deflate.Write(rawData, 0, rawData.Length);
                    }
                    return output.ToArray();
                }
            }
            catch
            {
                // Fallback to raw uncompressed data on any compression failure
                return rawData;
            }
        }

        public static byte[] DecompressIfNeeded(byte[] data)
        {
            if (data == null || data.Length < 8) return data;

            // Check magic header "CSMZ"
            if (data[0] != Magic[0] || data[1] != Magic[1] || data[2] != Magic[2] || data[3] != Magic[3])
            {
                // Uncompressed raw data
                return data;
            }

            int uncompressedLength = data[4] | (data[5] << 8) | (data[6] << 16) | (data[7] << 24);
            if (uncompressedLength <= 0 || uncompressedLength > 256 * 1024 * 1024)
            {
                // Invalid length header, return raw data
                return data;
            }

            try
            {
                var result = new byte[uncompressedLength];
                using (var input = new MemoryStream(data, 8, data.Length - 8, writable: false))
                using (var deflate = new DeflateStream(input, CompressionMode.Decompress))
                {
                    int totalRead = 0;
                    while (totalRead < uncompressedLength)
                    {
                        int read = deflate.Read(result, totalRead, uncompressedLength - totalRead);
                        if (read <= 0) break;
                        totalRead += read;
                    }
                }
                return result;
            }
            catch
            {
                // If decompression fails, return raw data
                return data;
            }
        }
    }
}
