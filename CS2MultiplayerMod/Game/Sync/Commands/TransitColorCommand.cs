using System;
using System.IO;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>
    /// Synchronizes transit line route color customization (e.g. Red Line, Blue Metro).
    /// </summary>
    public sealed class TransitColorCommand
    {
        public const ushort Id = 44;
        public ushort CommandId => Id;

        public int RouteIndex;
        public int RouteVersion;
        public byte R, G, B, A;

        public byte[] Serialize()
        {
            using (var ms = new MemoryStream(12))
            using (var w = new BinaryWriter(ms))
            {
                w.Write(RouteIndex);
                w.Write(RouteVersion);
                w.Write(R);
                w.Write(G);
                w.Write(B);
                w.Write(A);
                return ms.ToArray();
            }
        }

        public static TransitColorCommand Deserialize(byte[] data)
        {
            if (data == null || data.Length < 12) return null;
            using (var ms = new MemoryStream(data, writable: false))
            using (var r = new BinaryReader(ms))
            {
                return new TransitColorCommand
                {
                    RouteIndex = r.ReadInt32(),
                    RouteVersion = r.ReadInt32(),
                    R = r.ReadByte(),
                    G = r.ReadByte(),
                    B = r.ReadByte(),
                    A = r.ReadByte()
                };
            }
        }
    }
}
