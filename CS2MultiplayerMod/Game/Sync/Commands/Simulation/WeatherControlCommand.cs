using System;
using System.IO;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>
    /// Synchronizes atmospheric weather conditions, cloudiness, precipitation, and season locks.
    /// </summary>
    public sealed class WeatherControlCommand
    {
        public const ushort Id = 37;
        public ushort CommandId => Id;

        public float Temperature;
        public float Cloudiness;
        public float Precipitation;
        public byte SeasonIndex; // 0=Spring, 1=Summer, 2=Autumn, 3=Winter

        public byte[] Serialize()
        {
            using (var ms = new MemoryStream(13))
            using (var w = new BinaryWriter(ms))
            {
                w.Write(Temperature);
                w.Write(Cloudiness);
                w.Write(Precipitation);
                w.Write(SeasonIndex);
                return ms.ToArray();
            }
        }

        public static WeatherControlCommand Deserialize(byte[] data)
        {
            if (data == null || data.Length < 13) return null;
            using (var ms = new MemoryStream(data, writable: false))
            using (var r = new BinaryReader(ms))
            {
                return new WeatherControlCommand
                {
                    Temperature = r.ReadSingle(),
                    Cloudiness = r.ReadSingle(),
                    Precipitation = r.ReadSingle(),
                    SeasonIndex = r.ReadByte()
                };
            }
        }
    }
}
