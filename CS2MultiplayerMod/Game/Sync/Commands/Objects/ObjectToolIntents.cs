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
    /// One owner-relative step: the source buffer index, with prefab, kind and same-prefab ordinal as
    /// the fallback when unrelated entries differ.
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
    /// Portable identity of an object, net element or area. Owned net elements add their owner and layer
    /// contract so a nearby incompatible connector is not chosen.
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
        /// <summary>A prefab in the same placement graph, not an existing entity like <see cref="Attached"/>.</summary>
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
