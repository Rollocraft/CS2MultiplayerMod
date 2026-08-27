using System;
using System.Collections.Generic;
using CS2MultiplayerMod.Core.Protocol.Messages;

namespace CS2MultiplayerMod.Core.Protocol
{
    /// <summary>
    /// Translates between <see cref="INetMessage"/> instances and the byte payloads the
    /// transport delivers. The wire layout is: [1 byte MessageType][message body].
    ///
    /// Decoding is table-driven: each known <see cref="MessageType"/> maps to a factory
    /// producing a fresh, empty instance to read into, plus a per-type payload size cap.
    /// The cap is the first line of defense: a chat line claiming to be 10 MB is
    /// rejected before a single body byte is parsed, so the blanket
    /// <see cref="ProtocolConstants.MaxPayloadBytes"/> only matters for blob chunks.
    /// </summary>
    public sealed class MessageCodec
    {
        private struct Entry
        {
            public Func<INetMessage> Factory;
            public int MaxPayloadBytes;
        }

        private readonly Dictionary<MessageType, Entry> _entries = new Dictionary<MessageType, Entry>();

        /// <summary>A codec preloaded with every built-in message type and its size cap.</summary>
        public static MessageCodec CreateDefault()
        {
            var codec = new MessageCodec();
            // Sized for the DLC list (≤64 entries of ≤64 chars) on top of the fixed fields.
            codec.Register(MessageType.HandshakeRequest, () => new HandshakeRequest(), 32 * 1024);
            codec.Register(MessageType.HandshakeResponse, () => new HandshakeResponse(), 1024);
            codec.Register(MessageType.HandshakeChallenge, () => new HandshakeChallenge(), 256);
            codec.Register(MessageType.HandshakePending, () => new HandshakePendingMessage(), 64);
            codec.Register(MessageType.Heartbeat, () => new Heartbeat(), 64);
            codec.Register(MessageType.Chat, () => new ChatMessage(), 4 * 1024);
            codec.Register(MessageType.SimulationCommand, () => new SimulationCommandMessage(),
                ProtocolConstants.MaxSimulationCommandPayloadBytes);
            codec.Register(MessageType.StateSnapshot, () => new StateSnapshotMessage(), 256 * 1024);
            codec.Register(MessageType.PlayerState, () => new PlayerStateMessage(), 64);
            codec.Register(MessageType.BlobChunk, () => new BlobChunkMessage(),
                ProtocolConstants.BlobChunkBytes + 1024);
            codec.Register(MessageType.StateEdit, () => new StateEditMessage(), 128 * 1024);
            codec.Register(MessageType.ResyncRequest, () => new ResyncRequestMessage(), 64);
            codec.Register(MessageType.WorldSyncControl, () => new WorldSyncControlMessage(), 64);
            codec.Register(MessageType.DisconnectNotice, () => new DisconnectNoticeMessage(), 1024);
            return codec;
        }

        public void Register(MessageType type, Func<INetMessage> factory, int maxPayloadBytes)
        {
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            if (maxPayloadBytes <= 0 || maxPayloadBytes > ProtocolConstants.MaxPayloadBytes)
                throw new ArgumentOutOfRangeException(nameof(maxPayloadBytes));
            _entries[type] = new Entry { Factory = factory, MaxPayloadBytes = maxPayloadBytes };
        }

        public byte[] Encode(INetMessage message)
        {
            if (message == null) throw new ArgumentNullException(nameof(message));

            var writer = new NetworkWriter();
            writer.WriteByte((byte)message.Type);
            message.Write(writer);
            return writer.ToArray();
        }

        public INetMessage Decode(byte[] payload)
        {
            if (payload == null || payload.Length < 1)
                throw new ProtocolException("Empty payload cannot be decoded.");

            var reader = new NetworkReader(payload);
            var type = (MessageType)reader.ReadByte();

            Entry entry;
            if (!_entries.TryGetValue(type, out entry))
                throw new ProtocolException("Unknown message type: " + (byte)type + ".");

            if (payload.Length > entry.MaxPayloadBytes)
                throw new ProtocolException(type + " payload of " + payload.Length +
                                            " bytes exceeds its " + entry.MaxPayloadBytes + "-byte cap.");

            INetMessage message = entry.Factory();
            message.Read(reader);
            if (reader.Remaining != 0)
                throw new ProtocolException(type + " payload contains " + reader.Remaining +
                                            " trailing byte(s).");
            return message;
        }
    }
}
