using System;
using System.IO;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>
    /// Synchronizes service building allocated vehicle fleet limits (police, fire, medical, bus/train depots).
    /// </summary>
    public sealed class ServiceFleetCommand
    {
        public const ushort Id = 52;
        public ushort CommandId => Id;

        public int BuildingIndex;
        public int BuildingVersion;
        public int VehicleLimit;

        public byte[] Serialize()
        {
            using (var ms = new MemoryStream(12))
            using (var w = new BinaryWriter(ms))
            {
                w.Write(BuildingIndex);
                w.Write(BuildingVersion);
                w.Write(VehicleLimit);
                return ms.ToArray();
            }
        }

        public static ServiceFleetCommand Deserialize(byte[] data)
        {
            if (data == null || data.Length < 12) return null;
            using (var ms = new MemoryStream(data, writable: false))
            using (var r = new BinaryReader(ms))
            {
                return new ServiceFleetCommand
                {
                    BuildingIndex = r.ReadInt32(),
                    BuildingVersion = r.ReadInt32(),
                    VehicleLimit = r.ReadInt32()
                };
            }
        }
    }
}
