using System;
using System.IO;
using System.Text;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>
    /// Synchronizes public citizen Chirper social media feed posts and municipal announcements.
    /// </summary>
    public sealed class ChirperCommand
    {
        public const ushort Id = 49;
        public ushort CommandId => Id;

        public int SenderPlayerId;
        public string SenderName;
        public string MessageText;
        public byte AvatarIndex;

        public byte[] Serialize()
        {
            using (var ms = new MemoryStream(64))
            using (var w = new BinaryWriter(ms))
            {
                w.Write(SenderPlayerId);
                byte[] nameBytes = Encoding.UTF8.GetBytes(SenderName ?? "");
                w.Write((ushort)nameBytes.Length);
                if (nameBytes.Length > 0) w.Write(nameBytes);

                byte[] msgBytes = Encoding.UTF8.GetBytes(MessageText ?? "");
                w.Write((ushort)msgBytes.Length);
                if (msgBytes.Length > 0) w.Write(msgBytes);

                w.Write(AvatarIndex);
                return ms.ToArray();
            }
        }

        public static ChirperCommand Deserialize(byte[] data)
        {
            if (data == null || data.Length < 9) return null;
            using (var ms = new MemoryStream(data, writable: false))
            using (var r = new BinaryReader(ms))
            {
                int pid = r.ReadInt32();
                ushort nameLen = r.ReadUInt16();
                string name = "";
                if (nameLen > 0 && ms.Position + nameLen <= ms.Length)
                {
                    name = Encoding.UTF8.GetString(r.ReadBytes(nameLen));
                }

                ushort msgLen = r.ReadUInt16();
                string msg = "";
                if (msgLen > 0 && ms.Position + msgLen <= ms.Length)
                {
                    msg = Encoding.UTF8.GetString(r.ReadBytes(msgLen));
                }

                byte avatar = ms.Position < ms.Length ? r.ReadByte() : (byte)0;

                return new ChirperCommand
                {
                    SenderPlayerId = pid,
                    SenderName = name,
                    MessageText = msg,
                    AvatarIndex = avatar
                };
            }
        }
    }
}
