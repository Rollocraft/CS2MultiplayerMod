using System;
using CS2MultiplayerMod.Core.Protocol;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>
    /// Portable identity for one route waypoint and, when present, the transport-stop object to
    /// which it is connected. Entity ids are world-local, so a stop is described by prefab and
    /// transform; an optional top-level owner disambiguates identical station subobjects.
    /// </summary>
    public struct RouteWaypointIntent
    {
        public float X, Y, Z;

        public string StopPrefabName;
        public float StopX, StopY, StopZ;

        public string OwnerPrefabName;
        public float OwnerX, OwnerY, OwnerZ;
    }

    internal static class RouteCommandCodec
    {
        internal const int MaximumRouteNumber = 100000;
        internal const int MinimumWaypointBytes = 13; // position + has-stop flag

        internal static void ValidateRoute(string prefabName, int routeNumber,
            RouteWaypointIntent[] waypoints, int maxWaypoints)
        {
            ValidateName(prefabName, "route prefab");
            ValidateRouteNumber(routeNumber);
            if (waypoints == null || waypoints.Length < 2)
                throw new ProtocolException("A route must contain at least two waypoints.");
            if (waypoints.Length > maxWaypoints)
                throw new ProtocolException("Route waypoint count " + waypoints.Length +
                                            " exceeds limit " + maxWaypoints + ".");
            for (int i = 0; i < waypoints.Length; i++) ValidateWaypoint(waypoints[i], i);
        }

        internal static void ValidateRouteNumber(int routeNumber)
        {
            if (routeNumber < 0 || routeNumber > MaximumRouteNumber)
                throw new ProtocolException("Implausible route number: " + routeNumber + ".");
        }

        internal static void ValidateAnchor(float x, float y, float z)
        {
            ValidateCoordinate(x, "route anchor X");
            ValidateCoordinate(y, "route anchor Y");
            ValidateCoordinate(z, "route anchor Z");
        }

        internal static void WriteWaypoints(NetworkWriter writer, RouteWaypointIntent[] waypoints)
        {
            writer.WriteShort((short)waypoints.Length);
            for (int i = 0; i < waypoints.Length; i++)
            {
                RouteWaypointIntent waypoint = waypoints[i];
                writer.WriteFloat(waypoint.X);
                writer.WriteFloat(waypoint.Y);
                writer.WriteFloat(waypoint.Z);

                bool hasStop = !string.IsNullOrEmpty(waypoint.StopPrefabName);
                writer.WriteBool(hasStop);
                if (!hasStop) continue;

                writer.WriteString(waypoint.StopPrefabName);
                writer.WriteFloat(waypoint.StopX);
                writer.WriteFloat(waypoint.StopY);
                writer.WriteFloat(waypoint.StopZ);

                bool hasOwner = !string.IsNullOrEmpty(waypoint.OwnerPrefabName);
                writer.WriteBool(hasOwner);
                if (!hasOwner) continue;
                writer.WriteString(waypoint.OwnerPrefabName);
                writer.WriteFloat(waypoint.OwnerX);
                writer.WriteFloat(waypoint.OwnerY);
                writer.WriteFloat(waypoint.OwnerZ);
            }
        }

        internal static RouteWaypointIntent[] ReadWaypoints(NetworkReader reader, int maxWaypoints)
        {
            int count = WireGuard.ReadCount(reader, MinimumWaypointBytes, maxWaypoints);
            if (count < 2) throw new ProtocolException("A route must contain at least two waypoints.");

            var result = new RouteWaypointIntent[count];
            for (int i = 0; i < count; i++)
            {
                RouteWaypointIntent waypoint = default(RouteWaypointIntent);
                waypoint.X = WireGuard.ReadCoordinate(reader);
                waypoint.Y = WireGuard.ReadCoordinate(reader);
                waypoint.Z = WireGuard.ReadCoordinate(reader);
                if (reader.ReadBool())
                {
                    waypoint.StopPrefabName = WireGuard.ReadName(reader);
                    waypoint.StopX = WireGuard.ReadCoordinate(reader);
                    waypoint.StopY = WireGuard.ReadCoordinate(reader);
                    waypoint.StopZ = WireGuard.ReadCoordinate(reader);
                    if (reader.ReadBool())
                    {
                        waypoint.OwnerPrefabName = WireGuard.ReadName(reader);
                        waypoint.OwnerX = WireGuard.ReadCoordinate(reader);
                        waypoint.OwnerY = WireGuard.ReadCoordinate(reader);
                        waypoint.OwnerZ = WireGuard.ReadCoordinate(reader);
                    }
                }
                result[i] = waypoint;
            }
            return result;
        }

        internal static void RequireFullyRead(NetworkReader reader, string commandName)
        {
            if (reader.Remaining != 0)
                throw new ProtocolException("Trailing bytes in " + commandName +
                                            " command: " + reader.Remaining + ".");
        }

        private static void ValidateWaypoint(RouteWaypointIntent waypoint, int index)
        {
            ValidateCoordinate(waypoint.X, "waypoint " + index + " X");
            ValidateCoordinate(waypoint.Y, "waypoint " + index + " Y");
            ValidateCoordinate(waypoint.Z, "waypoint " + index + " Z");

            bool hasStop = !string.IsNullOrEmpty(waypoint.StopPrefabName);
            bool hasOwner = !string.IsNullOrEmpty(waypoint.OwnerPrefabName);
            if (!hasStop)
            {
                if (hasOwner)
                    throw new ProtocolException("Route waypoint " + index +
                                                " has an owner but no connected stop.");
                return;
            }

            ValidateName(waypoint.StopPrefabName, "stop prefab");
            ValidateCoordinate(waypoint.StopX, "stop " + index + " X");
            ValidateCoordinate(waypoint.StopY, "stop " + index + " Y");
            ValidateCoordinate(waypoint.StopZ, "stop " + index + " Z");
            if (!hasOwner) return;

            ValidateName(waypoint.OwnerPrefabName, "stop owner prefab");
            ValidateCoordinate(waypoint.OwnerX, "stop owner " + index + " X");
            ValidateCoordinate(waypoint.OwnerY, "stop owner " + index + " Y");
            ValidateCoordinate(waypoint.OwnerZ, "stop owner " + index + " Z");
        }

        internal static void ValidateName(string value, string field)
        {
            if (string.IsNullOrEmpty(value))
                throw new ProtocolException("Empty " + field + " name.");
            if (value.Length > WireGuard.MaxNameLength)
                throw new ProtocolException(field + " name longer than " +
                                            WireGuard.MaxNameLength + " characters.");
            for (int i = 0; i < value.Length; i++)
                if (char.IsControl(value[i]))
                    throw new ProtocolException("Control character in " + field + " name.");
        }

        private static void ValidateCoordinate(float value, string field)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) ||
                value < -WireGuard.MaxCoordinate || value > WireGuard.MaxCoordinate)
                throw new ProtocolException("Implausible " + field + ": " + value + ".");
        }
    }
}
