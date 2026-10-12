using System;
using System.Text;

namespace CS2MultiplayerMod.Core.Protocol
{
    /// <summary>Reads what <see cref="NetworkWriter"/> writes; a truncated payload throws, never over-reads.</summary>
    public sealed class NetworkReader
    {
        private readonly byte[] _buffer;
        private readonly int _end;
        private int _position;

        public NetworkReader(byte[] buffer) : this(buffer, 0, buffer != null ? buffer.Length : 0) { }

        public NetworkReader(byte[] buffer, int offset, int count)
        {
            _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || offset > buffer.Length || count < 0 || count > buffer.Length - offset)
                throw new ProtocolException("Invalid reader buffer range.");
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
            // A negative count comes off the wire: a protocol error.
            if (count < 0) throw new ProtocolException("Negative byte-array length: " + count + ".");
            Require(count);
            byte[] result = new byte[count];
            Buffer.BlockCopy(_buffer, _position, result, 0, count);
            _position += count;
            return result;
        }

        /// <summary>Read a length-prefixed payload that must fill the rest of this message.</summary>
        public byte[] ReadRemainingLengthPrefixedBytes(string messageName)
        {
            int length = ReadInt();
            if (length != Remaining)
                throw new ProtocolException(messageName + " body length does not match its envelope.");
            return length == 0 ? Array.Empty<byte>() : ReadBytes(length);
        }

        private void Require(int count)
        {
            // Overflow-safe: "_position + count" wraps for a forged length near int.MaxValue.
            if (count < 0 || count > _end - _position)
                throw new ProtocolException("Unexpected end of payload: needed " + count + " byte(s), have " + Remaining + ".");
        }
    }
}
