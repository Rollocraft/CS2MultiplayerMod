using System;
using System.IO;
using System.Text;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>
    /// Synchronizes custom names given to districts, buildings, roads, and transit lines.
    /// </summary>
    public sealed class CustomNameCommand
    {
        public const ushort Id = 33;
        public ushort CommandId => Id;

        public int EntityIndex;
        public int EntityVersion;
        public string CustomName;

        public byte[] Serialize()
        {
            using (var ms = new MemoryStream(64))
            using (var w = new BinaryWriter(ms))
            {
                w.Write(EntityIndex);
                w.Write(EntityVersion);
                byte[] strBytes = Encoding.UTF8.GetBytes(CustomName ?? "");
                w.Write((ushort)strBytes.Length);
                if (strBytes.Length > 0)
                {
                    w.Write(strBytes);
                }
                return ms.ToArray();
            }
        }

        public static CustomNameCommand Deserialize(byte[] data)
        {
            if (data == null || data.Length < 10) return null;
            using (var ms = new MemoryStream(data, writable: false))
            using (var r = new BinaryReader(ms))
            {
                int index = r.ReadInt32();
                int version = r.ReadInt32();
                ushort len = r.ReadUInt16();
                string name = "";
                if (len > 0 && len <= data.Length - 10)
                {
                    byte[] strBytes = r.ReadBytes(len);
                    name = Encoding.UTF8.GetString(strBytes);
                }
                return new CustomNameCommand
                {
                    EntityIndex = index,
                    EntityVersion = version,
                    CustomName = name
                };
            }
        }
    }
}
