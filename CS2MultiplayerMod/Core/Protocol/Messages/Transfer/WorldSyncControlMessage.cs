namespace CS2MultiplayerMod.Core.Protocol.Messages
{
    /// <summary>Stages in one host-authoritative, epoch-scoped world replacement.</summary>
    public enum WorldSyncStage : byte
    {
        Begin = 1,
        Quiesced = 2,
        Loaded = 3,
        Failed = 4,
        Resume = 5,
        Abort = 6,

        /// <summary>Begin for a peer that already holds the world: it quiesces and resumes, nothing is streamed.</summary>
        BeginBarrierOnly = 7,
    }

    /// <summary>
    /// World snapshot control: Begin/Resume/Abort host to client, Quiesced/Loaded/Failed back. The epoch
    /// discards stale controls and chunks.
    /// </summary>
    public sealed class WorldSyncControlMessage : INetMessage
    {
        public long Epoch;
        public WorldSyncStage Stage;
        public float ResumeSpeed;

        public WorldSyncControlMessage() { }

        public WorldSyncControlMessage(long epoch, WorldSyncStage stage, float resumeSpeed = 0f)
        {
            Epoch = epoch;
            Stage = stage;
            ResumeSpeed = resumeSpeed;
        }

        public MessageType Type => MessageType.WorldSyncControl;

        public void Write(NetworkWriter writer)
        {
            writer.WriteLong(Epoch);
            writer.WriteByte((byte)Stage);
            writer.WriteFloat(ResumeSpeed);
        }

        public void Read(NetworkReader reader)
        {
            Epoch = reader.ReadLong();
            Stage = (WorldSyncStage)reader.ReadByte();
            ResumeSpeed = reader.ReadFloat();
        }
    }
}
