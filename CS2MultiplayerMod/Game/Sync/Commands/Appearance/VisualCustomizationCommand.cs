using System;
using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Sync;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    [Flags]
    public enum VisualCustomizationFields : byte
    {
        None = 0,
        MeshColor = 1,
        Historical = 2,
    }

    /// <summary>The three RGBA channels stored by the game's mesh-color components.</summary>
    public struct VisualColorSet : IEquatable<VisualColorSet>
    {
        public float R0, G0, B0, A0;
        public float R1, G1, B1, A1;
        public float R2, G2, B2, A2;

        internal void Write(NetworkWriter writer)
        {
            Validate();
            writer.WriteFloat(R0); writer.WriteFloat(G0); writer.WriteFloat(B0); writer.WriteFloat(A0);
            writer.WriteFloat(R1); writer.WriteFloat(G1); writer.WriteFloat(B1); writer.WriteFloat(A1);
            writer.WriteFloat(R2); writer.WriteFloat(G2); writer.WriteFloat(B2); writer.WriteFloat(A2);
        }

        internal static VisualColorSet Read(NetworkReader reader)
        {
            return new VisualColorSet
            {
                R0 = ReadChannel(reader), G0 = ReadChannel(reader),
                B0 = ReadChannel(reader), A0 = ReadChannel(reader),
                R1 = ReadChannel(reader), G1 = ReadChannel(reader),
                B1 = ReadChannel(reader), A1 = ReadChannel(reader),
                R2 = ReadChannel(reader), G2 = ReadChannel(reader),
                B2 = ReadChannel(reader), A2 = ReadChannel(reader),
            };
        }

        private static float ReadChannel(NetworkReader reader)
        {
            float value = WireGuard.ReadFinite(reader);
            if (value < 0f || value > 1f)
                throw new ProtocolException("Mesh-color channel outside the 0..1 range.");
            return value;
        }

        internal void Validate()
        {
            ValidateChannel(R0); ValidateChannel(G0); ValidateChannel(B0); ValidateChannel(A0);
            ValidateChannel(R1); ValidateChannel(G1); ValidateChannel(B1); ValidateChannel(A1);
            ValidateChannel(R2); ValidateChannel(G2); ValidateChannel(B2); ValidateChannel(A2);
        }

        private static void ValidateChannel(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) || value < 0f || value > 1f)
                throw new ProtocolException("Mesh-color channel outside the finite 0..1 range.");
        }

        public bool Equals(VisualColorSet other) =>
            R0 == other.R0 && G0 == other.G0 && B0 == other.B0 && A0 == other.A0 &&
            R1 == other.R1 && G1 == other.G1 && B1 == other.B1 && A1 == other.A1 &&
            R2 == other.R2 && G2 == other.G2 && B2 == other.B2 && A2 == other.A2;

        public override bool Equals(object obj) => obj is VisualColorSet other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = R0.GetHashCode();
                hash = hash * 397 ^ G0.GetHashCode();
                hash = hash * 397 ^ B0.GetHashCode();
                hash = hash * 397 ^ A0.GetHashCode();
                hash = hash * 397 ^ R1.GetHashCode();
                hash = hash * 397 ^ G1.GetHashCode();
                hash = hash * 397 ^ B1.GetHashCode();
                hash = hash * 397 ^ A1.GetHashCode();
                hash = hash * 397 ^ R2.GetHashCode();
                hash = hash * 397 ^ G2.GetHashCode();
                hash = hash * 397 ^ B2.GetHashCode();
                return hash * 397 ^ A2.GetHashCode();
            }
        }
    }

    /// <summary>
    /// A stable target hint plus spatial fallback. Entity ids make the common same-save
    /// path exact; prefab, seed, and position cover entities created independently.
    /// </summary>
    public struct VisualCustomizationTarget
    {
        public const int EncodedBytes = 24;

        public int EntityIndex;
        public int EntityVersion;
        public int RandomSeed;
        public float X, Y, Z;

        internal void Write(NetworkWriter writer)
        {
            Validate();
            writer.WriteInt(EntityIndex);
            writer.WriteInt(EntityVersion);
            writer.WriteInt(RandomSeed);
            writer.WriteFloat(X); writer.WriteFloat(Y); writer.WriteFloat(Z);
        }

        private void Validate()
        {
            if (EntityIndex < 0 || EntityVersion <= 0)
                throw new ProtocolException("Invalid visual-customization entity hint.");
            if (RandomSeed < -1 || RandomSeed > ushort.MaxValue)
                throw new ProtocolException("Invalid visual-customization random seed.");
            ValidateCoordinate(X); ValidateCoordinate(Y); ValidateCoordinate(Z);
        }

        private static void ValidateCoordinate(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) ||
                value < -WireGuard.MaxCoordinate || value > WireGuard.MaxCoordinate)
                throw new ProtocolException("Visual-customization coordinate outside plausible bounds.");
        }

        internal static VisualCustomizationTarget Read(NetworkReader reader)
        {
            int index = reader.ReadInt();
            int version = reader.ReadInt();
            int randomSeed = reader.ReadInt();
            if (index < 0 || version <= 0)
                throw new ProtocolException("Invalid visual-customization entity hint.");
            if (randomSeed < -1 || randomSeed > ushort.MaxValue)
                throw new ProtocolException("Invalid visual-customization random seed.");

            return new VisualCustomizationTarget
            {
                EntityIndex = index,
                EntityVersion = version,
                RandomSeed = randomSeed,
                X = WireGuard.ReadCoordinate(reader),
                Y = WireGuard.ReadCoordinate(reader),
                Z = WireGuard.ReadCoordinate(reader),
            };
        }
    }

    /// <summary>
    /// Full resulting visual state for one or more instances of the same prefab. A field
    /// mask keeps simultaneous color and historical edits independent.
    /// </summary>
    public sealed class VisualCustomizationCommand : ISimulationCommand
    {
        public const ushort Id = 22;
        public const int MaxTargets = 256;
        public const int MaxEncodedBytes = 8 * 1024;

        public string PrefabName;
        public VisualCustomizationFields Fields;
        public bool HasCustomColor;
        public VisualColorSet Color;
        public bool IsHistorical;
        public VisualCustomizationTarget[] Targets;

        public ushort CommandId => Id;

        public void Write(NetworkWriter writer)
        {
            ValidateName(PrefabName);
            if (Fields == VisualCustomizationFields.None ||
                (Fields & ~(VisualCustomizationFields.MeshColor |
                            VisualCustomizationFields.Historical)) != 0)
                throw new ProtocolException("Invalid visual-customization field mask.");
            int count = Targets != null ? Targets.Length : 0;
            if (count == 0 || count > MaxTargets)
                throw new ProtocolException("Invalid visual-customization target count.");

            writer.WriteString(PrefabName);
            writer.WriteByte((byte)Fields);
            if ((Fields & VisualCustomizationFields.MeshColor) != 0)
            {
                writer.WriteBool(HasCustomColor);
                if (HasCustomColor) Color.Write(writer);
            }
            if ((Fields & VisualCustomizationFields.Historical) != 0)
                writer.WriteBool(IsHistorical);

            writer.WriteShort((short)count);
            for (int i = 0; i < count; i++) Targets[i].Write(writer);
        }

        public void Read(NetworkReader reader)
        {
            PrefabName = WireGuard.ReadName(reader);
            Fields = (VisualCustomizationFields)reader.ReadByte();
            if (Fields == VisualCustomizationFields.None ||
                (Fields & ~(VisualCustomizationFields.MeshColor |
                            VisualCustomizationFields.Historical)) != 0)
                throw new ProtocolException("Invalid visual-customization field mask.");

            if ((Fields & VisualCustomizationFields.MeshColor) != 0)
            {
                HasCustomColor = ReadStrictBool(reader, "custom-color state");
                if (HasCustomColor) Color = VisualColorSet.Read(reader);
            }
            if ((Fields & VisualCustomizationFields.Historical) != 0)
                IsHistorical = ReadStrictBool(reader, "historical state");

            int count = WireGuard.ReadCount(reader, VisualCustomizationTarget.EncodedBytes, MaxTargets);
            if (count == 0)
                throw new ProtocolException("Visual-customization command has no targets.");
            Targets = new VisualCustomizationTarget[count];
            for (int i = 0; i < count; i++) Targets[i] = VisualCustomizationTarget.Read(reader);

            if (reader.Remaining != 0)
                throw new ProtocolException("Trailing bytes in visual-customization command.");
        }

        public byte[] Encode()
        {
            int count = Targets != null ? Targets.Length : 0;
            var writer = new NetworkWriter(64 + count * VisualCustomizationTarget.EncodedBytes);
            Write(writer);
            if (writer.Length > MaxEncodedBytes)
                throw new ProtocolException("Visual-customization command exceeds its size limit.");
            return writer.ToArray();
        }

        public static VisualCustomizationCommand Decode(byte[] body)
        {
            if (body == null || body.Length > MaxEncodedBytes)
                throw new ProtocolException("Visual-customization command exceeds its size limit.");
            var command = new VisualCustomizationCommand();
            command.Read(new NetworkReader(body));
            return command;
        }

        private static bool ReadStrictBool(NetworkReader reader, string name)
        {
            byte value = reader.ReadByte();
            if (value > 1) throw new ProtocolException("Invalid " + name + ".");
            return value != 0;
        }

        private static void ValidateName(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > WireGuard.MaxNameLength)
                throw new ProtocolException("Invalid visual-customization prefab name.");
            for (int i = 0; i < value.Length; i++)
                if (char.IsControl(value[i]))
                    throw new ProtocolException("Control character in visual-customization prefab name.");
        }
    }
}
