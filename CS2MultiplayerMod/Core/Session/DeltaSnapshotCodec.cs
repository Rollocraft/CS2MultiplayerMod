using System;
using System.IO;

namespace CS2MultiplayerMod.Core.Session
{
    /// <summary>
    /// Binary XOR and run-length diffing engine for world snapshots.
    /// Emits compact delta patches when resyncing (/sync) against an existing baseline save.
    /// </summary>
    public static class DeltaSnapshotCodec
    {
        private static readonly byte[] DeltaMagic = new byte[] { 0x44, 0x45, 0x4C, 0x54 }; // "DELT"

        public static byte[] ComputeDelta(byte[] baseline, byte[] current)
        {
            if (baseline == null || current == null || baseline.Length == 0) return current;

            using (var ms = new MemoryStream(current.Length / 4))
            using (var w = new BinaryWriter(ms))
            {
                w.Write(DeltaMagic);
                w.Write(current.Length);

                int minLen = Math.Min(baseline.Length, current.Length);
                int i = 0;
                while (i < minLen)
                {
                    if (baseline[i] == current[i])
                    {
                        // Match run
                        int matchLen = 0;
                        while (i < minLen && baseline[i] == current[i] && matchLen < ushort.MaxValue)
                        {
                            matchLen++;
                            i++;
                        }
                        w.Write((byte)0); // 0 = Match
                        w.Write((ushort)matchLen);
                    }
                    else
                    {
                        // Diff run
                        int diffStart = i;
                        int diffLen = 0;
                        while (i < minLen && baseline[i] != current[i] && diffLen < ushort.MaxValue)
                        {
                            diffLen++;
                            i++;
                        }
                        w.Write((byte)1); // 1 = Diff
                        w.Write((ushort)diffLen);

                        // High-speed 64-bit unrolled XOR blitting
                        int k = 0;
                        while (k + 8 <= diffLen)
                        {
                            ulong bVal = BitConverter.ToUInt64(baseline, diffStart + k);
                            ulong cVal = BitConverter.ToUInt64(current, diffStart + k);
                            w.Write(bVal ^ cVal);
                            k += 8;
                        }
                        for (; k < diffLen; k++)
                        {
                            w.Write((byte)(baseline[diffStart + k] ^ current[diffStart + k]));
                        }
                    }
                }

                // Append any trailing new bytes beyond baseline length
                if (current.Length > minLen)
                {
                    int extra = current.Length - minLen;
                    w.Write((byte)2); // 2 = Append
                    w.Write((ushort)Math.Min(extra, (int)ushort.MaxValue));
                    w.Write(current, minLen, extra);
                }

                return ms.ToArray();
            }
        }

        public static byte[] ApplyDelta(byte[] baseline, byte[] delta)
        {
            if (delta == null || delta.Length < 8) return delta;
            if (delta[0] != DeltaMagic[0] || delta[1] != DeltaMagic[1] ||
                delta[2] != DeltaMagic[2] || delta[3] != DeltaMagic[3])
            {
                // Not a delta patch, return raw data
                return delta;
            }

            int targetLen = delta[4] | (delta[5] << 8) | (delta[6] << 16) | (delta[7] << 24);
            if (targetLen <= 0 || targetLen > 256 * 1024 * 1024) return delta;

            var result = new byte[targetLen];
            int outIdx = 0;
            int baseIdx = 0;

            using (var ms = new MemoryStream(delta, 8, delta.Length - 8, writable: false))
            using (var r = new BinaryReader(ms))
            {
                while (ms.Position < ms.Length && outIdx < targetLen)
                {
                    byte op = r.ReadByte();
                    ushort len = r.ReadUInt16();

                    if (op == 0) // Match from baseline
                    {
                        if (baseline != null && baseIdx + len <= baseline.Length)
                        {
                            Buffer.BlockCopy(baseline, baseIdx, result, outIdx, len);
                        }
                        baseIdx += len;
                        outIdx += len;
                    }
                    else if (op == 1) // XOR Diff against baseline
                    {
                        byte[] diffBytes = r.ReadBytes(len);
                        for (int k = 0; k < len && outIdx < targetLen; k++)
                        {
                            byte bVal = (baseline != null && baseIdx + k < baseline.Length) ? baseline[baseIdx + k] : (byte)0;
                            result[outIdx++] = (byte)(bVal ^ diffBytes[k]);
                        }
                        baseIdx += len;
                    }
                    else if (op == 2) // Append raw bytes
                    {
                        byte[] extra = r.ReadBytes(len);
                        Buffer.BlockCopy(extra, 0, result, outIdx, extra.Length);
                        outIdx += extra.Length;
                    }
                }
            }

            return result;
        }
    }
}
