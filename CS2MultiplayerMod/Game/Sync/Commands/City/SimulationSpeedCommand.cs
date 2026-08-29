using System;
using System.IO;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>
    /// Synchronizes simulation play/pause state and speed multiplier step (1x, 2x, 3x).
    /// </summary>
    public sealed class SimulationSpeedCommand
    {
        public const ushort Id = 32;
        public ushort CommandId => Id;

        public bool Paused;
        public byte SpeedIndex; // 0=Paused, 1=1x, 2=2x, 3=3x

        public byte[] Serialize()
        {
            using (var ms = new MemoryStream(2))
            using (var w = new BinaryWriter(ms))
            {
                w.Write(Paused);
                w.Write(SpeedIndex);
                return ms.ToArray();
            }
        }

        public static SimulationSpeedCommand Deserialize(byte[] data)
        {
            if (data == null || data.Length < 2) return null;
            using (var ms = new MemoryStream(data, writable: false))
            using (var r = new BinaryReader(ms))
            {
                return new SimulationSpeedCommand
                {
                    Paused = r.ReadBoolean(),
                    SpeedIndex = r.ReadByte()
                };
            }
        }
    }
}
