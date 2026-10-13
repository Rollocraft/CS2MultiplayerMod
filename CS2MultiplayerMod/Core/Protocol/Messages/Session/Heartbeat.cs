namespace CS2MultiplayerMod.Core.Protocol.Messages
{
    /// <summary>
    /// Keep-alive and latency probe. The reply echoes <see cref="SentAtMs"/> as <see cref="EchoOfMs"/>;
    /// round-trip is measured on the sender's clock only. Echoes are not echoed.
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
