namespace CS2MultiplayerMod.Core.Protocol.Messages
{
    /// <summary>
    /// Transport envelope for a simulation command (see
    /// <see cref="Sync.ISimulationCommand"/>). The core never interprets the body; it
    /// carries the command id, the simulation tick the command is scheduled for, and
    /// the opaque serialized command bytes. The game layer encodes/decodes the body.
    ///
    /// Tagging each command with a target <see cref="Tick"/> is what enables
    /// deterministic, lockstep-style application: every peer applies a given command
    /// on the same simulation frame.
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
            writer.WriteInt(Body != null ? Body.Length : 0);
            if (Body != null && Body.Length > 0)
                writer.WriteBytes(Body, 0, Body.Length);
        }

        public void Read(NetworkReader reader)
        {
            OriginPlayerId = reader.ReadInt();
            Tick = reader.ReadLong();
            CommandId = (ushort)reader.ReadShort();
            int length = reader.ReadInt();
            Body = length > 0 ? reader.ReadBytes(length) : System.Array.Empty<byte>();
        }
    }
}
