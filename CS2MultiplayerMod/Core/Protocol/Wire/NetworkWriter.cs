using System;
using System.Runtime.InteropServices;
using System.Text;

namespace CS2MultiplayerMod.Core.Protocol
{
    /// <summary>
    /// Little-endian binary writer over a growable buffer; strings are length-prefixed UTF-8.
    /// Dependency-free.
    /// </summary>
    public sealed class NetworkWriter
    {
        private byte[] _buffer;
        private int _length;

        // Preserve IEEE-754 bits without an array per coordinate on .NET 4.8.
        [StructLayout(LayoutKind.Explicit)]
        private struct FloatBits
        {
            [FieldOffset(0)] public float Value;
            [FieldOffset(0)] public int Bits;
        }

        public NetworkWriter(int initialCapacity = 256)
        {
            if (initialCapacity < 4) initialCapacity = 4;
            _buffer = new byte[initialCapacity];
            _length = 0;
        }

        public int Length => _length;

        public void WriteByte(byte value)
        {
            EnsureCapacity(1);
            _buffer[_length++] = value;
        }

        public void WriteBool(bool value) => WriteByte(value ? (byte)1 : (byte)0);

        public void WriteShort(short value)
        {
            EnsureCapacity(2);
            _buffer[_length++] = (byte)(value & 0xFF);
            _buffer[_length++] = (byte)((value >> 8) & 0xFF);
        }

        public void WriteInt(int value)
        {
            EnsureCapacity(4);
            _buffer[_length++] = (byte)(value & 0xFF);
            _buffer[_length++] = (byte)((value >> 8) & 0xFF);
            _buffer[_length++] = (byte)((value >> 16) & 0xFF);
            _buffer[_length++] = (byte)((value >> 24) & 0xFF);
        }

        public void WriteLong(long value)
        {
            EnsureCapacity(8);
            for (int i = 0; i < 8; i++)
            {
                _buffer[_length++] = (byte)(value & 0xFF);
                value >>= 8;
            }
        }

        public void WriteFloat(float value) => WriteInt(new FloatBits { Value = value }.Bits);

        public void WriteString(string value)
        {
            if (value == null)
            {
                WriteInt(-1);
                return;
            }

            int byteCount = Encoding.UTF8.GetByteCount(value);
            WriteInt(byteCount);
            EnsureCapacity(byteCount);
            _length += Encoding.UTF8.GetBytes(value, 0, value.Length, _buffer, _length);
        }

        public void WriteBytes(byte[] source, int offset, int count)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (offset < 0 || offset > source.Length || count < 0 || count > source.Length - offset)
                throw new ArgumentOutOfRangeException(nameof(count));
            if (count == 0) return;
            EnsureCapacity(count);
            Buffer.BlockCopy(source, offset, _buffer, _length, count);
            _length += count;
        }

        /// <summary>Write an opaque payload with its byte count, treating null as empty.</summary>
        public void WriteLengthPrefixedBytes(byte[] value)
        {
            int count = value != null ? value.Length : 0;
            WriteInt(count);
            if (count > 0) WriteBytes(value, 0, count);
        }

        /// <summary>Copy the written bytes into a fresh array sized exactly to the content.</summary>
        public byte[] ToArray()
        {
            byte[] result = new byte[_length];
            Buffer.BlockCopy(_buffer, 0, result, 0, _length);
            return result;
        }

        private void EnsureCapacity(int additional)
        {
            int required = _length + additional;
            if (required <= _buffer.Length) return;

            int newCapacity = _buffer.Length * 2;
            if (newCapacity < required) newCapacity = required;
            Array.Resize(ref _buffer, newCapacity);
        }
    }
}
