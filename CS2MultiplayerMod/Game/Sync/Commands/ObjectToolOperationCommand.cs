using System;
using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Sync;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>The native definition payload carried by one object-tool transaction.</summary>
    public enum ObjectToolDefinitionKind : byte
    {
        Object = 1,
        NetCourse = 2,
        Area = 3,
    }

    /// <summary>A portable identity for an entity referenced by a native tool definition.</summary>
    public enum PortableEntityKind : byte
    {
        None = 0,
        Object = 1,
        NetNode = 2,
        NetEdge = 3,
        Area = 4,
    }

    /// <summary>The owning buffer used for one stable step below a top-level object.</summary>
    public enum PortableOwnerPathKind : byte
    {
        InstalledUpgrade = 1,
        SubObject = 2,
        SubNet = 3,
        SubArea = 4,
    }

    /// <summary>
    /// One owner-relative identity step. The sender's buffer index records the exact source slot;
    /// prefab, entity kind, and same-prefab ordinal provide stable receiver-side lookup when
    /// unrelated buffer entries differ.
    /// </summary>
    public struct PortableOwnerPathStep
    {
        public PortableOwnerPathKind BufferKind;
        public PortableEntityKind EntityKind;
        public string PrefabName;
        public int BufferIndex;
        public int PrefabOrdinal;
    }

    /// <summary>
    /// Stable identity for an object, network element, or area. Entity indices never cross the
    /// wire; owned network elements additionally carry the top-level owner's identity and layer
    /// contract so a nearby incompatible connector cannot be selected.
    /// </summary>
    public struct PortableEntityRef
    {
        public PortableEntityKind Kind;
        public string PrefabName;
        public float PosX, PosY, PosZ;
        public float RotX, RotY, RotZ, RotW;
        public float Ax, Ay, Az, Bx, By, Bz, Cx, Cy, Cz, Dx, Dy, Dz;
        public string OwnerPrefabName;
        public float OwnerX, OwnerY, OwnerZ;
        public float OwnerRotX, OwnerRotY, OwnerRotZ, OwnerRotW;
        public uint RequiredLayers;
        public uint ConnectLayers;
        public PortableOwnerPathStep[] OwnerPath;
    }

    public struct ObjectDefinitionIntent
    {
        public float PosX, PosY, PosZ;
        public float LocalX, LocalY, LocalZ;
        public float ScaleX, ScaleY, ScaleZ;
        public float RotX, RotY, RotZ, RotW;
        public float LocalRotX, LocalRotY, LocalRotZ, LocalRotW;
        public float Elevation;
        public float Intensity;
        public float Age;
        public bool IsDecoration;
        public int ParentMesh;
        public int GroupIndex;
        public int Probability;
        public int PrefabSubIndex;
    }

    public struct ObjectCoursePositionIntent
    {
        public PortableEntityRef Entity;
        public float PosX, PosY, PosZ;
        public float RotX, RotY, RotZ, RotW;
        public float ElevationLeft, ElevationRight;
        public float CourseDelta;
        public float SplitPosition;
        public uint Flags;
        public int ParentMesh;
    }

    public struct ObjectNetCourseIntent
    {
        public ObjectCoursePositionIntent Start;
        public ObjectCoursePositionIntent End;
        public float Ax, Ay, Az, Bx, By, Bz, Cx, Cy, Cz, Dx, Dy, Dz;
        public float ElevationLeft, ElevationRight;
        public float Length;
        public int FixedIndex;
    }

    public struct ObjectAreaNodeIntent
    {
        public float X, Y, Z;
        public float Elevation;
    }

    /// <summary>One indexed definition from an object tool's indivisible native output batch.</summary>
    public sealed class ObjectToolDefinitionIntent
    {
        public ObjectToolDefinitionKind Kind;
        public bool PrefabIsNull;
        public string PrefabName;
        public string SubPrefabName;
        public PortableEntityRef Original;
        public PortableEntityRef Owner;
        public PortableEntityRef Attached;
        /// <summary>
        /// A prefab-local attachment target used by placeholder-building definitions. Unlike
        /// <see cref="Attached"/>, this names a prefab entity from the same native placement graph,
        /// not an already-existing simulation entity.
        /// </summary>
        public string AttachedPrefabName;
        public uint CreationFlags;
        public int RandomSeed;

        public bool HasOwnerDefinition;
        public string OwnerDefinitionPrefabName;
        public float OwnerDefinitionX, OwnerDefinitionY, OwnerDefinitionZ;
        public float OwnerDefinitionRotX, OwnerDefinitionRotY;
        public float OwnerDefinitionRotZ, OwnerDefinitionRotW;

        public ObjectDefinitionIntent Object;
        public ObjectNetCourseIntent NetCourse;
        public ObjectAreaNodeIntent[] AreaNodes;

        public bool HasUpgraded;
        public uint UpgradeGeneral, UpgradeLeft, UpgradeRight;
    }

    /// <summary>
    /// Exact, portable output of one native object-tool Apply. All definitions are encoded in one
    /// command, preserving source order and preventing a building, relocation, service extension,
    /// driveway, utility connector, or lot area from being received as a partial prefix.
    /// </summary>
    public sealed class ObjectToolOperationCommand : ISimulationCommand
    {
        public const ushort Id = 20;
        /// <summary>
        /// Asset stamps deliberately have no persistent root object definition. Their prefab emits
        /// its complete transformed subnet/subobject/area graph directly in one tool transaction.
        /// </summary>
        public const short AssetStampRootIndex = -1;
        public const int MaxDefinitions = 1024;
        public const int MaxAreaNodesPerDefinition = 1024;
        public const int MaxOwnerPathDepth = 32;
        public const int MaxOwnerBufferIndex = 32767;
        public const int MaxEncodedBytes = 256 * 1024;

        private const uint KnownCreationFlags = 0xfffffu;
        private const uint KnownCoursePosFlags = 0x7fffu;
        private const uint StampingCreationFlag = 0x80000u;
        private const uint AttachCreationFlag = 0x8u;
        private const uint UnsafeAssetStampCreationFlags = 0x60835u;

        public long OperationId;
        public short RootIndex;
        /// <summary>
        /// The compact input for a rooted object placement. The exact definition graph remains in
        /// <see cref="Definitions"/> as a compatibility fallback, while receivers that can reach
        /// the native generator rebuild ordinary and specialized-industry buildings against their
        /// own road subdivision.
        /// </summary>
        public bool HasPlacementInput;
        public uint ToolRandomSeed;
        public PortableEntityRef PlacementTarget;
        public string AssetStampPrefabName;
        public ObjectToolDefinitionIntent[] Definitions;

        public ushort CommandId => Id;
        public bool IsAssetStamp => RootIndex == AssetStampRootIndex;

        public void Write(NetworkWriter writer)
        {
            if (OperationId <= 0)
                throw new ProtocolException("Invalid object-tool operation id " + OperationId + ".");
            ValidateEnvelope();

            writer.WriteLong(OperationId);
            writer.WriteShort(RootIndex);
            writer.WriteShort((short)Definitions.Length);
            writer.WriteBool(HasPlacementInput);
            if (HasPlacementInput)
            {
                writer.WriteInt(unchecked((int)ToolRandomSeed));
                WriteEntityRef(writer, PlacementTarget);
            }
            if (IsAssetStamp) writer.WriteString(AssetStampPrefabName);
            for (int i = 0; i < Definitions.Length; i++) WriteDefinition(writer, Definitions[i]);
        }

        public void Read(NetworkReader reader)
        {
            OperationId = reader.ReadLong();
            if (OperationId <= 0)
                throw new ProtocolException("Invalid object-tool operation id " + OperationId + ".");
            RootIndex = reader.ReadShort();
            int count = WireGuard.ReadCount(reader, 2, MaxDefinitions);
            if (count == 0 || RootIndex < AssetStampRootIndex || RootIndex >= count)
                throw new ProtocolException("Invalid object-tool root/count " + RootIndex + "/" + count + ".");
            HasPlacementInput = reader.ReadBool();
            if (HasPlacementInput)
            {
                ToolRandomSeed = unchecked((uint)reader.ReadInt());
                PlacementTarget = ReadEntityRef(reader);
            }
            if (IsAssetStamp) AssetStampPrefabName = WireGuard.ReadName(reader);

            Definitions = new ObjectToolDefinitionIntent[count];
            for (int i = 0; i < count; i++) Definitions[i] = ReadDefinition(reader);
            ValidateEnvelope();
            if (reader.Remaining != 0)
                throw new ProtocolException("Trailing bytes in object-tool operation: " + reader.Remaining + ".");
        }

        private void ValidateEnvelope()
        {
            if (Definitions == null || Definitions.Length == 0 || Definitions.Length > MaxDefinitions)
                throw new ProtocolException("Invalid object-tool definition count.");

            if (!IsAssetStamp)
            {
                if (RootIndex < 0 || RootIndex >= Definitions.Length)
                    throw new ProtocolException("Invalid object-tool root index " + RootIndex + ".");
                if (Definitions[RootIndex] == null ||
                    Definitions[RootIndex].Kind != ObjectToolDefinitionKind.Object)
                    throw new ProtocolException("Object-tool root must be an object definition.");
                if (!string.IsNullOrEmpty(AssetStampPrefabName))
                    throw new ProtocolException("A rooted object operation may not name an asset stamp.");
                if (HasPlacementInput)
                {
                    ObjectToolDefinitionIntent root = Definitions[RootIndex];
                    // The game's tool seed is opaque; zero is a valid 32-bit generator input.
                    if (root.PrefabIsNull ||
                        string.IsNullOrEmpty(root.PrefabName) ||
                        root.Original.Kind != PortableEntityKind.None ||
                        root.Owner.Kind != PortableEntityKind.None ||
                        root.HasOwnerDefinition)
                        throw new ProtocolException(
                            "Compact placement input requires one new named top-level object.");
                }
                return;
            }

            if (HasPlacementInput)
                throw new ProtocolException("A rootless asset stamp may not carry placement input.");
            if (string.IsNullOrEmpty(AssetStampPrefabName))
                throw new ProtocolException("A rootless asset-stamp operation has no stamp prefab.");

            bool hasStampingNet = false;
            for (int i = 0; i < Definitions.Length; i++)
            {
                ObjectToolDefinitionIntent definition = Definitions[i];
                if (definition == null || definition.PrefabIsNull ||
                    string.IsNullOrEmpty(definition.PrefabName))
                    throw new ProtocolException("Asset-stamp definitions must create named prefabs.");
                if (definition.Original.Kind != PortableEntityKind.None ||
                    definition.Owner.Kind != PortableEntityKind.None ||
                    definition.Attached.Kind != PortableEntityKind.None ||
                    !string.IsNullOrEmpty(definition.AttachedPrefabName) ||
                    definition.HasOwnerDefinition)
                    throw new ProtocolException("A rootless asset stamp may not reference an external owner or original.");
                if ((definition.CreationFlags & ~KnownCreationFlags) != 0 ||
                    (definition.CreationFlags & UnsafeAssetStampCreationFlags) != 0)
                    throw new ProtocolException("Unsafe asset-stamp creation flags 0x" +
                                                definition.CreationFlags.ToString("x") + ".");

                if (definition.Kind != ObjectToolDefinitionKind.NetCourse) continue;
                if ((definition.CreationFlags & StampingCreationFlag) == 0 ||
                    definition.NetCourse.Start.Entity.Kind != PortableEntityKind.None ||
                    definition.NetCourse.End.Entity.Kind != PortableEntityKind.None)
                    throw new ProtocolException("Asset-stamp net courses must be new stamping courses.");
                hasStampingNet = true;
            }

            if (!hasStampingNet)
                throw new ProtocolException("A rootless asset stamp has no stamping net course.");
        }

        public byte[] Encode()
        {
            var writer = new NetworkWriter(1024);
            Write(writer);
            if (writer.Length > MaxEncodedBytes)
                throw new ProtocolException("Object-tool operation body " + writer.Length +
                                            " exceeds the " + MaxEncodedBytes + "-byte cap.");
            return writer.ToArray();
        }

        public static ObjectToolOperationCommand Decode(byte[] body)
        {
            if (body == null) throw new ProtocolException("Null object-tool operation body.");
            if (body.Length > MaxEncodedBytes)
                throw new ProtocolException("Object-tool operation body " + body.Length +
                                            " exceeds the " + MaxEncodedBytes + "-byte cap.");
            var command = new ObjectToolOperationCommand();
            command.Read(new NetworkReader(body));
            return command;
        }

        private static void WriteDefinition(NetworkWriter writer, ObjectToolDefinitionIntent value)
        {
            if (value == null) throw new ProtocolException("Null object-tool definition.");
            ValidatePrefabAttachment(value);
            writer.WriteByte((byte)value.Kind);
            writer.WriteBool(value.PrefabIsNull);
            if (!value.PrefabIsNull) writer.WriteString(value.PrefabName);
            WriteOptionalName(writer, value.SubPrefabName);
            WriteEntityRef(writer, value.Original);
            WriteEntityRef(writer, value.Owner);
            WriteEntityRef(writer, value.Attached);
            WriteOptionalName(writer, value.AttachedPrefabName);
            writer.WriteInt(unchecked((int)value.CreationFlags));
            writer.WriteInt(value.RandomSeed);

            writer.WriteBool(value.HasOwnerDefinition);
            if (value.HasOwnerDefinition)
            {
                writer.WriteString(value.OwnerDefinitionPrefabName);
                WriteFloat3(writer, value.OwnerDefinitionX, value.OwnerDefinitionY, value.OwnerDefinitionZ);
                WriteQuaternion(writer, value.OwnerDefinitionRotX, value.OwnerDefinitionRotY,
                    value.OwnerDefinitionRotZ, value.OwnerDefinitionRotW);
            }

            switch (value.Kind)
            {
                case ObjectToolDefinitionKind.Object:
                    WriteObject(writer, value.Object);
                    break;
                case ObjectToolDefinitionKind.NetCourse:
                    WriteNetCourse(writer, value.NetCourse);
                    break;
                case ObjectToolDefinitionKind.Area:
                    ObjectAreaNodeIntent[] nodes = value.AreaNodes ?? new ObjectAreaNodeIntent[0];
                    if (nodes.Length == 0 || nodes.Length > MaxAreaNodesPerDefinition)
                        throw new ProtocolException("Invalid object-tool area node count.");
                    writer.WriteShort((short)nodes.Length);
                    for (int i = 0; i < nodes.Length; i++)
                    {
                        WriteFloat3(writer, nodes[i].X, nodes[i].Y, nodes[i].Z);
                        writer.WriteFloat(nodes[i].Elevation);
                    }
                    break;
                default:
                    throw new ProtocolException("Unknown object-tool definition kind " + (byte)value.Kind + ".");
            }

            writer.WriteBool(value.HasUpgraded);
            if (value.HasUpgraded)
            {
                writer.WriteInt(unchecked((int)value.UpgradeGeneral));
                writer.WriteInt(unchecked((int)value.UpgradeLeft));
                writer.WriteInt(unchecked((int)value.UpgradeRight));
            }
        }

        private static ObjectToolDefinitionIntent ReadDefinition(NetworkReader reader)
        {
            var value = new ObjectToolDefinitionIntent
            {
                Kind = (ObjectToolDefinitionKind)reader.ReadByte(),
            };
            if (value.Kind < ObjectToolDefinitionKind.Object || value.Kind > ObjectToolDefinitionKind.Area)
                throw new ProtocolException("Unknown object-tool definition kind " + (byte)value.Kind + ".");
            value.PrefabIsNull = reader.ReadBool();
            if (!value.PrefabIsNull) value.PrefabName = WireGuard.ReadName(reader);
            value.SubPrefabName = ReadOptionalName(reader);
            value.Original = ReadEntityRef(reader);
            value.Owner = ReadEntityRef(reader);
            value.Attached = ReadEntityRef(reader);
            value.AttachedPrefabName = ReadOptionalName(reader);
            if (value.PrefabIsNull && value.Original.Kind == PortableEntityKind.None)
                throw new ProtocolException("Null-prefab object definition has no original.");
            value.CreationFlags = unchecked((uint)reader.ReadInt());
            value.RandomSeed = reader.ReadInt();
            if ((value.CreationFlags & ~KnownCreationFlags) != 0)
                throw new ProtocolException("Unknown object creation flags 0x" +
                                            value.CreationFlags.ToString("x") + ".");
            ValidatePrefabAttachment(value);

            value.HasOwnerDefinition = reader.ReadBool();
            if (value.HasOwnerDefinition)
            {
                value.OwnerDefinitionPrefabName = WireGuard.ReadName(reader);
                ReadFloat3(reader, out value.OwnerDefinitionX, out value.OwnerDefinitionY,
                    out value.OwnerDefinitionZ);
                ReadQuaternion(reader, out value.OwnerDefinitionRotX, out value.OwnerDefinitionRotY,
                    out value.OwnerDefinitionRotZ, out value.OwnerDefinitionRotW, "owner definition");
            }

            switch (value.Kind)
            {
                case ObjectToolDefinitionKind.Object:
                    value.Object = ReadObject(reader);
                    break;
                case ObjectToolDefinitionKind.NetCourse:
                    value.NetCourse = ReadNetCourse(reader);
                    break;
                case ObjectToolDefinitionKind.Area:
                    int count = WireGuard.ReadCount(reader, 16, MaxAreaNodesPerDefinition);
                    if (count == 0) throw new ProtocolException("Object-tool area has no nodes.");
                    value.AreaNodes = new ObjectAreaNodeIntent[count];
                    for (int i = 0; i < count; i++)
                    {
                        ReadFloat3(reader, out value.AreaNodes[i].X, out value.AreaNodes[i].Y,
                            out value.AreaNodes[i].Z);
                        value.AreaNodes[i].Elevation = WireGuard.ReadFinite(reader);
                        if (value.AreaNodes[i].Elevation != float.MinValue &&
                            (value.AreaNodes[i].Elevation < -100000f ||
                             value.AreaNodes[i].Elevation > 100000f))
                            throw new ProtocolException("Implausible area elevation " +
                                value.AreaNodes[i].Elevation + ".");
                    }
                    break;
            }

            value.HasUpgraded = reader.ReadBool();
            if (value.HasUpgraded)
            {
                value.UpgradeGeneral = unchecked((uint)reader.ReadInt());
                value.UpgradeLeft = unchecked((uint)reader.ReadInt());
                value.UpgradeRight = unchecked((uint)reader.ReadInt());
            }
            return value;
        }

        private static void ValidatePrefabAttachment(ObjectToolDefinitionIntent value)
        {
            if (string.IsNullOrEmpty(value.AttachedPrefabName)) return;
            if (value.Kind != ObjectToolDefinitionKind.Object || value.PrefabIsNull ||
                value.Attached.Kind != PortableEntityKind.None ||
                (value.CreationFlags & AttachCreationFlag) == 0)
                throw new ProtocolException(
                    "A prefab-local attachment must be an attached object definition.");
        }

        private static void WriteObject(NetworkWriter writer, ObjectDefinitionIntent value)
        {
            WriteFloat3(writer, value.PosX, value.PosY, value.PosZ);
            WriteFloat3(writer, value.LocalX, value.LocalY, value.LocalZ);
            WriteFloat3(writer, value.ScaleX, value.ScaleY, value.ScaleZ);
            WriteQuaternion(writer, value.RotX, value.RotY, value.RotZ, value.RotW);
            WriteQuaternion(writer, value.LocalRotX, value.LocalRotY, value.LocalRotZ, value.LocalRotW);
            writer.WriteFloat(value.Elevation);
            writer.WriteFloat(value.Intensity);
            writer.WriteFloat(value.Age);
            writer.WriteBool(value.IsDecoration);
            writer.WriteInt(value.ParentMesh);
            writer.WriteInt(value.GroupIndex);
            writer.WriteInt(value.Probability);
            writer.WriteInt(value.PrefabSubIndex);
        }

        private static ObjectDefinitionIntent ReadObject(NetworkReader reader)
        {
            var value = new ObjectDefinitionIntent();
            ReadFloat3(reader, out value.PosX, out value.PosY, out value.PosZ);
            ReadFloat3(reader, out value.LocalX, out value.LocalY, out value.LocalZ);
            value.ScaleX = ReadBounded(reader, -10000f, 10000f, "object scale");
            value.ScaleY = ReadBounded(reader, -10000f, 10000f, "object scale");
            value.ScaleZ = ReadBounded(reader, -10000f, 10000f, "object scale");
            ReadQuaternion(reader, out value.RotX, out value.RotY, out value.RotZ, out value.RotW,
                "object rotation", true);
            ReadQuaternion(reader, out value.LocalRotX, out value.LocalRotY, out value.LocalRotZ,
                out value.LocalRotW, "object local rotation", true);
            value.Elevation = ReadBounded(reader, -100000f, 100000f, "object elevation");
            value.Intensity = ReadBounded(reader, -100000f, 100000f, "object intensity");
            value.Age = ReadBounded(reader, -1000f, 1000f, "object age");
            value.IsDecoration = reader.ReadBool();
            value.ParentMesh = ReadIndex(reader, "object parent mesh");
            value.GroupIndex = ReadIndex(reader, "object group index");
            value.Probability = reader.ReadInt();
            if (value.Probability < -1 || value.Probability > 1000000)
                throw new ProtocolException("Implausible object probability " + value.Probability + ".");
            value.PrefabSubIndex = ReadIndex(reader, "object prefab sub-index");
            return value;
        }

        private static void WriteNetCourse(NetworkWriter writer, ObjectNetCourseIntent value)
        {
            WriteCoursePosition(writer, value.Start);
            WriteCoursePosition(writer, value.End);
            WriteCurve(writer, value.Ax, value.Ay, value.Az, value.Bx, value.By, value.Bz,
                value.Cx, value.Cy, value.Cz, value.Dx, value.Dy, value.Dz);
            writer.WriteFloat(value.ElevationLeft);
            writer.WriteFloat(value.ElevationRight);
            writer.WriteFloat(value.Length);
            writer.WriteInt(value.FixedIndex);
        }

        private static ObjectNetCourseIntent ReadNetCourse(NetworkReader reader)
        {
            var value = new ObjectNetCourseIntent
            {
                Start = ReadCoursePosition(reader),
                End = ReadCoursePosition(reader),
            };
            ReadCurve(reader, out value.Ax, out value.Ay, out value.Az,
                out value.Bx, out value.By, out value.Bz, out value.Cx, out value.Cy,
                out value.Cz, out value.Dx, out value.Dy, out value.Dz);
            value.ElevationLeft = ReadBounded(reader, -100000f, 100000f, "course elevation");
            value.ElevationRight = ReadBounded(reader, -100000f, 100000f, "course elevation");
            value.Length = ReadBounded(reader, 0f, WireGuard.MaxCoordinate, "course length");
            value.FixedIndex = ReadIndex(reader, "fixed-net index");
            return value;
        }

        private static void WriteCoursePosition(NetworkWriter writer, ObjectCoursePositionIntent value)
        {
            WriteEntityRef(writer, value.Entity);
            WriteFloat3(writer, value.PosX, value.PosY, value.PosZ);
            WriteQuaternion(writer, value.RotX, value.RotY, value.RotZ, value.RotW);
            writer.WriteFloat(value.ElevationLeft);
            writer.WriteFloat(value.ElevationRight);
            writer.WriteFloat(value.CourseDelta);
            writer.WriteFloat(value.SplitPosition);
            writer.WriteInt(unchecked((int)value.Flags));
            writer.WriteInt(value.ParentMesh);
        }

        private static ObjectCoursePositionIntent ReadCoursePosition(NetworkReader reader)
        {
            var value = new ObjectCoursePositionIntent { Entity = ReadEntityRef(reader) };
            ReadFloat3(reader, out value.PosX, out value.PosY, out value.PosZ);
            ReadQuaternion(reader, out value.RotX, out value.RotY, out value.RotZ, out value.RotW,
                "course rotation");
            value.ElevationLeft = ReadBounded(reader, -100000f, 100000f, "endpoint elevation");
            value.ElevationRight = ReadBounded(reader, -100000f, 100000f, "endpoint elevation");
            value.CourseDelta = ReadBounded(reader, -2f, 3f, "course delta");
            value.SplitPosition = ReadBounded(reader, -2f, 3f, "split position");
            value.Flags = unchecked((uint)reader.ReadInt());
            if ((value.Flags & ~KnownCoursePosFlags) != 0)
                throw new ProtocolException("Unknown object course-position flags 0x" +
                                            value.Flags.ToString("x") + ".");
            value.ParentMesh = ReadIndex(reader, "course parent mesh");
            return value;
        }

        private static void WriteEntityRef(NetworkWriter writer, PortableEntityRef value)
        {
            writer.WriteByte((byte)value.Kind);
            if (value.Kind == PortableEntityKind.None) return;
            writer.WriteString(value.PrefabName);
            WriteFloat3(writer, value.PosX, value.PosY, value.PosZ);
            WriteQuaternion(writer, value.RotX, value.RotY, value.RotZ, value.RotW);
            if (value.Kind == PortableEntityKind.NetEdge)
                WriteCurve(writer, value.Ax, value.Ay, value.Az, value.Bx, value.By, value.Bz,
                    value.Cx, value.Cy, value.Cz, value.Dx, value.Dy, value.Dz);
            bool hasOwner = !string.IsNullOrEmpty(value.OwnerPrefabName);
            writer.WriteBool(hasOwner);
            if (hasOwner)
            {
                writer.WriteString(value.OwnerPrefabName);
                WriteFloat3(writer, value.OwnerX, value.OwnerY, value.OwnerZ);
                WriteQuaternion(writer, value.OwnerRotX, value.OwnerRotY,
                    value.OwnerRotZ, value.OwnerRotW);
            }
            PortableOwnerPathStep[] ownerPath =
                value.OwnerPath ?? new PortableOwnerPathStep[0];
            if (ownerPath.Length > MaxOwnerPathDepth)
                throw new ProtocolException("Portable owner path is too deep.");
            if (ownerPath.Length != 0 && !hasOwner)
                throw new ProtocolException("Portable owner path has no top-level owner.");
            writer.WriteByte((byte)ownerPath.Length);
            for (int i = 0; i < ownerPath.Length; i++)
            {
                PortableOwnerPathStep step = ownerPath[i];
                ValidateOwnerPathStep(step);
                writer.WriteByte((byte)step.BufferKind);
                writer.WriteByte((byte)step.EntityKind);
                writer.WriteString(step.PrefabName);
                writer.WriteShort((short)step.BufferIndex);
                writer.WriteShort((short)step.PrefabOrdinal);
            }
            writer.WriteInt(unchecked((int)value.RequiredLayers));
            writer.WriteInt(unchecked((int)value.ConnectLayers));
        }

        private static PortableEntityRef ReadEntityRef(NetworkReader reader)
        {
            var value = new PortableEntityRef { Kind = (PortableEntityKind)reader.ReadByte() };
            if (value.Kind < PortableEntityKind.None || value.Kind > PortableEntityKind.Area)
                throw new ProtocolException("Unknown portable entity kind " + (byte)value.Kind + ".");
            if (value.Kind == PortableEntityKind.None) return value;
            value.PrefabName = WireGuard.ReadName(reader);
            ReadFloat3(reader, out value.PosX, out value.PosY, out value.PosZ);
            ReadQuaternion(reader, out value.RotX, out value.RotY, out value.RotZ, out value.RotW,
                "portable entity rotation");
            if (value.Kind == PortableEntityKind.NetEdge)
                ReadCurve(reader, out value.Ax, out value.Ay, out value.Az,
                    out value.Bx, out value.By, out value.Bz, out value.Cx, out value.Cy,
                    out value.Cz, out value.Dx, out value.Dy, out value.Dz);
            if (reader.ReadBool())
            {
                value.OwnerPrefabName = WireGuard.ReadName(reader);
                ReadFloat3(reader, out value.OwnerX, out value.OwnerY, out value.OwnerZ);
                ReadQuaternion(reader, out value.OwnerRotX, out value.OwnerRotY,
                    out value.OwnerRotZ, out value.OwnerRotW, "portable owner rotation");
            }
            int pathCount = reader.ReadByte();
            if (pathCount > MaxOwnerPathDepth)
                throw new ProtocolException("Portable owner path is too deep.");
            if (pathCount != 0 && string.IsNullOrEmpty(value.OwnerPrefabName))
                throw new ProtocolException("Portable owner path has no top-level owner.");
            if (pathCount != 0)
            {
                value.OwnerPath = new PortableOwnerPathStep[pathCount];
                for (int i = 0; i < pathCount; i++)
                {
                    var step = new PortableOwnerPathStep
                    {
                        BufferKind = (PortableOwnerPathKind)reader.ReadByte(),
                        EntityKind = (PortableEntityKind)reader.ReadByte(),
                        PrefabName = WireGuard.ReadName(reader),
                        BufferIndex = reader.ReadShort(),
                        PrefabOrdinal = reader.ReadShort(),
                    };
                    ValidateOwnerPathStep(step);
                    value.OwnerPath[i] = step;
                }
            }
            value.RequiredLayers = unchecked((uint)reader.ReadInt());
            value.ConnectLayers = unchecked((uint)reader.ReadInt());
            return value;
        }

        private static void ValidateOwnerPathStep(PortableOwnerPathStep step)
        {
            if (step.BufferKind < PortableOwnerPathKind.InstalledUpgrade ||
                step.BufferKind > PortableOwnerPathKind.SubArea)
                throw new ProtocolException("Unknown portable owner buffer kind " +
                                            (byte)step.BufferKind + ".");
            if (step.EntityKind < PortableEntityKind.Object ||
                step.EntityKind > PortableEntityKind.Area)
                throw new ProtocolException("Unknown portable path entity kind " +
                                            (byte)step.EntityKind + ".");
            if (string.IsNullOrEmpty(step.PrefabName))
                throw new ProtocolException("Portable owner path step has no prefab.");
            if (step.BufferIndex < 0 || step.BufferIndex > MaxOwnerBufferIndex ||
                step.PrefabOrdinal < 0 || step.PrefabOrdinal > MaxOwnerBufferIndex)
                throw new ProtocolException("Portable owner path index is out of range.");
        }

        private static void WriteOptionalName(NetworkWriter writer, string value)
        {
            writer.WriteBool(!string.IsNullOrEmpty(value));
            if (!string.IsNullOrEmpty(value)) writer.WriteString(value);
        }

        private static string ReadOptionalName(NetworkReader reader) =>
            reader.ReadBool() ? WireGuard.ReadName(reader) : null;

        private static void WriteFloat3(NetworkWriter writer, float x, float y, float z)
        {
            writer.WriteFloat(x); writer.WriteFloat(y); writer.WriteFloat(z);
        }

        private static void ReadFloat3(NetworkReader reader, out float x, out float y, out float z)
        {
            x = WireGuard.ReadCoordinate(reader);
            y = WireGuard.ReadCoordinate(reader);
            z = WireGuard.ReadCoordinate(reader);
        }

        private static void WriteQuaternion(NetworkWriter writer, float x, float y, float z, float w)
        {
            writer.WriteFloat(x); writer.WriteFloat(y); writer.WriteFloat(z); writer.WriteFloat(w);
        }

        private static void ReadQuaternion(NetworkReader reader, out float x, out float y,
            out float z, out float w, string name, bool allowZero = false)
        {
            x = ReadBounded(reader, -2f, 2f, name);
            y = ReadBounded(reader, -2f, 2f, name);
            z = ReadBounded(reader, -2f, 2f, name);
            w = ReadBounded(reader, -2f, 2f, name);
            float lengthSq = x * x + y * y + z * z + w * w;
            if ((!allowZero || lengthSq > 0.000001f) &&
                (lengthSq < 0.25f || lengthSq > 2.25f))
                throw new ProtocolException("Implausible " + name + " length " + lengthSq + ".");
        }

        private static void WriteCurve(NetworkWriter writer,
            float ax, float ay, float az, float bx, float by, float bz,
            float cx, float cy, float cz, float dx, float dy, float dz)
        {
            WriteFloat3(writer, ax, ay, az); WriteFloat3(writer, bx, by, bz);
            WriteFloat3(writer, cx, cy, cz); WriteFloat3(writer, dx, dy, dz);
        }

        private static void ReadCurve(NetworkReader reader,
            out float ax, out float ay, out float az, out float bx, out float by, out float bz,
            out float cx, out float cy, out float cz, out float dx, out float dy, out float dz)
        {
            ReadFloat3(reader, out ax, out ay, out az); ReadFloat3(reader, out bx, out by, out bz);
            ReadFloat3(reader, out cx, out cy, out cz); ReadFloat3(reader, out dx, out dy, out dz);
        }

        private static int ReadIndex(NetworkReader reader, string name)
        {
            int value = reader.ReadInt();
            if (value < -1 || value > 1000000)
                throw new ProtocolException("Implausible " + name + " " + value + ".");
            return value;
        }

        private static float ReadBounded(NetworkReader reader, float min, float max, string name)
        {
            float value = WireGuard.ReadFinite(reader);
            if (value < min || value > max)
                throw new ProtocolException("Implausible " + name + " " + value + ".");
            return value;
        }
    }
}
