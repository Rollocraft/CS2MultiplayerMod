namespace CS2MultiplayerMod.Core.Protocol
{
    /// <summary>A message owning its wire layout; the type byte is <see cref="MessageCodec"/>'s.</summary>
    public interface INetMessage
    {
        MessageType Type { get; }

        void Write(NetworkWriter writer);

        void Read(NetworkReader reader);
    }
}
