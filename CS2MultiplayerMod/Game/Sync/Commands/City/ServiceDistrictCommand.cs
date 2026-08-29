using System;
using System.Collections.Generic;
using System.IO;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>
    /// Synchronizes service building territory restrictions (assigning a facility to specific districts).
    /// </summary>
    public sealed class ServiceDistrictCommand
    {
        public const ushort Id = 47;
        public ushort CommandId => Id;

        public int BuildingIndex;
        public int BuildingVersion;
        public List<int> DistrictIndices = new List<int>();

        public byte[] Serialize()
        {
            using (var ms = new MemoryStream(10 + DistrictIndices.Count * 4))
            using (var w = new BinaryWriter(ms))
            {
                w.Write(BuildingIndex);
                w.Write(BuildingVersion);
                w.Write((ushort)DistrictIndices.Count);
                foreach (int d in DistrictIndices)
                {
                    w.Write(d);
                }
                return ms.ToArray();
            }
        }

        public static ServiceDistrictCommand Deserialize(byte[] data)
        {
            if (data == null || data.Length < 10) return null;
            using (var ms = new MemoryStream(data, writable: false))
            using (var r = new BinaryReader(ms))
            {
                int bIdx = r.ReadInt32();
                int bVer = r.ReadInt32();
                ushort count = r.ReadUInt16();
                var list = new List<int>(count);
                for (int i = 0; i < count && ms.Position + 4 <= ms.Length; i++)
                {
                    list.Add(r.ReadInt32());
                }
                return new ServiceDistrictCommand
                {
                    BuildingIndex = bIdx,
                    BuildingVersion = bVer,
                    DistrictIndices = list
                };
            }
        }
    }
}
