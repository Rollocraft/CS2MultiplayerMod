using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Sync;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>One brush frame's objects of one prefab, so a vegetation stroke does not flood the inbox.</summary>
    public sealed class ObjectPlacementBatchCommand : ISimulationCommand
    {
        public const ushort Id = 31;
        public const int MaxPlacements = 4096;
        public const int MaxEncodedBytes = 256 * 1024;

        public struct Placement
        {
            public float PosX, PosY, PosZ;
            public float RotX, RotY, RotZ, RotW;
            public int RandomSeed;
            public float Age;
        }

        public string PrefabName;
        public Placement[] Placements;
        public ushort CommandId => Id;

        public void Write(NetworkWriter writer)
        {
            if (string.IsNullOrEmpty(PrefabName))
                throw new ProtocolException("Object-placement batch has no prefab name.");
            if (Placements == null || Placements.Length == 0 ||
                Placements.Length > MaxPlacements)
                throw new ProtocolException("Invalid object-placement batch count.");

            writer.WriteString(PrefabName);
            writer.WriteShort((short)Placements.Length);
            for (int i = 0; i < Placements.Length; i++)
            {
                Placement value = Placements[i];
                writer.WriteFloat(value.PosX);
                writer.WriteFloat(value.PosY);
                writer.WriteFloat(value.PosZ);
                writer.WriteFloat(value.RotX);
                writer.WriteFloat(value.RotY);
                writer.WriteFloat(value.RotZ);
                writer.WriteFloat(value.RotW);
                writer.WriteInt(value.RandomSeed);
                writer.WriteFloat(value.Age);
            }
        }

        public void Read(NetworkReader reader)
        {
            PrefabName = WireGuard.ReadName(reader);
            int count = WireGuard.ReadCount(reader, 36, MaxPlacements);
            if (count == 0)
                throw new ProtocolException("Empty object-placement batch.");

            Placements = new Placement[count];
            for (int i = 0; i < count; i++)
            {
                Placement value = new Placement
                {
                    PosX = WireGuard.ReadCoordinate(reader),
                    PosY = WireGuard.ReadCoordinate(reader),
                    PosZ = WireGuard.ReadCoordinate(reader),
                    RotX = WireGuard.ReadFinite(reader),
                    RotY = WireGuard.ReadFinite(reader),
                    RotZ = WireGuard.ReadFinite(reader),
                    RotW = WireGuard.ReadFinite(reader),
                    RandomSeed = reader.ReadInt(),
                    Age = WireGuard.ReadFinite(reader),
                };
                float rotationLengthSq = value.RotX * value.RotX + value.RotY * value.RotY +
                                         value.RotZ * value.RotZ + value.RotW * value.RotW;
                if (rotationLengthSq < 0.25f || rotationLengthSq > 2.25f)
                    throw new ProtocolException("Implausible batched object rotation length " +
                                                rotationLengthSq + ".");
                if (value.RandomSeed < 0 || value.RandomSeed > ushort.MaxValue)
                    throw new ProtocolException("Batched object random seed is outside ushort range.");
                if (value.Age < 0f || value.Age > 1f)
                    throw new ProtocolException("Batched object age is outside [0,1].");
                Placements[i] = value;
            }
            if (reader.Remaining != 0)
                throw new ProtocolException("Trailing bytes in object-placement batch: " +
                                            reader.Remaining + ".");
        }

        public byte[] Encode()
        {
            var writer = new NetworkWriter(128);
            Write(writer);
            if (writer.Length > MaxEncodedBytes)
                throw new ProtocolException("Object-placement batch body " + writer.Length +
                                            " exceeds the " + MaxEncodedBytes + "-byte cap.");
            return writer.ToArray();
        }

        public static ObjectPlacementBatchCommand Decode(byte[] body)
        {
            if (body == null) throw new ProtocolException("Null object-placement batch body.");
            if (body.Length > MaxEncodedBytes)
                throw new ProtocolException("Object-placement batch body " + body.Length +
                                            " exceeds the " + MaxEncodedBytes + "-byte cap.");
            var command = new ObjectPlacementBatchCommand();
            command.Read(new NetworkReader(body));
            return command;
        }
    }
}
