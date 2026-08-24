using System;
using System.IO;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>
    /// Synchronizes park and tourist attraction ticket entrance admission fees.
    /// </summary>
    public sealed class ParkFeeCommand
    {
        public const ushort Id = 42;
        public ushort CommandId => Id;

        public int ParkIndex;
        public int ParkVersion;
        public ushort FeeAmount;

        public byte[] Serialize()
        {
            using (var ms = new MemoryStream(10))
            using (var w = new BinaryWriter(ms))
            {
                w.Write(ParkIndex);
                w.Write(ParkVersion);
                w.Write(FeeAmount);
                return ms.ToArray();
            }
        }

        public static ParkFeeCommand Deserialize(byte[] data)
        {
            if (data == null || data.Length < 10) return null;
            using (var ms = new MemoryStream(data, writable: false))
            using (var r = new BinaryReader(ms))
            {
                return new ParkFeeCommand
                {
                    ParkIndex = r.ReadInt32(),
                    ParkVersion = r.ReadInt32(),
                    FeeAmount = r.ReadUInt16()
                };
            }
        }
    }
}
