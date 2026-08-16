namespace CS2MultiplayerMod.Core.Protocol.Messages
{
    /// <summary>
    /// Periodic keep-alive and latency probe. A ping carries sender's monotonic clock
    /// in <see cref="SentAtMs"/>; receiver answers with heartbeat whose <see cref="EchoOfMs"/>
    /// returns that value. Original sender measures round-trip as now - echo, both
    /// subtractions on sender's own clock so clocks need not agree. Echo never echoed back.
    /// </summary>
    public sealed class Heartbeat : INetMessage
    {
        /// <summary>Sender's monotonic clock (ms) when this heartbeat was sent.</summary>
        public long SentAtMs;

        /// <summary>0 for a ping; for an echo, the ping's <see cref="SentAtMs"/> being returned.</summary>
        public long EchoOfMs;

        public Heartbeat() { }

        public Heartbeat(long sentAtMs, long echoOfMs = 0)
        {
            SentAtMs = sentAtMs;
            EchoOfMs = echoOfMs;
        }

        public MessageType Type => MessageType.Heartbeat;

        public void Write(NetworkWriter writer)
        {
            writer.WriteLong(SentAtMs);
            writer.WriteLong(EchoOfMs);
        }

        public void Read(NetworkReader reader)
        {
            SentAtMs = reader.ReadLong();
            EchoOfMs = reader.ReadLong();
        }
    }
}
