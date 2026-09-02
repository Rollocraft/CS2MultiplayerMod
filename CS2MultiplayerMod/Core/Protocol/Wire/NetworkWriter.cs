using System;
using System.Text;

namespace CS2MultiplayerMod.Core.Protocol
{
    /// <summary>
    /// Minimal, allocation-light binary writer over a growable byte buffer.
    ///
    /// All multi-byte values are written little-endian (every supported platform is
    /// little-endian, so this stays consistent across host and clients). Strings are
    /// length-prefixed UTF-8. Deliberately dependency-free so the protocol layer can
    /// be reused and tested outside the game.
    /// </summary>
    public sealed class NetworkWriter
    {
        private byte[] _buffer;
        private int _length;

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

        public void WriteFloat(float value)
        {
            // BitConverter is little-endian on every supported (x86/ARM) target, matching
            // the manual little-endian integer writes above.
            byte[] bytes = BitConverter.GetBytes(value);
            WriteBytes(bytes, 0, 4);
        }

        public void WriteString(string value)
        {
            if (value == null)
            {
                WriteInt(-1);
                return;
            }

            byte[] bytes = Encoding.UTF8.GetBytes(value);
            WriteInt(bytes.Length);
            WriteBytes(bytes, 0, bytes.Length);
        }

        public void WriteBytes(byte[] source, int offset, int count)
        {
            if (count <= 0) return;
            EnsureCapacity(count);
            Buffer.BlockCopy(source, offset, _buffer, _length, count);
            _length += count;
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
