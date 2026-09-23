using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Sync;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>A development-tree node purchase, by prefab name; the host deducts the shared points.</summary>
    public sealed class DevTreePurchaseCommand : ISimulationCommand
    {
        public const ushort Id = 18;

        public string NodePrefabName;

        public ushort CommandId => Id;

        public void Write(NetworkWriter writer) => writer.WriteString(NodePrefabName);

        public void Read(NetworkReader reader) => NodePrefabName = WireGuard.ReadName(reader);

        public byte[] Encode()
        {
            var writer = new NetworkWriter(64);
            Write(writer);
            return writer.ToArray();
        }

        public static DevTreePurchaseCommand Decode(byte[] body)
        {
            var command = new DevTreePurchaseCommand();
            command.Read(new NetworkReader(body));
            return command;
        }
    }
}
