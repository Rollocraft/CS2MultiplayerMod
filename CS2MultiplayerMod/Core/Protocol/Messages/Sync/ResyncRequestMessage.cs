namespace CS2MultiplayerMod.Core.Protocol.Messages
{
    /// <summary>
    /// Client -> host: stream the current world now, from <c>/sync</c> or a settled pipeline fault.
    /// <see cref="Reason"/> is untrusted log text; nothing branches on it.
    /// </summary>
    public sealed class ResyncRequestMessage : INetMessage
    {
        public int OriginPlayerId;
        public long StateRequestId;
        public StateDigest[] Digests = System.Array.Empty<StateDigest>();
        /// <summary>Notice attribution only; never changes permission or recovery policy.</summary>
        public bool IsAutomatic;

        /// <summary>Short human-readable cause, for logs only. Never null after a read.</summary>
        public string Reason;

        public ResyncRequestMessage() { }

        public ResyncRequestMessage(int originPlayerId, string reason = null, bool isAutomatic = false)
        {
            OriginPlayerId = originPlayerId;
            Reason = reason;
            IsAutomatic = isAutomatic;
        }

        public MessageType Type => MessageType.ResyncRequest;

        public void Write(NetworkWriter writer)
        {
            ValidateDigests();
            writer.WriteInt(OriginPlayerId);
            writer.WriteBool(IsAutomatic);
            writer.WriteString(WireGuard.SanitizeText(Reason, WireGuard.MaxResyncReasonLength));
            writer.WriteLong(StateRequestId);
            writer.WriteByte((byte)Digests.Length);
            foreach (var digest in Digests)
            {
                writer.WriteByte(digest.ChannelId);
                writer.WriteBytes(digest.Hash, 0, 32);
            }
        }

        private void ValidateDigests()
        {
            if (StateRequestId < 0 || Digests == null || Digests.Length > StateRepairMessage.MaxChannels ||
                (StateRequestId == 0) != (Digests.Length == 0))
                throw new ProtocolException("Invalid state repair request.");
            var seen = new System.Collections.Generic.HashSet<byte>();
            foreach (var digest in Digests)
                if (digest == null || !seen.Add(digest.ChannelId) || digest.Hash == null || digest.Hash.Length != 32)
                    throw new ProtocolException("Invalid state repair digest.");
        }

        public void Read(NetworkReader reader)
        {
            OriginPlayerId = reader.ReadInt();
            IsAutomatic = reader.ReadBool();
            Reason = WireGuard.SanitizeText(reader.ReadString(), WireGuard.MaxResyncReasonLength);
            StateRequestId = reader.ReadLong();
            int count = reader.ReadByte();
            if (count > StateRepairMessage.MaxChannels) throw new ProtocolException("Too many repair digests.");
            Digests = new StateDigest[count];
            for (int i = 0; i < count; i++)
                Digests[i] = new StateDigest { ChannelId = reader.ReadByte(), Hash = reader.ReadBytes(32) };
            ValidateDigests();
        }
    }
}
