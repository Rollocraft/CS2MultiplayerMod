using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Sync;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>
    /// "A player set a policy on this district/route/building." Applied on the receiver
    /// through the game's own <c>PoliciesUISystem.SetPolicy</c>, so modifiers and triggers
    /// refresh exactly like a local click - see <see cref="PolicySyncSystem"/>. City-wide
    /// policies travel separately (the editable CityPolicy state channel).
    /// </summary>
    public sealed class EntityPolicyCommand : ISimulationCommand
    {
        public const ushort Id = 15;

        public const byte KindDistrict = 1;
        public const byte KindRoute = 2;
        public const byte KindBuilding = 3;

        public byte TargetKind;
        public string TargetPrefabName;
        public float AnchorX, AnchorY, AnchorZ;
        public string PolicyPrefabName;
        public bool Active;
        public float Adjustment;

        public ushort CommandId => Id;

        public void Write(NetworkWriter writer)
        {
            writer.WriteByte(TargetKind);
            writer.WriteString(TargetPrefabName);
            writer.WriteFloat(AnchorX); writer.WriteFloat(AnchorY); writer.WriteFloat(AnchorZ);
            writer.WriteString(PolicyPrefabName);
            writer.WriteBool(Active);
            writer.WriteFloat(Adjustment);
        }

        public void Read(NetworkReader reader)
        {
            TargetKind = reader.ReadByte();
            if (TargetKind < KindDistrict || TargetKind > KindBuilding)
                throw new ProtocolException("Unknown policy target kind: " + TargetKind + ".");
            TargetPrefabName = WireGuard.ReadName(reader);
            AnchorX = WireGuard.ReadCoordinate(reader); AnchorY = WireGuard.ReadCoordinate(reader); AnchorZ = WireGuard.ReadCoordinate(reader);
            PolicyPrefabName = WireGuard.ReadName(reader);
            Active = reader.ReadBool();
            Adjustment = WireGuard.ReadFinite(reader);
        }

        public byte[] Encode()
        {
            var writer = new NetworkWriter(96);
            Write(writer);
            return writer.ToArray();
        }

        public static EntityPolicyCommand Decode(byte[] body)
        {
            var command = new EntityPolicyCommand();
            command.Read(new NetworkReader(body));
            return command;
        }
    }
}
