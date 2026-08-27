using System;
using System.Text;

namespace CS2MultiplayerMod.Core.Protocol
{
    /// <summary>
    /// Counterpart to <see cref="NetworkWriter"/>. Reads little-endian primitives and
    /// length-prefixed UTF-8 strings from a byte buffer, validating bounds so a
    /// malformed or truncated payload throws rather than reading out of range.
    /// </summary>
    public sealed class NetworkReader
    {
        private readonly byte[] _buffer;
        private readonly int _end;
        private int _position;

        public NetworkReader(byte[] buffer) : this(buffer, 0, buffer != null ? buffer.Length : 0) { }

        public NetworkReader(byte[] buffer, int offset, int count)
        {
            _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
            _position = offset;
            _end = offset + count;
        }

        public int Remaining => _end - _position;

        public byte ReadByte()
        {
            Require(1);
            return _buffer[_position++];
        }

        public bool ReadBool() => ReadByte() != 0;

        public short ReadShort()
        {
            Require(2);
            int value = _buffer[_position] | (_buffer[_position + 1] << 8);
            _position += 2;
            return (short)value;
        }

        public int ReadInt()
        {
            Require(4);
            int value = _buffer[_position]
                        | (_buffer[_position + 1] << 8)
                        | (_buffer[_position + 2] << 16)
                        | (_buffer[_position + 3] << 24);
            _position += 4;
            return value;
        }

        public long ReadLong()
        {
            Require(8);
            long value = 0;
            for (int i = 0; i < 8; i++)
                value |= (long)_buffer[_position + i] << (8 * i);
            _position += 8;
            return value;
        }

        public float ReadFloat()
        {
            Require(4);
            float value = BitConverter.ToSingle(_buffer, _position);
            _position += 4;
            return value;
        }

        public string ReadString()
        {
            int length = ReadInt();
            if (length < 0) return null;
            if (length == 0) return string.Empty;

            Require(length);
            string value = Encoding.UTF8.GetString(_buffer, _position, length);
            _position += length;
            return value;
        }

        public byte[] ReadBytes(int count)
        {
            // A negative count comes from wire data (a length prefix), so it is a
            // protocol error, not a caller bug.
            if (count < 0) throw new ProtocolException("Negative byte-array length: " + count + ".");
            Require(count);
            byte[] result = new byte[count];
            Buffer.BlockCopy(_buffer, _position, result, 0, count);
            _position += count;
            return result;
        }

        private void Require(int count)
        {
            // Overflow-safe form: count comes off the wire, and "_position + count" would
            // wrap negative for a forged length near int.MaxValue and slip past the check.
            if (count < 0 || count > _end - _position)
                throw new ProtocolException("Unexpected end of payload: needed " + count + " byte(s), have " + Remaining + ".");
        }
    }
}
