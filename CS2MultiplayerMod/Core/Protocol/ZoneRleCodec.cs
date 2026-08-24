using System;
using System.IO;

namespace CS2MultiplayerMod.Core.Protocol
{
    /// <summary>
    /// Bitmask Run-Length Encoding (RLE) codec for compressing large 2D zoning block grids
    /// into compact byte arrays.
    /// </summary>
    public static class ZoneRleCodec
    {
        public static byte[] Encode(byte[] rawZones)
        {
            if (rawZones == null || rawZones.Length == 0) return Array.Empty<byte>();

            using (var ms = new MemoryStream(rawZones.Length / 2))
            using (var w = new BinaryWriter(ms))
            {
                int i = 0;
                while (i < rawZones.Length)
                {
                    byte current = rawZones[i];
                    byte count = 1;
                    while (i + count < rawZones.Length && rawZones[i + count] == current && count < 255)
                    {
                        count++;
                    }

                    w.Write(count);
                    w.Write(current);
                    i += count;
                }
                return ms.ToArray();
            }
        }

        public static byte[] Decode(byte[] compressed, int expectedLength)
        {
            if (compressed == null || compressed.Length == 0) return Array.Empty<byte>();

            var output = new byte[expectedLength];
            int outIdx = 0;

            using (var ms = new MemoryStream(compressed, writable: false))
            using (var r = new BinaryReader(ms))
            {
                while (ms.Position < ms.Length && outIdx < expectedLength)
                {
                    byte count = r.ReadByte();
                    byte val = r.ReadByte();
                    for (int j = 0; j < count && outIdx < expectedLength; j++)
                    {
                        output[outIdx++] = val;
                    }
                }
            }
            return output;
        }
    }
}
