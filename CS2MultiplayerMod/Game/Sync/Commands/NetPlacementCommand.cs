using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Sync;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>How a native net-course endpoint relates to existing network geometry.</summary>
    public enum NetEndpointTargetKind : byte
    {
        /// <summary>No captured intent is available; the receiver must classify from geometry.</summary>
        Infer = 0,
        /// <summary>The endpoint deliberately creates a new node.</summary>
        Free = 1,
        /// <summary>The endpoint reuses an owner-less network node.</summary>
        Node = 2,
        /// <summary>The endpoint splits an existing network edge.</summary>
        Edge = 3,
        /// <summary>The endpoint reuses an owned connector node.</summary>
        OwnedNode = 4,
        /// <summary>The endpoint targets an owned connector edge.</summary>
        OwnedEdge = 5,
    }

    /// <summary>
    /// Portable form of one native course endpoint. Entity ids are deliberately absent: nodes and
    /// edges are identified on the receiver by a source-world anchor, an optional prefab name and,
    /// for an edge, its source curve. The remaining fields are copied into the receiver's CoursePos.
    /// </summary>
    public struct NetEndpointIntent
    {
        public NetEndpointTargetKind Kind;

        public float PosX, PosY, PosZ;
        public float RotX, RotY, RotZ, RotW;
        public float ElevationLeft, ElevationRight;
        public float CourseDelta;
        public float SplitPosition;
        public uint Flags;
        public int ParentMesh;

        public string TargetPrefabName;
        public float AnchorX, AnchorY, AnchorZ;

        // Source target-edge curve. It disambiguates close parallel carriageways and crossings;
        // the anchor still permits a receiver whose equivalent road is subdivided differently.
        public float TargetAx, TargetAy, TargetAz;
        public float TargetBx, TargetBy, TargetBz;
        public float TargetCx, TargetCy, TargetCz;
        public float TargetDx, TargetDy, TargetDz;
    }

    /// <summary>
    /// "A player committed this native net course." In addition to its final cubic curve, a captured
    /// command carries the placement intent the network generator consumed: exact endpoint mode,
    /// portable target identity, elevation, course flags and creation state. Courses emitted by one
    /// tool apply share <see cref="OperationId"/> and carry an index/count for correlation.
    /// </summary>
    public sealed class NetPlacementCommand : ISimulationCommand
    {
        public const ushort Id = 2;
        public const int MaxEncodedBytes = 4096;
        public const int MaxCoursesPerOperation = 1024;

        private const uint KnownCoursePosFlags = 0x7fffu;
        private const uint KnownCreationFlags = 0xfffffu;

        public long OperationId;
        public short CourseIndex;
        public short CourseCount;
        public bool HasNativeCourse;

        public string PrefabName;
        public string SubPrefabName;

        // Cubic Bezier control points a -> b -> c -> d (start, two handles, end).
        public float Ax, Ay, Az;
        public float Bx, By, Bz;
        public float Cx, Cy, Cz;
        public float Dx, Dy, Dz;
        public float Length;

        public int RandomSeed;
        public uint CreationFlags;
        public float CourseElevationLeft, CourseElevationRight;
        public int FixedIndex;
        public NetEndpointIntent Start;
        public NetEndpointIntent End;

        public ushort CommandId => Id;

        public void Write(NetworkWriter w)
        {
            int count = CourseCount > 0 ? CourseCount : 1;
            int index = CourseIndex >= 0 ? CourseIndex : 0;
            if (count > MaxCoursesPerOperation || index >= count)
                throw new ProtocolException("Invalid net-course operation index/count " + index + "/" + count + ".");

            w.WriteLong(OperationId);
            w.WriteShort((short)index);
            w.WriteShort((short)count);
            w.WriteBool(HasNativeCourse);
            w.WriteString(PrefabName);
            WriteCurve(w, Ax, Ay, Az, Bx, By, Bz, Cx, Cy, Cz, Dx, Dy, Dz);
            w.WriteFloat(Length);

            if (!HasNativeCourse) return;

            w.WriteInt(RandomSeed);
            w.WriteInt(unchecked((int)CreationFlags));
            bool hasSubPrefab = !string.IsNullOrEmpty(SubPrefabName);
            w.WriteBool(hasSubPrefab);
            if (hasSubPrefab) w.WriteString(SubPrefabName);
            w.WriteFloat(CourseElevationLeft);
            w.WriteFloat(CourseElevationRight);
            w.WriteInt(FixedIndex);
            WriteEndpoint(w, Start);
            WriteEndpoint(w, End);
        }

        public void Read(NetworkReader r)
        {
            OperationId = r.ReadLong();
            CourseIndex = r.ReadShort();
            CourseCount = r.ReadShort();
            if (CourseCount <= 0 || CourseCount > MaxCoursesPerOperation ||
                CourseIndex < 0 || CourseIndex >= CourseCount)
                throw new ProtocolException("Invalid net-course operation index/count " +
                                            CourseIndex + "/" + CourseCount + ".");

            HasNativeCourse = r.ReadBool();
            PrefabName = WireGuard.ReadName(r);
            ReadCurve(r, out Ax, out Ay, out Az, out Bx, out By, out Bz,
                out Cx, out Cy, out Cz, out Dx, out Dy, out Dz);
            Length = ReadBounded(r, 0f, WireGuard.MaxCoordinate, "net-course length");

            if (HasNativeCourse)
            {
                RandomSeed = r.ReadInt();
                CreationFlags = unchecked((uint)r.ReadInt());
                if ((CreationFlags & ~KnownCreationFlags) != 0)
                    throw new ProtocolException("Unknown net creation flags 0x" + CreationFlags.ToString("x") + ".");
                if (r.ReadBool()) SubPrefabName = WireGuard.ReadName(r);
                CourseElevationLeft = ReadBounded(r, -100000f, 100000f, "course elevation");
                CourseElevationRight = ReadBounded(r, -100000f, 100000f, "course elevation");
                FixedIndex = r.ReadInt();
                if (FixedIndex < -1 || FixedIndex > 1000000)
                    throw new ProtocolException("Implausible fixed-net index " + FixedIndex + ".");
                Start = ReadEndpoint(r);
                End = ReadEndpoint(r);
            }

            if (r.Remaining != 0)
                throw new ProtocolException("Trailing bytes in net-placement command: " + r.Remaining + ".");
        }

        public byte[] Encode()
        {
            var w = new NetworkWriter(512);
            Write(w);
            if (w.Length > MaxEncodedBytes)
                throw new ProtocolException("Net-placement command body " + w.Length +
                                            " exceeds the " + MaxEncodedBytes + "-byte cap.");
            return w.ToArray();
        }

        public static NetPlacementCommand Decode(byte[] body)
        {
            if (body == null) throw new ProtocolException("Null net-placement command body.");
            if (body.Length > MaxEncodedBytes)
                throw new ProtocolException("Net-placement command body " + body.Length +
                                            " exceeds the " + MaxEncodedBytes + "-byte cap.");
            var c = new NetPlacementCommand();
            c.Read(new NetworkReader(body));
            return c;
        }

        private static void WriteEndpoint(NetworkWriter w, NetEndpointIntent endpoint)
        {
            w.WriteByte((byte)endpoint.Kind);
            w.WriteFloat(endpoint.PosX); w.WriteFloat(endpoint.PosY); w.WriteFloat(endpoint.PosZ);
            w.WriteFloat(endpoint.RotX); w.WriteFloat(endpoint.RotY);
            w.WriteFloat(endpoint.RotZ); w.WriteFloat(endpoint.RotW);
            w.WriteFloat(endpoint.ElevationLeft); w.WriteFloat(endpoint.ElevationRight);
            w.WriteFloat(endpoint.CourseDelta);
            w.WriteFloat(endpoint.SplitPosition);
            w.WriteInt(unchecked((int)endpoint.Flags));
            w.WriteInt(endpoint.ParentMesh);

            bool hasTarget = endpoint.Kind == NetEndpointTargetKind.Node ||
                             endpoint.Kind == NetEndpointTargetKind.OwnedNode ||
                             endpoint.Kind == NetEndpointTargetKind.Edge ||
                             endpoint.Kind == NetEndpointTargetKind.OwnedEdge;
            if (!hasTarget) return;

            bool hasTargetPrefab = !string.IsNullOrEmpty(endpoint.TargetPrefabName);
            w.WriteBool(hasTargetPrefab);
            if (hasTargetPrefab) w.WriteString(endpoint.TargetPrefabName);
            w.WriteFloat(endpoint.AnchorX); w.WriteFloat(endpoint.AnchorY); w.WriteFloat(endpoint.AnchorZ);
            if (endpoint.Kind == NetEndpointTargetKind.Edge ||
                endpoint.Kind == NetEndpointTargetKind.OwnedEdge)
                WriteCurve(w,
                    endpoint.TargetAx, endpoint.TargetAy, endpoint.TargetAz,
                    endpoint.TargetBx, endpoint.TargetBy, endpoint.TargetBz,
                    endpoint.TargetCx, endpoint.TargetCy, endpoint.TargetCz,
                    endpoint.TargetDx, endpoint.TargetDy, endpoint.TargetDz);
        }

        private static NetEndpointIntent ReadEndpoint(NetworkReader r)
        {
            var endpoint = new NetEndpointIntent { Kind = (NetEndpointTargetKind)r.ReadByte() };
            if (endpoint.Kind < NetEndpointTargetKind.Infer || endpoint.Kind > NetEndpointTargetKind.OwnedEdge)
                throw new ProtocolException("Unknown net endpoint kind " + (byte)endpoint.Kind + ".");

            endpoint.PosX = WireGuard.ReadCoordinate(r);
            endpoint.PosY = WireGuard.ReadCoordinate(r);
            endpoint.PosZ = WireGuard.ReadCoordinate(r);
            endpoint.RotX = ReadBounded(r, -2f, 2f, "course rotation");
            endpoint.RotY = ReadBounded(r, -2f, 2f, "course rotation");
            endpoint.RotZ = ReadBounded(r, -2f, 2f, "course rotation");
            endpoint.RotW = ReadBounded(r, -2f, 2f, "course rotation");
            endpoint.ElevationLeft = ReadBounded(r, -100000f, 100000f, "endpoint elevation");
            endpoint.ElevationRight = ReadBounded(r, -100000f, 100000f, "endpoint elevation");
            endpoint.CourseDelta = ReadBounded(r, -2f, 3f, "course delta");
            endpoint.SplitPosition = ReadBounded(r, -2f, 3f, "split position");
            endpoint.Flags = unchecked((uint)r.ReadInt());
            if ((endpoint.Flags & ~KnownCoursePosFlags) != 0)
                throw new ProtocolException("Unknown course-position flags 0x" + endpoint.Flags.ToString("x") + ".");
            endpoint.ParentMesh = r.ReadInt();
            if (endpoint.ParentMesh < -1 || endpoint.ParentMesh > 1000000)
                throw new ProtocolException("Implausible parent mesh " + endpoint.ParentMesh + ".");

            bool hasTarget = endpoint.Kind == NetEndpointTargetKind.Node ||
                             endpoint.Kind == NetEndpointTargetKind.OwnedNode ||
                             endpoint.Kind == NetEndpointTargetKind.Edge ||
                             endpoint.Kind == NetEndpointTargetKind.OwnedEdge;
            if (!hasTarget) return endpoint;

            if (r.ReadBool()) endpoint.TargetPrefabName = WireGuard.ReadName(r);
            endpoint.AnchorX = WireGuard.ReadCoordinate(r);
            endpoint.AnchorY = WireGuard.ReadCoordinate(r);
            endpoint.AnchorZ = WireGuard.ReadCoordinate(r);
            if (endpoint.Kind == NetEndpointTargetKind.Edge ||
                endpoint.Kind == NetEndpointTargetKind.OwnedEdge)
                ReadCurve(r,
                    out endpoint.TargetAx, out endpoint.TargetAy, out endpoint.TargetAz,
                    out endpoint.TargetBx, out endpoint.TargetBy, out endpoint.TargetBz,
                    out endpoint.TargetCx, out endpoint.TargetCy, out endpoint.TargetCz,
                    out endpoint.TargetDx, out endpoint.TargetDy, out endpoint.TargetDz);
            return endpoint;
        }

        private static void WriteCurve(NetworkWriter w,
            float ax, float ay, float az, float bx, float by, float bz,
            float cx, float cy, float cz, float dx, float dy, float dz)
        {
            w.WriteFloat(ax); w.WriteFloat(ay); w.WriteFloat(az);
            w.WriteFloat(bx); w.WriteFloat(by); w.WriteFloat(bz);
            w.WriteFloat(cx); w.WriteFloat(cy); w.WriteFloat(cz);
            w.WriteFloat(dx); w.WriteFloat(dy); w.WriteFloat(dz);
        }

        private static void ReadCurve(NetworkReader r,
            out float ax, out float ay, out float az, out float bx, out float by, out float bz,
            out float cx, out float cy, out float cz, out float dx, out float dy, out float dz)
        {
            ax = WireGuard.ReadCoordinate(r); ay = WireGuard.ReadCoordinate(r); az = WireGuard.ReadCoordinate(r);
            bx = WireGuard.ReadCoordinate(r); by = WireGuard.ReadCoordinate(r); bz = WireGuard.ReadCoordinate(r);
            cx = WireGuard.ReadCoordinate(r); cy = WireGuard.ReadCoordinate(r); cz = WireGuard.ReadCoordinate(r);
            dx = WireGuard.ReadCoordinate(r); dy = WireGuard.ReadCoordinate(r); dz = WireGuard.ReadCoordinate(r);
        }

        private static float ReadBounded(NetworkReader r, float min, float max, string name)
        {
            float value = WireGuard.ReadFinite(r);
            if (value < min || value > max)
                throw new ProtocolException("Implausible " + name + " " + value + ".");
            return value;
        }
    }
}
