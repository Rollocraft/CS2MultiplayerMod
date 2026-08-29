using System;
using System.IO;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>
    /// Synchronizes public transit line passenger ticket pricing / fares.
    /// </summary>
    public sealed class TransitFareCommand
    {
        public const ushort Id = 53;
        public ushort CommandId => Id;

        public int RouteNumber;
        public int TicketPrice;

        public byte[] Serialize()
        {
            using (var ms = new MemoryStream(8))
            using (var w = new BinaryWriter(ms))
            {
                w.Write(RouteNumber);
                w.Write(TicketPrice);
                return ms.ToArray();
            }
        }

        public static TransitFareCommand Deserialize(byte[] data)
        {
            if (data == null || data.Length < 8) return null;
            using (var ms = new MemoryStream(data, writable: false))
            using (var r = new BinaryReader(ms))
            {
                return new TransitFareCommand
                {
                    RouteNumber = r.ReadInt32(),
                    TicketPrice = r.ReadInt32()
                };
            }
        }
    }
}
