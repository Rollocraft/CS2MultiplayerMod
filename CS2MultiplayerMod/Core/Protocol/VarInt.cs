using System;
using System.IO;

namespace CS2MultiplayerMod.Core.Protocol
{
    /// <summary>
    /// High-performance variable-length integer (VarInt) encoding and decoding routines.
    /// Encodes 32-bit and 64-bit integers into 1-5 bytes, shrinking protocol payloads.
    /// </summary>
    public static class VarInt
    {
        public static void WriteVarInt(Stream stream, uint value)
        {
            while (value >= 0x80)
            {
                stream.WriteByte((byte)(value | 0x80));
                value >>= 7;
            }
            stream.WriteByte((byte)value);
        }

        public static void WriteVarInt(BinaryWriter writer, uint value)
        {
            while (value >= 0x80)
            {
                writer.Write((byte)(value | 0x80));
                value >>= 7;
            }
            writer.Write((byte)value);
        }

        public static uint ReadVarInt(Stream stream)
        {
            uint result = 0;
            int shift = 0;
            while (true)
            {
                int b = stream.ReadByte();
                if (b == -1) throw new EndOfStreamException();
                result |= (uint)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) break;
                shift += 7;
                if (shift > 35) throw new FormatException("VarInt too long");
            }
            return result;
        }

        public static uint ReadVarInt(BinaryReader reader)
        {
            uint result = 0;
            int shift = 0;
            while (true)
            {
                byte b = reader.ReadByte();
                result |= (uint)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) break;
                shift += 7;
                if (shift > 35) throw new FormatException("VarInt too long");
            }
            return result;
        }

        public static void WriteZigZag(BinaryWriter writer, int value)
        {
            uint zigZag = (uint)((value << 1) ^ (value >> 31));
            WriteVarInt(writer, zigZag);
        }

        public static int ReadZigZag(BinaryReader reader)
        {
            uint zigZag = ReadVarInt(reader);
            return (int)((zigZag >> 1) ^ (-(int)(zigZag & 1)));
        }
    }
}
