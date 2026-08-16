using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Sync;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>
    /// One already-encoded network command inside an atomic net-tool operation. Only placement,
    /// deletion, and replacement commands are valid members; keeping their existing codecs as the
    /// inner format avoids maintaining a second representation of the same portable geometry.
    /// </summary>
    public sealed class NetToolOperationItem
    {
        public ushort CommandId;
        public byte[] Body;
    }

    /// <summary>
    /// The complete heterogeneous output of one net-tool Apply. A mixed road gesture may create
    /// courses while deleting and replacing existing edges. Those pieces must cross the wire in one
    /// envelope so the receiver can preflight them against one topology and commit them in one
    /// native transaction instead of applying three independently ordered command streams.
    /// </summary>
    public sealed class NetToolOperationCommand : ISimulationCommand
    {
        public const ushort Id = 28;
        public const int MaxItems = 1024;
        public const int MaxEncodedBytes = 256 * 1024;

        // A placement is already capped at 4 KiB. Delete/replace commands are much smaller, so the
        // same ceiling is a simple defense against one oversized nested allocation.
        private const int MaxNestedCommandBytes = NetPlacementCommand.MaxEncodedBytes;

        public long OperationId;
        public NetToolOperationItem[] Items;

        public ushort CommandId => Id;

        public void Write(NetworkWriter writer)
        {
            ValidateEnvelope();

            writer.WriteLong(OperationId);
            writer.WriteShort((short)Items.Length);
            for (int i = 0; i < Items.Length; i++)
            {
                NetToolOperationItem item = Items[i];
                writer.WriteShort((short)item.CommandId);
                writer.WriteInt(item.Body.Length);
                writer.WriteBytes(item.Body, 0, item.Body.Length);
            }
        }

        public void Read(NetworkReader reader)
        {
            OperationId = reader.ReadLong();
            if (OperationId <= 0)
                throw new ProtocolException("Invalid net-tool operation id " + OperationId + ".");

            int count = WireGuard.ReadCount(reader, 6, MaxItems);
            if (count == 0)
                throw new ProtocolException("A net-tool operation must contain at least one item.");

            Items = new NetToolOperationItem[count];
            long encodedLength = 8 + 2;
            for (int i = 0; i < count; i++)
            {
                int rawCommandId = reader.ReadShort();
                if (rawCommandId <= 0)
                    throw new ProtocolException("Invalid nested net command id " + rawCommandId + ".");
                if (!IsAllowedNestedCommand((ushort)rawCommandId))
                    throw new ProtocolException("Command " + rawCommandId +
                                                " is not valid inside a net-tool operation.");

                int length = reader.ReadInt();
                if (length <= 0 || length > MaxNestedCommandBytes)
                    throw new ProtocolException("Invalid nested net command length " + length + ".");
                encodedLength += 2 + 4 + length;
                if (encodedLength > MaxEncodedBytes)
                    throw new ProtocolException("Net-tool operation body exceeds the " +
                                                MaxEncodedBytes + "-byte cap.");
                if (length > reader.Remaining)
                    throw new ProtocolException("Nested net command length " + length +
                                                " exceeds the remaining " + reader.Remaining +
                                                " payload byte(s).");

                Items[i] = new NetToolOperationItem
                {
                    CommandId = (ushort)rawCommandId,
                    Body = reader.ReadBytes(length),
                };
            }

            if (reader.Remaining != 0)
                throw new ProtocolException("Trailing bytes in net-tool operation: " +
                                            reader.Remaining + ".");
            ValidateEnvelope();
        }

        public byte[] Encode()
        {
            var writer = new NetworkWriter(1024);
            Write(writer);
            if (writer.Length > MaxEncodedBytes)
                throw new ProtocolException("Net-tool operation body " + writer.Length +
                                            " exceeds the " + MaxEncodedBytes + "-byte cap.");
            return writer.ToArray();
        }

        public static NetToolOperationCommand Decode(byte[] body)
        {
            if (body == null) throw new ProtocolException("Null net-tool operation body.");
            if (body.Length > MaxEncodedBytes)
                throw new ProtocolException("Net-tool operation body " + body.Length +
                                            " exceeds the " + MaxEncodedBytes + "-byte cap.");

            var command = new NetToolOperationCommand();
            command.Read(new NetworkReader(body));
            return command;
        }

        private void ValidateEnvelope()
        {
            if (OperationId <= 0)
                throw new ProtocolException("Invalid net-tool operation id " + OperationId + ".");
            if (Items == null || Items.Length == 0 || Items.Length > MaxItems)
                throw new ProtocolException("Invalid net-tool operation item count.");

            int placementCount = 0;
            int mutationCount = 0;
            long encodedLength = 8 + 2;
            var placements = new NetPlacementCommand[Items.Length];
            for (int i = 0; i < Items.Length; i++)
            {
                NetToolOperationItem item = Items[i];
                if (item == null)
                    throw new ProtocolException("Null item in net-tool operation.");
                if (item.Body == null || item.Body.Length == 0 ||
                    item.Body.Length > MaxNestedCommandBytes)
                    throw new ProtocolException("Invalid nested net command body.");
                encodedLength += 2 + 4 + item.Body.Length;
                if (encodedLength > MaxEncodedBytes)
                    throw new ProtocolException("Net-tool operation body exceeds the " +
                                                MaxEncodedBytes + "-byte cap.");

                switch (item.CommandId)
                {
                    case NetPlacementCommand.Id:
                        NetPlacementCommand placement = DecodePlacement(item.Body);
                        if (!placement.HasNativeCourse)
                            throw new ProtocolException(
                                "A mixed net-tool operation requires native placement courses.");
                        placements[placementCount++] = placement;
                        break;
                    case NetDeleteCommand.Id:
                        DecodeDelete(item.Body);
                        mutationCount++;
                        break;
                    case NetReplaceCommand.Id:
                        DecodeReplace(item.Body);
                        mutationCount++;
                        break;
                    default:
                        throw new ProtocolException("Command " + item.CommandId +
                                                    " is not valid inside a net-tool operation.");
                }
            }

            if (placementCount == 0 || mutationCount == 0)
                throw new ProtocolException("A net-tool operation must mix at least one native " +
                                            "placement with at least one delete or replacement.");
            ValidatePlacementCorrelation(placements, placementCount);
        }

        private void ValidatePlacementCorrelation(NetPlacementCommand[] placements, int count)
        {
            if (count == 0) return;

            var seen = new bool[count];
            for (int i = 0; i < count; i++)
            {
                NetPlacementCommand placement = placements[i];
                if (placement.OperationId != OperationId)
                    throw new ProtocolException("Nested placement operation id " +
                                                placement.OperationId + " does not match outer id " +
                                                OperationId + ".");
                if (placement.CourseCount != count)
                    throw new ProtocolException("Nested placement course count " +
                                                placement.CourseCount + " does not match the " +
                                                count + " placement item(s) in the operation.");
                int index = placement.CourseIndex;
                if (index != i)
                    throw new ProtocolException("Nested placement course index " + index +
                                                " is out of source order; expected " + i + ".");
                if (index < 0 || index >= count || seen[index])
                    throw new ProtocolException("Duplicate or invalid nested placement course index " +
                                                index + ".");
                seen[index] = true;
            }

            for (int i = 0; i < seen.Length; i++)
                if (!seen[i])
                    throw new ProtocolException("Missing nested placement course index " + i + ".");
        }

        private static NetPlacementCommand DecodePlacement(byte[] body)
        {
            var reader = new NetworkReader(body);
            var command = new NetPlacementCommand();
            command.Read(reader);
            RequireFullyConsumed(reader, "net-placement");
            return command;
        }

        private static void DecodeDelete(byte[] body)
        {
            var reader = new NetworkReader(body);
            var command = new NetDeleteCommand();
            command.Read(reader);
            RequireFullyConsumed(reader, "net-delete");
        }

        private static void DecodeReplace(byte[] body)
        {
            var reader = new NetworkReader(body);
            var command = new NetReplaceCommand();
            command.Read(reader);
            RequireFullyConsumed(reader, "net-replace");
        }

        private static void RequireFullyConsumed(NetworkReader reader, string name)
        {
            if (reader.Remaining != 0)
                throw new ProtocolException("Trailing bytes in nested " + name +
                                            " command: " + reader.Remaining + ".");
        }

        private static bool IsAllowedNestedCommand(ushort commandId)
        {
            return commandId == NetPlacementCommand.Id ||
                   commandId == NetDeleteCommand.Id ||
                   commandId == NetReplaceCommand.Id;
        }
    }
}
