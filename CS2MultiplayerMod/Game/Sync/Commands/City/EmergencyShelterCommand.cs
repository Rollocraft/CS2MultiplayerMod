using System;
using System.IO;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>
    /// Synchronizes emergency shelter evacuation state and siren alarms.
    /// </summary>
    public sealed class EmergencyShelterCommand
    {
        public const ushort Id = 50;
        public ushort CommandId => Id;

        public int BuildingIndex;
        public int BuildingVersion;
        public bool IsEvacuating;

        public byte[] Serialize()
        {
            using (var ms = new MemoryStream(9))
            using (var w = new BinaryWriter(ms))
            {
                w.Write(BuildingIndex);
                w.Write(BuildingVersion);
                w.Write(IsEvacuating);
                return ms.ToArray();
            }
        }

        public static EmergencyShelterCommand Deserialize(byte[] data)
        {
            if (data == null || data.Length < 9) return null;
            using (var ms = new MemoryStream(data, writable: false))
            using (var r = new BinaryReader(ms))
            {
                return new EmergencyShelterCommand
                {
                    BuildingIndex = r.ReadInt32(),
                    BuildingVersion = r.ReadInt32(),
                    IsEvacuating = r.ReadBoolean()
                };
            }
        }
    }
}
