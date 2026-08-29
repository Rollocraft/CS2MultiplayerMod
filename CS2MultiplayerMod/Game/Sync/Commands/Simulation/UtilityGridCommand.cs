using System;
using System.IO;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>
    /// Synchronizes electricity grid import/export caps and water/sewage distribution limits.
    /// </summary>
    public sealed class UtilityGridCommand
    {
        public const ushort Id = 35;
        public ushort CommandId => Id;

        public int ElectricityImportLimit;
        public int ElectricityExportLimit;
        public int WaterImportLimit;
        public int WaterExportLimit;

        public byte[] Serialize()
        {
            using (var ms = new MemoryStream(16))
            using (var w = new BinaryWriter(ms))
            {
                w.Write(ElectricityImportLimit);
                w.Write(ElectricityExportLimit);
                w.Write(WaterImportLimit);
                w.Write(WaterExportLimit);
                return ms.ToArray();
            }
        }

        public static UtilityGridCommand Deserialize(byte[] data)
        {
            if (data == null || data.Length < 16) return null;
            using (var ms = new MemoryStream(data, writable: false))
            using (var r = new BinaryReader(ms))
            {
                return new UtilityGridCommand
                {
                    ElectricityImportLimit = r.ReadInt32(),
                    ElectricityExportLimit = r.ReadInt32(),
                    WaterImportLimit = r.ReadInt32(),
                    WaterExportLimit = r.ReadInt32()
                };
            }
        }
    }
}
