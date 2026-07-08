using Game.Prefabs;
using Game.Routes;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Sync.Commands;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    public partial class RouteSyncSystem
    {
        /// <summary>Ordered waypoint positions of a route, or null when unreadable/too short.</summary>
        private float3[] WaypointPositions(Entity route)
        {
            if (!EntityManager.HasBuffer<RouteWaypoint>(route)) return null;
            DynamicBuffer<RouteWaypoint> waypoints = EntityManager.GetBuffer<RouteWaypoint>(route, true);
            if (waypoints.Length < 2) return null;

            var positions = new float3[waypoints.Length];
            for (int i = 0; i < waypoints.Length; i++)
            {
                if (!EntityManager.HasComponent<Position>(waypoints[i].m_Waypoint)) return null;
                positions[i] = EntityManager.GetComponentData<Position>(waypoints[i].m_Waypoint).m_Position;
            }
            return positions;
        }

        private void CaptureCreated(MultiplayerSession session, long now)
        {
            if (_createdRoutes.IsEmptyIgnoreFilter) return;

            NativeArray<Entity> entities = _createdRoutes.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity entity = entities[i];
                    string name = _prefabSystem.GetPrefabName(EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab);
                    if (string.IsNullOrEmpty(name)) continue;

                    float3[] positions = WaypointPositions(entity);
                    if (positions == null) continue;
                    if (_guard.Consume(RouteKey(name, positions[0]), now)) continue;

                    var command = new RouteCreateCommand
                    {
                        PrefabName = name,
                        WaypointX = new float[positions.Length],
                        WaypointY = new float[positions.Length],
                        WaypointZ = new float[positions.Length],
                    };
                    for (int w = 0; w < positions.Length; w++)
                    {
                        command.WaypointX[w] = positions[w].x;
                        command.WaypointY[w] = positions[w].y;
                        command.WaypointZ[w] = positions[w].z;
                    }
                    if (EntityManager.HasComponent<RouteNumber>(entity))
                        command.RouteNumber = EntityManager.GetComponentData<RouteNumber>(entity).m_Number;
                    if (EntityManager.HasComponent<Color>(entity))
                    {
                        UnityEngine.Color32 color = EntityManager.GetComponentData<Color>(entity).m_Color;
                        command.ColorR = color.r; command.ColorG = color.g;
                        command.ColorB = color.b; command.ColorA = color.a;
                    }
                    session.SendCommand(0, RouteCreateCommand.Id, command.Encode());
                    Mod.Verbose("[MP] RouteSync captured line '" + name + "' (" + positions.Length + " stops).");
                }
            }
            finally
            {
                entities.Dispose();
            }
        }

        private void CaptureDeleted(MultiplayerSession session, long now)
        {
            if (_deletedRoutes.IsEmptyIgnoreFilter) return;

            NativeArray<Entity> entities = _deletedRoutes.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity entity = entities[i];
                    string name = _prefabSystem.GetPrefabName(EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab);
                    if (string.IsNullOrEmpty(name)) continue;

                    // Waypoint entities are torn down with the route but their data is
                    // still readable here at ModificationEnd.
                    if (!EntityManager.HasBuffer<RouteWaypoint>(entity)) continue;
                    DynamicBuffer<RouteWaypoint> waypoints = EntityManager.GetBuffer<RouteWaypoint>(entity, true);
                    if (waypoints.Length == 0 ||
                        !EntityManager.HasComponent<Position>(waypoints[0].m_Waypoint)) continue;
                    float3 first = EntityManager.GetComponentData<Position>(waypoints[0].m_Waypoint).m_Position;

                    if (_guard.Consume(RouteDeleteKey(name, first), now)) continue;

                    var command = new RouteDeleteCommand
                    {
                        PrefabName = name,
                        WaypointX = first.x, WaypointY = first.y, WaypointZ = first.z,
                    };
                    session.SendCommand(0, RouteDeleteCommand.Id, command.Encode());
                }
            }
            finally
            {
                entities.Dispose();
            }
        }

        private static bool RingsEqual(float3[] a, float3[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (math.distancesq(a[i], b[i]) > 0.01f) return false;
            return true;
        }

        private uint ColorOf(Entity route)
        {
            if (!EntityManager.HasComponent<Color>(route)) return 0;
            UnityEngine.Color32 c = EntityManager.GetComponentData<Color>(route).m_Color;
            return (uint)(c.r | (c.g << 8) | (c.b << 16) | (c.a << 24));
        }

        /// <summary>
        /// 1 Hz comparison of every line's waypoint ring + color against the last scan -
        /// edits don't reliably surface as Created/Deleted, so they are detected by
        /// content. First sighting only records (creation has its own command).
        /// </summary>
        private void ScanForEdits(MultiplayerSession session, long now)
        {
            if (now - _lastEditScanMs < EditScanIntervalMs) return;
            _lastEditScanMs = now;

            _nextRoutes.Clear();
            NativeArray<Entity> entities = _liveRoutes.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity entity = entities[i];
                    float3[] ring = WaypointPositions(entity);
                    if (ring == null) continue;
                    uint rgba = ColorOf(entity);

                    RouteSnapshot old;
                    bool had = _knownRoutes.TryGetValue(entity, out old);
                    _nextRoutes[entity] = new RouteSnapshot { Ring = ring, Rgba = rgba };
                    if (!had) continue;
                    if (RingsEqual(old.Ring, ring) && old.Rgba == rgba) continue;

                    string name = _prefabSystem.GetPrefabName(EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab);
                    if (string.IsNullOrEmpty(name)) continue;
                    if (_guard.Consume(RouteUpdateKey(name, ring[0]), now)) continue;

                    var command = new RouteUpdateCommand
                    {
                        PrefabName = name,
                        AnchorX = old.Ring[0].x, AnchorY = old.Ring[0].y, AnchorZ = old.Ring[0].z,
                        ColorR = (byte)rgba, ColorG = (byte)(rgba >> 8),
                        ColorB = (byte)(rgba >> 16), ColorA = (byte)(rgba >> 24),
                        WaypointX = new float[ring.Length],
                        WaypointY = new float[ring.Length],
                        WaypointZ = new float[ring.Length],
                    };
                    for (int w = 0; w < ring.Length; w++)
                    {
                        command.WaypointX[w] = ring[w].x;
                        command.WaypointY[w] = ring[w].y;
                        command.WaypointZ[w] = ring[w].z;
                    }
                    session.SendCommand(0, RouteUpdateCommand.Id, command.Encode());
                    Mod.Verbose("[MP] RouteSync captured edit of line '" + name + "' (" +
                                 ring.Length + " stops).");
                }
            }
            finally
            {
                entities.Dispose();
            }

            var swap = _knownRoutes;
            _knownRoutes = _nextRoutes;
            _nextRoutes = swap;
        }

    }
}
