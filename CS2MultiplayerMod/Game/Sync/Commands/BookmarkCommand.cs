using System;
using System.IO;
using System.Text;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>
    /// Synchronizes shared camera navigation bookmarks across players.
    /// </summary>
    public sealed class BookmarkCommand
    {
        public const ushort Id = 36;
        public ushort CommandId => Id;

        public string BookmarkName;
        public float X, Y, Z;

        public byte[] Serialize()
        {
            using (var ms = new MemoryStream(32))
            using (var w = new BinaryWriter(ms))
            {
                byte[] nameBytes = Encoding.UTF8.GetBytes(BookmarkName ?? "");
                w.Write((ushort)nameBytes.Length);
                if (nameBytes.Length > 0) w.Write(nameBytes);
                w.Write(X);
                w.Write(Y);
                w.Write(Z);
                return ms.ToArray();
            }
        }

        public static BookmarkCommand Deserialize(byte[] data)
        {
            if (data == null || data.Length < 14) return null;
            using (var ms = new MemoryStream(data, writable: false))
            using (var r = new BinaryReader(ms))
            {
                ushort len = r.ReadUInt16();
                string name = "";
                if (len > 0 && len <= data.Length - 14)
                {
                    name = Encoding.UTF8.GetString(r.ReadBytes(len));
                }
                float x = r.ReadSingle();
                float y = r.ReadSingle();
                float z = r.ReadSingle();
                return new BookmarkCommand
                {
                    BookmarkName = name,
                    X = x,
                    Y = y,
                    Z = z
                };
            }
        }
    }
}
