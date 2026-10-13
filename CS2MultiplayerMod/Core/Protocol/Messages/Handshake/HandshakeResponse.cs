namespace CS2MultiplayerMod.Core.Protocol.Messages
{
    /// <summary>Accepted: player id and simulation-sync choice. Rejected: a readable reason.</summary>
    public sealed class HandshakeResponse : INetMessage
    {
        public bool Accepted;
        public int AssignedPlayerId;
        public string Reason;

        /// <summary>
        /// The session's simulation-sync choice; a client follows it over its own setting or would wait
        /// forever for host decisions.
        /// </summary>
        public bool SimulationSync = true;

        public HandshakeResponse() { }

        public static HandshakeResponse Accept(int playerId, bool simulationSync = true) =>
            new HandshakeResponse
            {
                Accepted = true,
                AssignedPlayerId = playerId,
                Reason = null,
                SimulationSync = simulationSync,
            };

        public static HandshakeResponse Reject(string reason) =>
            new HandshakeResponse { Accepted = false, AssignedPlayerId = 0, Reason = reason };

        public MessageType Type => MessageType.HandshakeResponse;

        public void Write(NetworkWriter writer)
        {
            writer.WriteBool(Accepted);
            writer.WriteInt(AssignedPlayerId);
            writer.WriteString(Reason);
            writer.WriteBool(SimulationSync);
        }

        public void Read(NetworkReader reader)
        {
            Accepted = reader.ReadBool();
            AssignedPlayerId = reader.ReadInt();
            Reason = reader.ReadString();
            SimulationSync = reader.ReadBool();
        }
    }
}
