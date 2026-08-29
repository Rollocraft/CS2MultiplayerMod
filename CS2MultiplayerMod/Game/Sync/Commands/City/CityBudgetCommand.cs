using System;
using System.IO;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>
    /// Synchronizes municipal budget funding percentages and tax rate percentages.
    /// </summary>
    public sealed class CityBudgetCommand
    {
        public const ushort Id = 30;
        public ushort CommandId => Id;

        public byte ServiceType; // 0=Electricity, 1=Water, 2=Healthcare, 3=Education, 4=Police, 5=Fire, 6=Transit, etc.
        public byte BudgetPercent; // 0-150%
        public byte ZoneTaxType; // 0=ResidentialLow, 1=ResidentialHigh, 2=Commercial, 3=Industrial, 4=Office, 255=None
        public byte TaxRatePercent; // 1-30%

        public byte[] Serialize()
        {
            using (var ms = new MemoryStream(4))
            using (var w = new BinaryWriter(ms))
            {
                w.Write(ServiceType);
                w.Write(BudgetPercent);
                w.Write(ZoneTaxType);
                w.Write(TaxRatePercent);
                return ms.ToArray();
            }
        }

        public static CityBudgetCommand Deserialize(byte[] data)
        {
            if (data == null || data.Length < 4) return null;
            using (var ms = new MemoryStream(data, writable: false))
            using (var r = new BinaryReader(ms))
            {
                return new CityBudgetCommand
                {
                    ServiceType = r.ReadByte(),
                    BudgetPercent = r.ReadByte(),
                    ZoneTaxType = r.ReadByte(),
                    TaxRatePercent = r.ReadByte()
                };
            }
        }
    }
}
