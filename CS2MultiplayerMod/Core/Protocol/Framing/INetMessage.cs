namespace CS2MultiplayerMod.Core.Protocol
{
    /// <summary>
    /// A serializable application message. Each implementation owns its own wire
    /// layout via <see cref="Write"/>/<see cref="Read"/>; the leading type byte is
    /// handled by <see cref="MessageCodec"/>, not here.
    /// </summary>
    public interface INetMessage
    {
        MessageType Type { get; }

        void Write(NetworkWriter writer);

        void Read(NetworkReader reader);
    }
}
