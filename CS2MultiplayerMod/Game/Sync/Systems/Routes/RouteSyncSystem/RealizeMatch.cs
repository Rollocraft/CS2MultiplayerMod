using System;
using System.Collections.Generic;
using Game.Common;
using Game.Prefabs;
using Game.Routes;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Game.Sync.Commands;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    // Finding the local route a command refers to, and telling an existing one from a route this
    // peer has yet to create: by number, by anchor position, and by the shape of its waypoints.
    public partial class RouteSyncSystem
    {
        private Entity[] MatchOriginalWaypoints(Entity route, Entity[] connections,
            float3[] positions)
        {
            var result = new Entity[positions.Length];
            if (route == Entity.Null || !EntityManager.HasBuffer<RouteWaypoint>(route))
                return result;

            DynamicBuffer<RouteWaypoint> original =
                EntityManager.GetBuffer<RouteWaypoint>(route, isReadOnly: true);
            var used = new bool[original.Length];
            for (int i = 0; i < positions.Length; i++)
            {
                for (int j = 0; j < original.Length; j++)
                {
                    if (used[j]) continue;
                    Entity waypoint = original[j].m_Waypoint;
                    if (!EntityManager.HasComponent<Position>(waypoint) ||
                        !WaypointPositionMatches(
                            EntityManager.GetComponentData<Position>(waypoint).m_Position,
                            positions[i], connections[i]))
                        continue;

                    Entity oldConnection = Entity.Null;
                    if (EntityManager.HasComponent<Connected>(waypoint))
                        oldConnection =
                            EntityManager.GetComponentData<Connected>(waypoint).m_Connected;
                    if (oldConnection != connections[i]) continue;
                    used[j] = true;
                    result[i] = waypoint;
                    break;
                }
            }
            return result;
        }

        private bool RouteGraphMatches(Entity route, Entity[] connections, float3[] positions)
        {
            if (!EntityManager.HasBuffer<RouteWaypoint>(route)) return false;
            DynamicBuffer<RouteWaypoint> current =
                EntityManager.GetBuffer<RouteWaypoint>(route, isReadOnly: true);
            if (current.Length != positions.Length) return false;

            for (int i = 0; i < positions.Length; i++)
            {
                Entity waypoint = current[i].m_Waypoint;
                if (!EntityManager.Exists(waypoint) ||
                    !EntityManager.HasComponent<Position>(waypoint) ||
                    !WaypointPositionMatches(EntityManager
                            .GetComponentData<Position>(waypoint).m_Position,
                        positions[i], connections[i]))
                    return false;
                Entity connection = Entity.Null;
                if (EntityManager.HasComponent<Connected>(waypoint))
                    connection =
                        EntityManager.GetComponentData<Connected>(waypoint).m_Connected;
                if (connection != connections[i]) return false;
            }
            return true;
        }

        /// <summary>
        /// A waypoint that only shapes the path is placed at exactly the submitted position; one
        /// bound to a stop follows that stop and is compared with the stop tolerances.
        /// </summary>
        private static bool WaypointPositionMatches(float3 actual, float3 wanted,
            Entity connection) =>
            connection == Entity.Null
                ? math.distancesq(actual, wanted) <= FreeWaypointMatchDistanceSq
                : StopPositionsMatch(actual, wanted);

        private Entity FindExistingCreate(Entity prefab, int routeNumber,
            RouteWaypointIntent[] desired, out bool numberConflict)
        {
            numberConflict = false;
            if (routeNumber <= 0) return Entity.Null;

            NativeArray<Entity> routes = _liveRoutes.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < routes.Length; i++)
                {
                    Entity candidate = routes[i];
                    if (EntityManager.GetComponentData<PrefabRef>(candidate).m_Prefab != prefab ||
                        RouteNumberOf(candidate) != routeNumber)
                        continue;

                    RouteSnapshot snapshot;
                    if (TryCaptureSnapshot(candidate, out snapshot) &&
                        WaypointsMatchIntent(snapshot.Waypoints, desired))
                        return candidate;
                    numberConflict = true;
                    return Entity.Null;
                }
                return Entity.Null;
            }
            finally
            {
                routes.Dispose();
            }
        }

        private HashSet<Entity> CaptureShapeMatches(Entity prefab,
            RouteWaypointIntent[] desired)
        {
            var result = new HashSet<Entity>();
            NativeArray<Entity> routes = _liveRoutes.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < routes.Length; i++)
                {
                    Entity candidate = routes[i];
                    if (EntityManager.GetComponentData<PrefabRef>(candidate).m_Prefab != prefab)
                        continue;
                    RouteSnapshot snapshot;
                    if (TryCaptureSnapshot(candidate, out snapshot) &&
                        WaypointsMatchIntent(snapshot.Waypoints, desired))
                        result.Add(candidate);
                }
                return result;
            }
            finally
            {
                routes.Dispose();
            }
        }

        private Entity FindRoute(Entity prefab, int routeNumber, float3 anchor,
            float maxAnchorDistanceSq, out bool ambiguous)
        {
            ambiguous = false;
            var exact = new List<Entity>();
            var spatial = new List<Entity>();
            NativeArray<Entity> routes = _liveRoutes.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < routes.Length; i++)
                {
                    Entity candidate = routes[i];
                    if (EntityManager.GetComponentData<PrefabRef>(candidate).m_Prefab != prefab)
                        continue;
                    if (routeNumber > 0 && RouteNumberOf(candidate) == routeNumber)
                        exact.Add(candidate);

                    float3 first;
                    if (TryGetFirstWaypoint(candidate, out first) &&
                        math.distancesq(first, anchor) <= maxAnchorDistanceSq)
                        spatial.Add(candidate);
                }
            }
            finally
            {
                routes.Dispose();
            }

            if (exact.Count == 1) return exact[0];
            if (exact.Count > 1)
            {
                Entity match = Entity.Null;
                for (int i = 0; i < exact.Count; i++)
                {
                    float3 first;
                    if (!TryGetFirstWaypoint(exact[i], out first) ||
                        math.distancesq(first, anchor) > maxAnchorDistanceSq)
                        continue;
                    if (match != Entity.Null)
                    {
                        ambiguous = true;
                        return Entity.Null;
                    }
                    match = exact[i];
                }
                if (match != Entity.Null) return match;
                ambiguous = true;
                return Entity.Null;
            }

            if (spatial.Count == 1) return spatial[0];
            ambiguous = spatial.Count > 1;
            return Entity.Null;
        }

        private bool TryGetFirstWaypoint(Entity route, out float3 position)
        {
            position = default(float3);
            if (!EntityManager.HasBuffer<RouteWaypoint>(route)) return false;
            DynamicBuffer<RouteWaypoint> waypoints =
                EntityManager.GetBuffer<RouteWaypoint>(route, isReadOnly: true);
            if (waypoints.Length == 0 ||
                !EntityManager.HasComponent<Position>(waypoints[0].m_Waypoint))
                return false;
            position =
                EntityManager.GetComponentData<Position>(waypoints[0].m_Waypoint).m_Position;
            return true;
        }

        private bool TryApplyMetadata(Entity route, Entity prefab, int routeNumber, uint rgba,
            HashSet<Entity> ignoredNumberConflicts = null)
        {
            if (!RouteNumberAvailable(route, prefab, routeNumber,
                    ignoredNumberConflicts))
                return false;
            if (routeNumber > 0)
            {
                if (EntityManager.HasComponent<RouteNumber>(route))
                    EntityManager.SetComponentData(route,
                        new RouteNumber { m_Number = routeNumber });
                else
                    EntityManager.AddComponentData(route,
                        new RouteNumber { m_Number = routeNumber });
            }

            UnityEngine.Color32 color = UnpackColor(rgba);
            if (EntityManager.HasComponent<Color>(route))
                EntityManager.SetComponentData(route, new Color { m_Color = color });
            else
                EntityManager.AddComponentData(route, new Color { m_Color = color });
            if (!EntityManager.HasComponent<Updated>(route))
                EntityManager.AddComponent<Updated>(route);
            return true;
        }

        private bool RouteNumberAvailable(Entity route, Entity prefab, int routeNumber,
            HashSet<Entity> ignoredRoutes = null)
        {
            if (routeNumber <= 0) return true;
            NativeArray<Entity> routes = _liveRoutes.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < routes.Length; i++)
                {
                    Entity other = routes[i];
                    if (other == route ||
                        (ignoredRoutes != null && ignoredRoutes.Contains(other)) ||
                        EntityManager.GetComponentData<PrefabRef>(other).m_Prefab != prefab)
                        continue;
                    if (RouteNumberOf(other) == routeNumber) return false;
                }
                return true;
            }
            finally
            {
                routes.Dispose();
            }
        }
    }
}
