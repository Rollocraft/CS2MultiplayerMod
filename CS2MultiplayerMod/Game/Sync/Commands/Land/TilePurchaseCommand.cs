using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Sync;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>
    /// "A player purchased these map tiles." Tiles are matched by their polygon centroid
    /// (the tile grid is generated identically from the same map on every machine).
    /// Carries the price the buyer's game charged so the host can charge the shared
    /// treasury the exact same amount - see <see cref="TilePurchaseSyncSystem"/>.
    /// </summary>
    public sealed class TilePurchaseCommand : ISimulationCommand
    {
        public const ushort Id = 14;

        public int TotalCost;
        public float[] CenterX, CenterZ;

        public ushort CommandId => Id;

        public void Write(NetworkWriter writer)
        {
            writer.WriteInt(TotalCost);
            int count = CenterX != null ? CenterX.Length : 0;
            writer.WriteShort((short)count);
            for (int i = 0; i < count; i++)
            {
                writer.WriteFloat(CenterX[i]);
                writer.WriteFloat(CenterZ[i]);
            }
        }

        public void Read(NetworkReader reader)
        {
            TotalCost = reader.ReadInt();
            if (TotalCost < 0)
                throw new ProtocolException("Negative tile cost: " + TotalCost + ".");
            int count = WireGuard.ReadCount(reader, 8);
            CenterX = new float[count];
            CenterZ = new float[count];
            for (int i = 0; i < count; i++)
            {
                CenterX[i] = WireGuard.ReadCoordinate(reader);
                CenterZ[i] = WireGuard.ReadCoordinate(reader);
            }
        }

        public byte[] Encode()
        {
            var writer = new NetworkWriter(16 + (CenterX != null ? CenterX.Length * 8 : 0));
            Write(writer);
            return writer.ToArray();
        }

        public static TilePurchaseCommand Decode(byte[] body)
        {
            var command = new TilePurchaseCommand();
            command.Read(new NetworkReader(body));
            return command;
        }
    }
}
