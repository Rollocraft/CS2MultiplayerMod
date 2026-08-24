using System;
using System.IO;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>
    /// Synchronizes city loan borrowing, repayment, and credit line actions.
    /// </summary>
    public sealed class CityLoanCommand
    {
        public const ushort Id = 29;
        public ushort CommandId => Id;

        public int LoanId;
        public int AmountDelta; // Positive for borrowing, negative for repayment
        public int TotalDebt;

        public byte[] Serialize()
        {
            using (var ms = new MemoryStream(12))
            using (var w = new BinaryWriter(ms))
            {
                w.Write(LoanId);
                w.Write(AmountDelta);
                w.Write(TotalDebt);
                return ms.ToArray();
            }
        }

        public static CityLoanCommand Deserialize(byte[] data)
        {
            if (data == null || data.Length < 12) return null;
            using (var ms = new MemoryStream(data, writable: false))
            using (var r = new BinaryReader(ms))
            {
                return new CityLoanCommand
                {
                    LoanId = r.ReadInt32(),
                    AmountDelta = r.ReadInt32(),
                    TotalDebt = r.ReadInt32()
                };
            }
        }
    }
}
