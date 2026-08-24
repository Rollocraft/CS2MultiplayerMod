using System;
using System.IO;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>
    /// Synchronizes rolling simulation state checksum hashes to detect desyncs automatically.
    /// </summary>
    public sealed class ChecksumCommand
    {
        public const ushort Id = 38;
        public ushort CommandId => Id;

        public uint SimulationFrame;
        public uint StateHash;
        public long Money;
        public int Population;

        public byte[] Serialize()
        {
            using (var ms = new MemoryStream(20))
            using (var w = new BinaryWriter(ms))
            {
                w.Write(SimulationFrame);
                w.Write(StateHash);
                w.Write(Money);
                w.Write(Population);
                return ms.ToArray();
            }
        }

        public static ChecksumCommand Deserialize(byte[] data)
        {
            if (data == null || data.Length < 20) return null;
            using (var ms = new MemoryStream(data, writable: false))
            using (var r = new BinaryReader(ms))
            {
                return new ChecksumCommand
                {
                    SimulationFrame = r.ReadUInt32(),
                    StateHash = r.ReadUInt32(),
                    Money = r.ReadInt64(),
                    Population = r.ReadInt32()
                };
            }
        }
    }
}
