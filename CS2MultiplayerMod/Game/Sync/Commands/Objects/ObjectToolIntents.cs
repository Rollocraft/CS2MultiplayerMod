using System;
using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Sync;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    // The portable vocabulary an object-tool operation is described in: the kinds of definition a
    // tool emits, and the references that name an entity to a peer that shares none of our entity
    // ids - a prefab, a kind, and the path of owners down from a top-level object.
    //
    // Separated from ObjectToolOperationCommand.cs, which is the command that carries them.
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
}
