using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Sync;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>Same-prefab objects removed by one brush frame, as one inbox item.</summary>
    public sealed class ObjectDeleteBatchCommand : ISimulationCommand
    {
        public const ushort Id = 32;
        public const int MaxDeletes = 4096;
        public const int MaxEncodedBytes = 64 * 1024;

        public struct Position
        {
            public float X, Y, Z;
        }

        public string PrefabName;
        public Position[] Positions;
        public ushort CommandId => Id;

        public void Write(NetworkWriter writer)
        {
            if (string.IsNullOrEmpty(PrefabName))
                throw new ProtocolException("Object-delete batch has no prefab name.");
            if (Positions == null || Positions.Length == 0 || Positions.Length > MaxDeletes)
                throw new ProtocolException("Invalid object-delete batch count.");

            writer.WriteString(PrefabName);
            writer.WriteShort((short)Positions.Length);
            for (int i = 0; i < Positions.Length; i++)
            {
                writer.WriteFloat(Positions[i].X);
                writer.WriteFloat(Positions[i].Y);
                writer.WriteFloat(Positions[i].Z);
            }
        }

        public void Read(NetworkReader reader)
        {
            PrefabName = WireGuard.ReadName(reader);
            int count = WireGuard.ReadCount(reader, 12, MaxDeletes);
            if (count == 0) throw new ProtocolException("Empty object-delete batch.");
            Positions = new Position[count];
            for (int i = 0; i < count; i++)
            {
                Positions[i] = new Position
                {
                    X = WireGuard.ReadCoordinate(reader),
                    Y = WireGuard.ReadCoordinate(reader),
                    Z = WireGuard.ReadCoordinate(reader),
                };
            }
            if (reader.Remaining != 0)
                throw new ProtocolException("Trailing bytes in object-delete batch: " +
                                            reader.Remaining + ".");
        }

        public byte[] Encode()
        {
            var writer = new NetworkWriter(64);
            Write(writer);
            if (writer.Length > MaxEncodedBytes)
                throw new ProtocolException("Object-delete batch body " + writer.Length +
                                            " exceeds the " + MaxEncodedBytes + "-byte cap.");
            return writer.ToArray();
        }

        public static ObjectDeleteBatchCommand Decode(byte[] body)
        {
            if (body == null) throw new ProtocolException("Null object-delete batch body.");
            if (body.Length > MaxEncodedBytes)
                throw new ProtocolException("Object-delete batch body " + body.Length +
                                            " exceeds the " + MaxEncodedBytes + "-byte cap.");
            var command = new ObjectDeleteBatchCommand();
            command.Read(new NetworkReader(body));
            return command;
        }
    }
}
