using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Sync;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>
    /// "A player changed a piece of road in place" - carries the RESULTING composition,
    /// not a delta, for either an edge (trees, grass, wide sidewalks, sound barriers, street
    /// lights, crosswalks, roadside tree-row styles) or a node (traffic lights, all-way stop,
    /// roundabout - <see cref="IsNode"/>). All-zero flags mean "the upgrade was removed".
    /// See <see cref="NetUpgradeSyncSystem"/>.
    /// </summary>
    public sealed class NetUpgradeCommand : ISimulationCommand
    {
        public const ushort Id = 9;

        /// <summary>A segment has at most a few side/median replacement rows (2 sides x styles).</summary>
        public const int MaxSubReplacements = 8;

        /// <summary>One roadside sub-replacement row (e.g. a specific tree style on one side).</summary>
        public struct SubRep
        {
            public string PrefabName;
            public byte Type;
            public sbyte Side;
            public byte AgeMask;
        }

        private static readonly SubRep[] NoSubReps = new SubRep[0];

        public string PrefabName;
        public float Ax, Ay, Az;
        public float Dx, Dy, Dz;
        public uint General, Left, Right;
        public bool IsNode;
        public SubRep[] SubReps = NoSubReps;

        public ushort CommandId => Id;

        public void Write(NetworkWriter writer)
        {
            writer.WriteString(PrefabName);
            writer.WriteFloat(Ax); writer.WriteFloat(Ay); writer.WriteFloat(Az);
            writer.WriteFloat(Dx); writer.WriteFloat(Dy); writer.WriteFloat(Dz);
            writer.WriteInt((int)General);
            writer.WriteInt((int)Left);
            writer.WriteInt((int)Right);
            writer.WriteBool(IsNode);
            SubRep[] subs = SubReps ?? NoSubReps;
            writer.WriteByte((byte)subs.Length);
            for (int i = 0; i < subs.Length; i++)
            {
                writer.WriteString(subs[i].PrefabName);
                writer.WriteByte(subs[i].Type);
                writer.WriteByte((byte)subs[i].Side);
                writer.WriteByte(subs[i].AgeMask);
            }
        }

        public void Read(NetworkReader reader)
        {
            PrefabName = WireGuard.ReadName(reader);
            Ax = WireGuard.ReadCoordinate(reader); Ay = WireGuard.ReadCoordinate(reader); Az = WireGuard.ReadCoordinate(reader);
            Dx = WireGuard.ReadCoordinate(reader); Dy = WireGuard.ReadCoordinate(reader); Dz = WireGuard.ReadCoordinate(reader);
            General = (uint)reader.ReadInt();
            Left = (uint)reader.ReadInt();
            Right = (uint)reader.ReadInt();
            IsNode = reader.ReadBool();
            int count = reader.ReadByte();
            if (count > MaxSubReplacements)
                throw new ProtocolException("Sub-replacement count " + count + " exceeds limit " + MaxSubReplacements + ".");
            if (count == 0)
            {
                SubReps = NoSubReps;
                return;
            }
            var subs = new SubRep[count];
            for (int i = 0; i < count; i++)
            {
                subs[i].PrefabName = WireGuard.ReadName(reader);
                subs[i].Type = reader.ReadByte();
                subs[i].Side = (sbyte)reader.ReadByte();
                subs[i].AgeMask = reader.ReadByte();
            }
            SubReps = subs;
        }

        public byte[] Encode()
        {
            var writer = new NetworkWriter(96);
            Write(writer);
            return writer.ToArray();
        }

        public static NetUpgradeCommand Decode(byte[] body)
        {
            var command = new NetUpgradeCommand();
            command.Read(new NetworkReader(body));
            return command;
        }
    }
}
