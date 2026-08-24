using System;
using System.IO;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>
    /// Synchronizes shared 3D ruler and slope measurement overlays across players.
    /// </summary>
    public sealed class MeasurementCommand
    {
        public const ushort Id = 37;
        public ushort CommandId => Id;

        public int PlayerId;
        public float StartX, StartY, StartZ;
        public float EndX, EndY, EndZ;
        public bool Active;

        public byte[] Serialize()
        {
            using (var ms = new MemoryStream(29))
            using (var w = new BinaryWriter(ms))
            {
                w.Write(PlayerId);
                w.Write(StartX);
                w.Write(StartY);
                w.Write(StartZ);
                w.Write(EndX);
                w.Write(EndY);
                w.Write(EndZ);
                w.Write(Active);
                return ms.ToArray();
            }
        }

        public static MeasurementCommand Deserialize(byte[] data)
        {
            if (data == null || data.Length < 29) return null;
            using (var ms = new MemoryStream(data, writable: false))
            using (var r = new BinaryReader(ms))
            {
                return new MeasurementCommand
                {
                    PlayerId = r.ReadInt32(),
                    StartX = r.ReadSingle(),
                    StartY = r.ReadSingle(),
                    StartZ = r.ReadSingle(),
                    EndX = r.ReadSingle(),
                    EndY = r.ReadSingle(),
                    EndZ = r.ReadSingle(),
                    Active = r.ReadBoolean()
                };
            }
        }
    }
}
