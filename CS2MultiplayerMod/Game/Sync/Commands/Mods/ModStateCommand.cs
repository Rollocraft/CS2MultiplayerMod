using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Sync;
using CS2MultiplayerMod.Core.Sync.ModSync;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>
    /// One carrier's complete replicated mod closure: applies alone, applies twice harmlessly, and can
    /// be held until the carrier exists. Sent by whoever edited, relayed by the host.
    /// </summary>
    public sealed class ModStateCommand : ISimulationCommand
    {
        public const ushort Id = 30;

        /// <summary>Refused on the network thread, before the type table is consulted.</summary>
        public const int MaxBodyBytes = 512 * 1024;

        public ModStateSnapshot Snapshot;

        public ushort CommandId => Id;

        /// <summary>Required before encoding or decoding: types are named by table index.</summary>
        public IModTypeLookup Types;

        public void Write(NetworkWriter writer) => Snapshot.Write(writer, Types);

        public void Read(NetworkReader reader) => Snapshot = ModStateSnapshot.Read(reader, Types);

        public byte[] Encode()
        {
            var writer = new NetworkWriter(512);
            Write(writer);
            return writer.ToArray();
        }

        public static ModStateCommand Decode(byte[] body, IModTypeLookup types)
        {
            var command = new ModStateCommand { Types = types };
            command.Read(new NetworkReader(body));
            return command;
        }
    }
}
