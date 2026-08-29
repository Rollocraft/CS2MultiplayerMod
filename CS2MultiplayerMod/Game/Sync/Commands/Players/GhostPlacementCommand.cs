using System;
using System.IO;
using System.Text;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>
    /// Synchronizes active placement tool blueprint holograms (buildings, roads, zones).
    /// </summary>
    public sealed class GhostPlacementCommand
    {
        public const ushort Id = 38;
        public ushort CommandId => Id;

        public int PlayerId;
        public float X, Y, Z;
        public float RotationYaw;
        public string PrefabName;

        public byte[] Serialize()
        {
            using (var ms = new MemoryStream(48))
            using (var w = new BinaryWriter(ms))
            {
                w.Write(PlayerId);
                w.Write(X);
                w.Write(Y);
                w.Write(Z);
                w.Write(RotationYaw);
                byte[] strBytes = Encoding.UTF8.GetBytes(PrefabName ?? "");
                w.Write((ushort)strBytes.Length);
                if (strBytes.Length > 0) w.Write(strBytes);
                return ms.ToArray();
            }
        }

        public static GhostPlacementCommand Deserialize(byte[] data)
        {
            if (data == null || data.Length < 22) return null;
            using (var ms = new MemoryStream(data, writable: false))
            using (var r = new BinaryReader(ms))
            {
                int pid = r.ReadInt32();
                float x = r.ReadSingle();
                float y = r.ReadSingle();
                float z = r.ReadSingle();
                float yaw = r.ReadSingle();
                ushort len = r.ReadUInt16();
                string prefab = "";
                if (len > 0 && len <= data.Length - 22)
                {
                    prefab = Encoding.UTF8.GetString(r.ReadBytes(len));
                }
                return new GhostPlacementCommand
                {
                    PlayerId = pid,
                    X = x,
                    Y = y,
                    Z = z,
                    RotationYaw = yaw,
                    PrefabName = prefab
                };
            }
        }
    }
}
