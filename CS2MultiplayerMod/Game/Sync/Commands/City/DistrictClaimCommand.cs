using System;
using System.IO;
using System.Text;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>
    /// Synchronizes district ownership claims and designated mayor badges across players.
    /// </summary>
    public sealed class DistrictClaimCommand
    {
        public const ushort Id = 39;
        public ushort CommandId => Id;

        public int DistrictIndex;
        public int DistrictVersion;
        public int OwnerPlayerId;
        public string OwnerPlayerName;

        public byte[] Serialize()
        {
            using (var ms = new MemoryStream(32))
            using (var w = new BinaryWriter(ms))
            {
                w.Write(DistrictIndex);
                w.Write(DistrictVersion);
                w.Write(OwnerPlayerId);
                byte[] nameBytes = Encoding.UTF8.GetBytes(OwnerPlayerName ?? "");
                w.Write((ushort)nameBytes.Length);
                if (nameBytes.Length > 0) w.Write(nameBytes);
                return ms.ToArray();
            }
        }

        public static DistrictClaimCommand Deserialize(byte[] data)
        {
            if (data == null || data.Length < 14) return null;
            using (var ms = new MemoryStream(data, writable: false))
            using (var r = new BinaryReader(ms))
            {
                int index = r.ReadInt32();
                int version = r.ReadInt32();
                int pid = r.ReadInt32();
                ushort len = r.ReadUInt16();
                string name = "";
                if (len > 0 && len <= data.Length - 14)
                {
                    name = Encoding.UTF8.GetString(r.ReadBytes(len));
                }
                return new DistrictClaimCommand
                {
                    DistrictIndex = index,
                    DistrictVersion = version,
                    OwnerPlayerId = pid,
                    OwnerPlayerName = name
                };
            }
        }
    }
}
