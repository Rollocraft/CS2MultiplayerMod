using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Systems.Net
{
    // Commit orchestration for NetSyncSystem. A remote net operation includes the objects and areas
    // its native generation updates as side effects; the complete local preview graph is temporarily
    // Disabled so an unrelated tool can remain selected without either transaction consuming the
    // other one's entities.
    // Validating an armed route transaction before it is committed.
    public partial class NetSyncSystem
    {
        /// <summary>
        /// Verify the complete route graph immediately before its isolated apply. Route application
        /// dereferences every root buffer entry and every non-null original, so a missing child or
        /// stale original rejects the whole graph instead of allowing a partially connected line.
        /// </summary>
        private bool ValidateArmedRouteTransaction(out string reason)
        {
            NativeArray<Entity> temps = _routeTransactionTemps.ToEntityArray(Allocator.Temp);
            try
            {
                if (temps.Length == 0)
                {
                    reason = "the generated route transaction was empty";
                    return false;
                }

                int routeCount = 0;
                int waypointCount = 0;
                int segmentCount = 0;
                for (int i = 0; i < temps.Length; i++)
                {
                    Entity entity = temps[i];
                    if (!EntityManager.Exists(entity) ||
                        EntityManager.HasComponent<Deleted>(entity))
                    {
                        reason = "the generated route transaction contains a deleted entity";
                        return false;
                    }

                    bool isRoute =
                        EntityManager.HasComponent<global::Game.Routes.Route>(entity);
                    bool isWaypoint =
                        EntityManager.HasComponent<global::Game.Routes.Waypoint>(entity);
                    bool isSegment =
                        EntityManager.HasComponent<global::Game.Routes.Segment>(entity);
                    if (!isRoute && !isWaypoint && !isSegment)
                    {
                        reason = "the route transaction contains an unknown Temp entity";
                        return false;
                    }

                    Temp temp = EntityManager.GetComponentData<Temp>(entity);
                    if (temp.m_Original != Entity.Null)
                    {
                        if (!EntityManager.Exists(temp.m_Original) ||
                            EntityManager.HasComponent<Deleted>(temp.m_Original) ||
                            EntityManager.HasComponent<Temp>(temp.m_Original))
                        {
                            reason = "a generated route entity has a stale original";
                            return false;
                        }
                        if ((isRoute &&
                             !EntityManager.HasComponent<global::Game.Routes.Route>(
                                 temp.m_Original)) ||
                            (isWaypoint &&
                             !EntityManager.HasComponent<global::Game.Routes.Waypoint>(
                                 temp.m_Original)) ||
                            (isSegment &&
                             !EntityManager.HasComponent<global::Game.Routes.Segment>(
                                 temp.m_Original)))
                        {
                            reason = "a generated route entity has a mismatched original";
                            return false;
                        }
                    }

                    if (isWaypoint)
                    {
                        waypointCount++;
                        if (EntityManager.HasComponent<global::Game.Routes.Connected>(entity))
                        {
                            Entity connected =
                                EntityManager
                                    .GetComponentData<global::Game.Routes.Connected>(entity)
                                    .m_Connected;
                            if (connected != Entity.Null &&
                                (!EntityManager.Exists(connected) ||
                                 EntityManager.HasComponent<Deleted>(connected)))
                            {
                                reason = "a generated route waypoint has a stale connection";
                                return false;
                            }
                        }
                    }
                    if (isSegment) segmentCount++;
                    if (!isRoute) continue;

                    routeCount++;
                    if (!EntityManager.HasBuffer<global::Game.Routes.RouteWaypoint>(entity) ||
                        !EntityManager.HasBuffer<global::Game.Routes.RouteSegment>(entity))
                    {
                        reason = "the generated route root is missing its graph buffers";
                        return false;
                    }
                    if (temp.m_Original != Entity.Null &&
                        (!EntityManager
                             .HasBuffer<global::Game.Routes.RouteWaypoint>(temp.m_Original) ||
                         !EntityManager
                             .HasBuffer<global::Game.Routes.RouteSegment>(temp.m_Original)))
                    {
                        reason = "the existing route root is missing its graph buffers";
                        return false;
                    }

                    DynamicBuffer<global::Game.Routes.RouteWaypoint> waypoints =
                        EntityManager.GetBuffer<global::Game.Routes.RouteWaypoint>(
                            entity, isReadOnly: true);
                    DynamicBuffer<global::Game.Routes.RouteSegment> segments =
                        EntityManager.GetBuffer<global::Game.Routes.RouteSegment>(
                            entity, isReadOnly: true);
                    if (waypoints.Length < 2 || segments.Length != waypoints.Length)
                    {
                        reason = "the generated route graph is incomplete";
                        return false;
                    }
                    for (int j = 0; j < waypoints.Length; j++)
                    {
                        Entity child = waypoints[j].m_Waypoint;
                        if (child == Entity.Null || !EntityManager.Exists(child) ||
                            !EntityManager.HasComponent<Temp>(child) ||
                            !EntityManager
                                .HasComponent<global::Game.Routes.Waypoint>(child) ||
                            EntityManager.HasComponent<Deleted>(child))
                        {
                            reason = "the generated route has a missing waypoint";
                            return false;
                        }
                    }
                    for (int j = 0; j < segments.Length; j++)
                    {
                        Entity child = segments[j].m_Segment;
                        if (child == Entity.Null || !EntityManager.Exists(child) ||
                            !EntityManager.HasComponent<Temp>(child) ||
                            !EntityManager
                                .HasComponent<global::Game.Routes.Segment>(child) ||
                            EntityManager.HasComponent<Deleted>(child))
                        {
                            reason = "the generated route has a missing segment";
                            return false;
                        }
                    }
                }

                if (routeCount != 1)
                {
                    reason = "the route transaction contains " + routeCount +
                             " roots instead of one";
                    return false;
                }
                if (waypointCount < 2 || segmentCount < 2)
                {
                    reason = "the route transaction is missing owned graph entities";
                    return false;
                }

                reason = null;
                return true;
            }
            finally
            {
                temps.Dispose();
            }
        }
    }
}
