using System;
using System.IO;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>
    /// Synchronizes electricity and water import/export trading switches with outside connections.
    /// </summary>
    public sealed class UtilityTradeCommand
    {
        public const ushort Id = 51;
        public ushort CommandId => Id;

        public bool ElectricityImport;
        public bool ElectricityExport;
        public bool WaterImport;
        public bool WaterExport;

        public byte[] Serialize()
        {
            using (var ms = new MemoryStream(4))
            using (var w = new BinaryWriter(ms))
            {
                w.Write(ElectricityImport);
                w.Write(ElectricityExport);
                w.Write(WaterImport);
                w.Write(WaterExport);
                return ms.ToArray();
            }
        }

        public static UtilityTradeCommand Deserialize(byte[] data)
        {
            if (data == null || data.Length < 4) return null;
            using (var ms = new MemoryStream(data, writable: false))
            using (var r = new BinaryReader(ms))
            {
                return new UtilityTradeCommand
                {
                    ElectricityImport = r.ReadBoolean(),
                    ElectricityExport = r.ReadBoolean(),
                    WaterImport = r.ReadBoolean(),
                    WaterExport = r.ReadBoolean()
                };
            }
        }
    }
}
