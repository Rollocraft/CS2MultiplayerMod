using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Sync;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>
    /// "A player purchased this development-tree node." The node travels by prefab name
    /// (its entity differs per machine); the receiver unlocks the same node and the host
    /// deducts the node's cost from the shared points - see <see cref="DevTreeSyncSystem"/>.
    /// </summary>
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
