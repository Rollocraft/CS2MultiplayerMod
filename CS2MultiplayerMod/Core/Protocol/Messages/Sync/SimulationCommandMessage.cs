namespace CS2MultiplayerMod.Core.Protocol.Messages
{
    /// <summary>
    /// Envelope for a <see cref="Sync.ISimulationCommand"/>: command id, target <see cref="Tick"/> and
    /// the opaque body, which only the game layer encodes and decodes.
    /// </summary>
    public sealed class SimulationCommandMessage : INetMessage
    {
        public int OriginPlayerId;
        public long Tick;
        public ushort CommandId;
        public byte[] Body;

        public SimulationCommandMessage() { }

        public SimulationCommandMessage(int originPlayerId, long tick, ushort commandId, byte[] body)
        {
            OriginPlayerId = originPlayerId;
            Tick = tick;
            CommandId = commandId;
            Body = body ?? System.Array.Empty<byte>();
        }

        public MessageType Type => MessageType.SimulationCommand;

        public void Write(NetworkWriter writer)
        {
            writer.WriteInt(OriginPlayerId);
            writer.WriteLong(Tick);
            writer.WriteShort((short)CommandId);
            writer.WriteLengthPrefixedBytes(Body);
        }

        public void Read(NetworkReader reader)
        {
            OriginPlayerId = reader.ReadInt();
            Tick = reader.ReadLong();
            CommandId = (ushort)reader.ReadShort();
            Body = reader.ReadRemainingLengthPrefixedBytes("Simulation command");
        }
    }
}
