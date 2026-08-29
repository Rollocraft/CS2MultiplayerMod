using System;
using System.IO;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>
    /// Synchronizes sun time-of-day angle and perpetual daylight lock across players.
    /// </summary>
    public sealed class DaylightCommand
    {
        public const ushort Id = 54;
        public ushort CommandId => Id;

        public bool OverrideTime;
        public float TimeOfDay; // 0.0 - 24.0 (12.0 = noon)

        public byte[] Serialize()
        {
            using (var ms = new MemoryStream(5))
            using (var w = new BinaryWriter(ms))
            {
                w.Write(OverrideTime);
                w.Write(TimeOfDay);
                return ms.ToArray();
            }
        }

        public static DaylightCommand Deserialize(byte[] data)
        {
            if (data == null || data.Length < 5) return null;
            using (var ms = new MemoryStream(data, writable: false))
            using (var r = new BinaryReader(ms))
            {
                return new DaylightCommand
                {
                    OverrideTime = r.ReadBoolean(),
                    TimeOfDay = r.ReadSingle()
                };
            }
        }
    }
}
