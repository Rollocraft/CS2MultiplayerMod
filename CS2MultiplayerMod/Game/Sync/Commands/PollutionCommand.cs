using System;
using System.IO;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>
    /// Synchronizes global city environmental pollution indices (air, ground, noise).
    /// </summary>
    public sealed class PollutionCommand
    {
        public const ushort Id = 32;
        public ushort CommandId => Id;

        public short AverageAirPollution;
        public short AverageGroundPollution;
        public short AverageNoisePollution;

        public byte[] Serialize()
        {
            using (var ms = new MemoryStream(6))
            using (var w = new BinaryWriter(ms))
            {
                w.Write(AverageAirPollution);
                w.Write(AverageGroundPollution);
                w.Write(AverageNoisePollution);
                return ms.ToArray();
            }
        }

        public static PollutionCommand Deserialize(byte[] data)
        {
            if (data == null || data.Length < 6) return null;
            using (var ms = new MemoryStream(data, writable: false))
            using (var r = new BinaryReader(ms))
            {
                return new PollutionCommand
                {
                    AverageAirPollution = r.ReadInt16(),
                    AverageGroundPollution = r.ReadInt16(),
                    AverageNoisePollution = r.ReadInt16()
                };
            }
        }
    }
}
