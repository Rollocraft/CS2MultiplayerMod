using System;
using System.IO;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>
    /// Synchronizes city milestone level tiers, progression XP, and development points.
    /// </summary>
    public sealed class MilestoneCommand
    {
        public const ushort Id = 34;
        public ushort CommandId => Id;

        public int CurrentTier;
        public int TotalXP;
        public int DevPoints;

        public byte[] Serialize()
        {
            using (var ms = new MemoryStream(12))
            using (var w = new BinaryWriter(ms))
            {
                w.Write(CurrentTier);
                w.Write(TotalXP);
                w.Write(DevPoints);
                return ms.ToArray();
            }
        }

        public static MilestoneCommand Deserialize(byte[] data)
        {
            if (data == null || data.Length < 12) return null;
            using (var ms = new MemoryStream(data, writable: false))
            using (var r = new BinaryReader(ms))
            {
                return new MilestoneCommand
                {
                    CurrentTier = r.ReadInt32(),
                    TotalXP = r.ReadInt32(),
                    DevPoints = r.ReadInt32()
                };
            }
        }
    }
}
