using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Sync;
using CS2MultiplayerMod.Core.Sync.ModSync;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>
    /// The session's replicated third-party types in order, sent when gameplay sync opens and on every
    /// join. Clients report entries they cannot bind.
    /// </summary>
    public sealed class ModTypeTableCommand : ISimulationCommand
    {
        public const ushort Id = 29;

        public ModTypeTable Table;

        public ushort CommandId => Id;

        public void Write(NetworkWriter writer) => Table.Write(writer);

        public void Read(NetworkReader reader) => Table = ModTypeTable.Read(reader);

        public byte[] Encode()
        {
            var writer = new NetworkWriter(2048);
            Write(writer);
            return writer.ToArray();
        }

        public static ModTypeTableCommand Decode(byte[] body)
        {
            var command = new ModTypeTableCommand();
            command.Read(new NetworkReader(body));
            return command;
        }
    }
}
