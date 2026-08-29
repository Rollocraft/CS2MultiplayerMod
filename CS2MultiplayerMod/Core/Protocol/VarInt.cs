using System;
using System.IO;

namespace CS2MultiplayerMod.Core.Protocol
{
    /// <summary>
    /// High-performance variable-length integer (VarInt) encoding and decoding routines.
    /// Encodes 32-bit and 64-bit integers into 1-10 bytes, shrinking protocol payloads.
    /// Supports Stream, BinaryReader/Writer, and zero-allocation in-place byte arrays.
    /// </summary>
    public static class VarInt
    {
        // ---------------- 32-Bit VarInt ----------------

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

        public static void WriteVarInt(byte[] buffer, ref int offset, uint value)
        {
            while (value >= 0x80)
            {
                buffer[offset++] = (byte)(value | 0x80);
                value >>= 7;
            }
            buffer[offset++] = (byte)value;
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

        public static uint ReadVarInt(byte[] buffer, ref int offset)
        {
            uint result = 0;
            int shift = 0;
            while (true)
            {
                byte b = buffer[offset++];
                result |= (uint)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) break;
                shift += 7;
                if (shift > 35) throw new FormatException("VarInt too long");
            }
            return result;
        }

        // ---------------- 32-Bit ZigZag ----------------

        public static void WriteZigZag(Stream stream, int value)
        {
            uint zigZag = (uint)((value << 1) ^ (value >> 31));
            WriteVarInt(stream, zigZag);
        }

        public static void WriteZigZag(BinaryWriter writer, int value)
        {
            uint zigZag = (uint)((value << 1) ^ (value >> 31));
            WriteVarInt(writer, zigZag);
        }

        public static void WriteZigZag(byte[] buffer, ref int offset, int value)
        {
            uint zigZag = (uint)((value << 1) ^ (value >> 31));
            WriteVarInt(buffer, ref offset, zigZag);
        }

        public static int ReadZigZag(Stream stream)
        {
            uint zigZag = ReadVarInt(stream);
            return (int)((zigZag >> 1) ^ (-(int)(zigZag & 1)));
        }

        public static int ReadZigZag(BinaryReader reader)
        {
            uint zigZag = ReadVarInt(reader);
            return (int)((zigZag >> 1) ^ (-(int)(zigZag & 1)));
        }

        public static int ReadZigZag(byte[] buffer, ref int offset)
        {
            uint zigZag = ReadVarInt(buffer, ref offset);
            return (int)((zigZag >> 1) ^ (-(int)(zigZag & 1)));
        }

        // ---------------- 64-Bit VarLong ----------------

        public static void WriteVarLong(Stream stream, ulong value)
        {
            while (value >= 0x80)
            {
                stream.WriteByte((byte)(value | 0x80));
                value >>= 7;
            }
            stream.WriteByte((byte)value);
        }

        public static void WriteVarLong(BinaryWriter writer, ulong value)
        {
            while (value >= 0x80)
            {
                writer.Write((byte)(value | 0x80));
                value >>= 7;
            }
            writer.Write((byte)value);
        }

        public static void WriteVarLong(byte[] buffer, ref int offset, ulong value)
        {
            while (value >= 0x80)
            {
                buffer[offset++] = (byte)(value | 0x80);
                value >>= 7;
            }
            buffer[offset++] = (byte)value;
        }

        public static ulong ReadVarLong(Stream stream)
        {
            ulong result = 0;
            int shift = 0;
            while (true)
            {
                int b = stream.ReadByte();
                if (b == -1) throw new EndOfStreamException();
                result |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) break;
                shift += 7;
                if (shift > 70) throw new FormatException("VarLong too long");
            }
            return result;
        }

        public static ulong ReadVarLong(BinaryReader reader)
        {
            ulong result = 0;
            int shift = 0;
            while (true)
            {
                byte b = reader.ReadByte();
                result |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) break;
                shift += 7;
                if (shift > 70) throw new FormatException("VarLong too long");
            }
            return result;
        }

        public static ulong ReadVarLong(byte[] buffer, ref int offset)
        {
            ulong result = 0;
            int shift = 0;
            while (true)
            {
                byte b = buffer[offset++];
                result |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) break;
                shift += 7;
                if (shift > 70) throw new FormatException("VarLong too long");
            }
            return result;
        }

        // ---------------- 64-Bit ZigZag64 ----------------

        public static void WriteZigZag64(Stream stream, long value)
        {
            ulong zigZag = (ulong)((value << 1) ^ (value >> 63));
            WriteVarLong(stream, zigZag);
        }

        public static void WriteZigZag64(BinaryWriter writer, long value)
        {
            ulong zigZag = (ulong)((value << 1) ^ (value >> 63));
            WriteVarLong(writer, zigZag);
        }

        public static void WriteZigZag64(byte[] buffer, ref int offset, long value)
        {
            ulong zigZag = (ulong)((value << 1) ^ (value >> 63));
            WriteVarLong(buffer, ref offset, zigZag);
        }

        public static long ReadZigZag64(Stream stream)
        {
            ulong zigZag = ReadVarLong(stream);
            return (long)((zigZag >> 1) ^ (ulong)(-(long)(zigZag & 1)));
        }

        public static long ReadZigZag64(BinaryReader reader)
        {
            ulong zigZag = ReadVarLong(reader);
            return (long)((zigZag >> 1) ^ (ulong)(-(long)(zigZag & 1)));
        }

        public static long ReadZigZag64(byte[] buffer, ref int offset)
        {
            ulong zigZag = ReadVarLong(buffer, ref offset);
            return (long)((zigZag >> 1) ^ (ulong)(-(long)(zigZag & 1)));
        }
    }
}
