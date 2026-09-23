using System.Collections.Generic;
using CS2MultiplayerMod.Core.Protocol;

namespace CS2MultiplayerMod.Core.Sync.ModSync
{
    /// <summary>Reads a negotiated type table back by wire index.</summary>
    public interface IModTypeLookup
    {
        int Count { get; }
        ModTypeDescriptor ByIndex(int index);
    }

    /// <summary>One flattened field value. Which member carries it follows from the type's leaf kind.</summary>
    public struct ModLeaf
    {
        public long Integer;
        public double Real;
        public string Text;
        public ModEntityRef Reference;

        public static ModLeaf FromInteger(long value) => new ModLeaf { Integer = value };
        public static ModLeaf FromReal(double value) => new ModLeaf { Real = value };
        public static ModLeaf FromText(string value) => new ModLeaf { Text = value ?? string.Empty };
        public static ModLeaf FromReference(ModEntityRef value) => new ModLeaf { Reference = value };
    }

    /// <summary>The value of one replicated type on one entity: a component, a tag, or a whole buffer.</summary>
    public sealed class ModComponentValue
    {
        /// <summary>Index into the session's negotiated type table.</summary>
        public int TypeIndex;

        /// <summary>1 for a component, 0 for a tag, the element count for a buffer.</summary>
        public int ElementCount;

        /// <summary><see cref="ElementCount"/> x the type's leaf count, element by element.</summary>
        public ModLeaf[] Leaves;

        public ModComponentValue() { Leaves = new ModLeaf[0]; }
    }

    /// <summary>Everything one entity carries of the replicated types.</summary>
    public sealed class ModEntityValues
    {
        public readonly List<ModComponentValue> Components = new List<ModComponentValue>();
    }

    /// <summary>
    /// One carrier's complete replicated state as an idempotent transaction; mods rebuild their
    /// bookkeeping per edit, so diffs would not work. Empty means the state was removed.
    /// </summary>
    public sealed class ModStateSnapshot
    {
        /// <summary>Satellites in one transaction. Past this the carrier is skipped, not truncated.</summary>
        public const int MaxSatellites = 512;

        /// <summary>Replicated types on one entity.</summary>
        public const int MaxComponentsPerEntity = 64;

        /// <summary>Elements in one replicated buffer.</summary>
        public const int MaxBufferElements = 8192;

        /// <summary>Leaves in one transaction, across every entity in it.</summary>
        public const int MaxTotalLeaves = 262144;

        public ModEntityRef Carrier;
        public ModEntityValues CarrierValues = new ModEntityValues();
        public readonly List<ModEntityValues> Satellites = new List<ModEntityValues>();
        // A Road Speed reset removes CustomSpeed; the final lane value travels with the removal.
        public bool HasLaneSpeedReset;
        public float LaneSpeedReset;

        /// <summary>True when the carrier holds nothing replicated - the removal case.</summary>
        public bool IsEmpty => CarrierValues.Components.Count == 0 && Satellites.Count == 0;

        public void Write(NetworkWriter writer, IModTypeLookup types)
        {
            writer.WriteShort((short)Satellites.Count);
            Carrier.Write(writer);
            WriteEntity(writer, types, CarrierValues);
            for (int i = 0; i < Satellites.Count; i++) WriteEntity(writer, types, Satellites[i]);
            writer.WriteBool(HasLaneSpeedReset);
            if (HasLaneSpeedReset) writer.WriteFloat(LaneSpeedReset);
        }

        public static ModStateSnapshot Read(NetworkReader reader, IModTypeLookup types)
        {
            var snapshot = new ModStateSnapshot();
            int satellites = reader.ReadShort();
            if (satellites < 0 || satellites > MaxSatellites)
                throw new ProtocolException("Mod state transaction declares " + satellites + " satellites.");

            // Satellite count first, so every satellite reference is bounds-checked as read.
            snapshot.Carrier = ModEntityRef.Read(reader, satellites);

            int budget = MaxTotalLeaves;
            snapshot.CarrierValues = ReadEntity(reader, types, satellites, ref budget);
            for (int i = 0; i < satellites; i++)
                snapshot.Satellites.Add(ReadEntity(reader, types, satellites, ref budget));
            snapshot.HasLaneSpeedReset = reader.ReadBool();
            if (snapshot.HasLaneSpeedReset)
            {
                snapshot.LaneSpeedReset = reader.ReadFloat();
                if (float.IsNaN(snapshot.LaneSpeedReset) ||
                    float.IsInfinity(snapshot.LaneSpeedReset) ||
                    snapshot.LaneSpeedReset < 0.1f || snapshot.LaneSpeedReset > 500f)
                    throw new ProtocolException("Invalid restored road speed.");
            }
            return snapshot;
        }

