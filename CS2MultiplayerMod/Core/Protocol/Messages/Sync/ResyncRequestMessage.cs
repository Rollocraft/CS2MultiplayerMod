namespace CS2MultiplayerMod.Core.Protocol.Messages
{
    /// <summary>
    /// Client -> host: stream the current world now, from <c>/sync</c> or a settled pipeline fault.
    /// <see cref="Reason"/> is untrusted log text; nothing branches on it.
    /// </summary>
    public sealed class ResyncRequestMessage : INetMessage
    {
        public int OriginPlayerId;
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
            writer.WriteInt(OriginPlayerId);
            writer.WriteBool(IsAutomatic);
            writer.WriteString(WireGuard.SanitizeText(Reason, WireGuard.MaxResyncReasonLength));
        }

        public void Read(NetworkReader reader)
        {
            OriginPlayerId = reader.ReadInt();
            IsAutomatic = reader.ReadBool();
            Reason = WireGuard.SanitizeText(reader.ReadString(), WireGuard.MaxResyncReasonLength);
        }
    }
}
