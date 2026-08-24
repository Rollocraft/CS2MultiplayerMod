using System;
using System.IO;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>
    /// Synchronizes intersection traffic light toggles, stop signs, and crosswalk rules.
    /// </summary>
    public sealed class TrafficLightCommand
    {
        public const ushort Id = 39;
        public ushort CommandId => Id;

        public int NodeIndex;
        public int NodeVersion;
        public bool HasTrafficLights;
        public bool HasAllWayStop;
        public bool HasPedestrianCrosswalk;

        public byte[] Serialize()
        {
            using (var ms = new MemoryStream(11))
            using (var w = new BinaryWriter(ms))
            {
                w.Write(NodeIndex);
                w.Write(NodeVersion);
                w.Write(HasTrafficLights);
                w.Write(HasAllWayStop);
                w.Write(HasPedestrianCrosswalk);
                return ms.ToArray();
            }
        }

        public static TrafficLightCommand Deserialize(byte[] data)
        {
            if (data == null || data.Length < 11) return null;
            using (var ms = new MemoryStream(data, writable: false))
            using (var r = new BinaryReader(ms))
            {
                return new TrafficLightCommand
                {
                    NodeIndex = r.ReadInt32(),
                    NodeVersion = r.ReadInt32(),
                    HasTrafficLights = r.ReadBoolean(),
                    HasAllWayStop = r.ReadBoolean(),
                    HasPedestrianCrosswalk = r.ReadBoolean()
                };
            }
        }
    }
}