        private static void WriteEntity(NetworkWriter writer, IModTypeLookup types, ModEntityValues values)
        {
            writer.WriteByte((byte)values.Components.Count);
            for (int i = 0; i < values.Components.Count; i++)
            {
                ModComponentValue component = values.Components[i];
                ModTypeDescriptor descriptor = types.ByIndex(component.TypeIndex);
                writer.WriteShort((short)component.TypeIndex);
                writer.WriteInt(component.ElementCount);

                int leafCount = descriptor.LeafCount;
                for (int leaf = 0; leaf < component.Leaves.Length; leaf++)
                    WriteLeaf(writer, descriptor.Leaves[leaf % leafCount], component.Leaves[leaf]);
            }
        }

        private static ModEntityValues ReadEntity(NetworkReader reader, IModTypeLookup types,
            int satellites, ref int budget)
        {
            var values = new ModEntityValues();
            int count = reader.ReadByte();
            if (count > MaxComponentsPerEntity)
                throw new ProtocolException("Mod state entity declares " + count + " components.");

            for (int i = 0; i < count; i++)
            {
                int typeIndex = reader.ReadShort();
                if (typeIndex < 0 || typeIndex >= types.Count)
                    throw new ProtocolException("Mod state names type " + typeIndex + " of " + types.Count + ".");

                ModTypeDescriptor descriptor = types.ByIndex(typeIndex);
                int elements = reader.ReadInt();
                if (elements < 0 || elements > MaxBufferElements)
                    throw new ProtocolException("Mod state type " + descriptor.DisplayName +
                        " declares " + elements + " elements.");
                if (descriptor.Kind != ModTypeKind.Buffer && elements > 1)
                    throw new ProtocolException("Mod state type " + descriptor.DisplayName +
                        " is not a buffer but declares " + elements + " elements.");

                int total = elements * descriptor.LeafCount;
                if (total > budget)
                    throw new ProtocolException("Mod state transaction exceeds its leaf budget.");
                budget -= total;

                var component = new ModComponentValue
                {
                    TypeIndex = typeIndex,
                    ElementCount = elements,
                    Leaves = new ModLeaf[total],
                };
                for (int leaf = 0; leaf < total; leaf++)
                    component.Leaves[leaf] =
                        ReadLeaf(reader, descriptor.Leaves[leaf % descriptor.LeafCount], satellites);
                values.Components.Add(component);
            }
            return values;
        }

        private static void WriteLeaf(NetworkWriter writer, ModValueKind kind, ModLeaf leaf)
        {
            switch (kind)
            {
                case ModValueKind.Bool: writer.WriteBool(leaf.Integer != 0); break;
                case ModValueKind.I8: writer.WriteByte(unchecked((byte)(sbyte)leaf.Integer)); break;
                case ModValueKind.U8: writer.WriteByte(unchecked((byte)leaf.Integer)); break;
                case ModValueKind.I16:
                case ModValueKind.U16: writer.WriteShort(unchecked((short)leaf.Integer)); break;
                case ModValueKind.I32:
                case ModValueKind.U32: writer.WriteInt(unchecked((int)leaf.Integer)); break;
                case ModValueKind.I64:
                case ModValueKind.U64: writer.WriteLong(leaf.Integer); break;
                case ModValueKind.F32: writer.WriteFloat((float)leaf.Real); break;
                case ModValueKind.F64: writer.WriteLong(System.BitConverter.DoubleToInt64Bits(leaf.Real)); break;
                case ModValueKind.Text: writer.WriteString(leaf.Text ?? string.Empty); break;
                case ModValueKind.EntityRef: leaf.Reference.Write(writer); break;
                default: throw new ProtocolException("Mod state leaf has unknown kind " + (byte)kind + ".");
            }
        }

        private static ModLeaf ReadLeaf(NetworkReader reader, ModValueKind kind, int satellites)
        {
            switch (kind)
            {
                case ModValueKind.Bool: return ModLeaf.FromInteger(reader.ReadBool() ? 1 : 0);
                case ModValueKind.I8: return ModLeaf.FromInteger((sbyte)reader.ReadByte());
                case ModValueKind.U8: return ModLeaf.FromInteger(reader.ReadByte());
                case ModValueKind.I16: return ModLeaf.FromInteger(reader.ReadShort());
                case ModValueKind.U16: return ModLeaf.FromInteger((ushort)reader.ReadShort());
                case ModValueKind.I32: return ModLeaf.FromInteger(reader.ReadInt());
                case ModValueKind.U32: return ModLeaf.FromInteger((uint)reader.ReadInt());
                case ModValueKind.I64:
                case ModValueKind.U64: return ModLeaf.FromInteger(reader.ReadLong());
                case ModValueKind.F32: return ModLeaf.FromReal(reader.ReadFloat());
                case ModValueKind.F64:
                    return ModLeaf.FromReal(System.BitConverter.Int64BitsToDouble(reader.ReadLong()));
                case ModValueKind.Text: return ModLeaf.FromText(reader.ReadString());
                case ModValueKind.EntityRef:
                    return ModLeaf.FromReference(ModEntityRef.Read(reader, satellites));
                default: throw new ProtocolException("Mod state leaf has unknown kind " + (byte)kind + ".");
            }
        }
    }
}
