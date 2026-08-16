namespace CS2MultiplayerMod.Core.Protocol.Messages
{
    /// <summary>
    /// Host's reply to a <see cref="HandshakeRequest"/>. On acceptance it carries the
    /// assigned player id; on rejection it carries a human-readable reason.
    /// </summary>
    public sealed class HandshakeResponse : INetMessage
    {
        public bool Accepted;
        public int AssignedPlayerId;
        public string Reason;

        public HandshakeResponse() { }

        public static HandshakeResponse Accept(int playerId) =>
            new HandshakeResponse { Accepted = true, AssignedPlayerId = playerId, Reason = null };

        public static HandshakeResponse Reject(string reason) =>
            new HandshakeResponse { Accepted = false, AssignedPlayerId = 0, Reason = reason };

        public MessageType Type => MessageType.HandshakeResponse;

        public void Write(NetworkWriter writer)
        {
            writer.WriteBool(Accepted);
            writer.WriteInt(AssignedPlayerId);
            writer.WriteString(Reason);
        }

        public void Read(NetworkReader reader)
        {
            Accepted = reader.ReadBool();
            AssignedPlayerId = reader.ReadInt();
            Reason = reader.ReadString();
        }
    }
}
