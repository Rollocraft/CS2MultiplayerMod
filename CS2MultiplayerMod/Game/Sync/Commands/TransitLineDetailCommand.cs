using System;
using System.IO;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>
    /// Synchronizes transit route ticket pricing and assigned vehicle capacity allocation.
    /// </summary>
    public sealed class TransitLineDetailCommand
    {
        public const ushort Id = 40;
        public ushort CommandId => Id;

        public int RouteIndex;
        public int RouteVersion;
        public ushort TicketPrice;
        public ushort VehicleCount;

        public byte[] Serialize()
        {
            using (var ms = new MemoryStream(12))
            using (var w = new BinaryWriter(ms))
            {
                w.Write(RouteIndex);
                w.Write(RouteVersion);
                w.Write(TicketPrice);
                w.Write(VehicleCount);
                return ms.ToArray();
            }
        }

        public static TransitLineDetailCommand Deserialize(byte[] data)
        {
            if (data == null || data.Length < 12) return null;
            using (var ms = new MemoryStream(data, writable: false))
            using (var r = new BinaryReader(ms))
            {
                return new TransitLineDetailCommand
                {
                    RouteIndex = r.ReadInt32(),
                    RouteVersion = r.ReadInt32(),
                    TicketPrice = r.ReadUInt16(),
                    VehicleCount = r.ReadUInt16()
                };
            }
        }
    }
}
