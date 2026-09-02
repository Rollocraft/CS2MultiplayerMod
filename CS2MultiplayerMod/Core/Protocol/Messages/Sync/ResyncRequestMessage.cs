namespace CS2MultiplayerMod.Core.Protocol.Messages
{
    /// <summary>
    /// Client -> host: "stream current world now." Sent when the player runs <c>/sync</c>
    /// due to suspected city drift, and when the client's own sync pipeline settles on a
    /// world reload. Host saves and streams live world - periodic resync but on demand.
    ///
    /// <see cref="Reason"/> carries WHY, so the host's log distinguishes a player pressing
    /// the button from a client that could not apply an edit. It is untrusted display text:
    /// the reader sanitizes it, and nothing branches on its content.
    /// </summary>
    public sealed class ResyncRequestMessage : INetMessage
    {
        public int OriginPlayerId;

        /// <summary>Short human-readable cause, for logs only. Never null after a read.</summary>
        public string Reason;

        public ResyncRequestMessage() { }

        public ResyncRequestMessage(int originPlayerId, string reason = null)
        {
            OriginPlayerId = originPlayerId;
            Reason = reason;
        }

        public MessageType Type => MessageType.ResyncRequest;

        public void Write(NetworkWriter writer)
        {
            writer.WriteInt(OriginPlayerId);
            writer.WriteString(WireGuard.SanitizeText(Reason, WireGuard.MaxResyncReasonLength));
        }

        public void Read(NetworkReader reader)
        {
            OriginPlayerId = reader.ReadInt();
            Reason = WireGuard.SanitizeText(reader.ReadString(), WireGuard.MaxResyncReasonLength);
        }
    }
}
