using System;
using System.IO;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>
    /// Synchronizes individual building operational power switches (ON/OFF).
    /// </summary>
    public sealed class BuildingToggleCommand
    {
        public const ushort Id = 45;
        public ushort CommandId => Id;

        public int BuildingIndex;
        public int BuildingVersion;
        public bool IsOperational;

        public byte[] Serialize()
        {
            using (var ms = new MemoryStream(9))
            using (var w = new BinaryWriter(ms))
            {
                w.Write(BuildingIndex);
                w.Write(BuildingVersion);
                w.Write(IsOperational);
                return ms.ToArray();
            }
        }

        public static BuildingToggleCommand Deserialize(byte[] data)
        {
            if (data == null || data.Length < 9) return null;
            using (var ms = new MemoryStream(data, writable: false))
            using (var r = new BinaryReader(ms))
            {
                return new BuildingToggleCommand
                {
                    BuildingIndex = r.ReadInt32(),
                    BuildingVersion = r.ReadInt32(),
                    IsOperational = r.ReadBoolean()
                };
            }
        }
    }
}
