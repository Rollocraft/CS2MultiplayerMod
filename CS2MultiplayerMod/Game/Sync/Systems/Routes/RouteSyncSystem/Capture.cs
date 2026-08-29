using System;
using System.Collections.Generic;
using Game.Common;
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
        /// <summary>
        /// Treat routes already present when gameplay synchronization opens as world state, not as
        /// local edits. A route whose graph is still initializing remains in the baseline set until
        /// a complete snapshot can be read, preventing a freshly loaded world from echoing all of
        /// its lines back as new commands.
        /// </summary>
        private void BaselineLiveRoutes()
        {
            _knownRoutes.Clear();
            _nextRoutes.Clear();
            _needsCreateCapture.Clear();
            _baselinePendingRoutes.Clear();

            NativeArray<Entity> entities = _liveRoutes.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    RouteSnapshot snapshot;
                    if (TryCaptureSnapshot(entities[i], out snapshot))
                        _knownRoutes[entities[i]] = snapshot;
                    else
                        _baselinePendingRoutes.Add(entities[i]);
                }
            }
            finally
            {
                entities.Dispose();
            }

            if (Mod.Service != null) _lastEditScanMs = Mod.Service.NowMs;
            Diagnostics.FlightRecorder.Note("route baseline live=" + _knownRoutes.Count +
                                              " pending=" + _baselinePendingRoutes.Count);
        }

        private bool TryCaptureSnapshot(Entity route, out RouteSnapshot snapshot)
        {
            snapshot = default(RouteSnapshot);
            RouteWaypointIntent[] waypoints;
            if (!TryCaptureWaypoints(route, out waypoints)) return false;

            Route routeData = EntityManager.GetComponentData<Route>(route);
            snapshot = new RouteSnapshot
            {
                Waypoints = waypoints,
                Rgba = ColorOf(route),
                RouteNumber = RouteNumberOf(route),
                IsComplete = (routeData.m_Flags & RouteFlags.Complete) != 0,
            };

            Entity prefab =
                EntityManager.GetComponentData<PrefabRef>(route).m_Prefab;
            if (!EntityManager.HasComponent<TransportLineData>(prefab)) return true;

            // The route tool only commits a public transport line once its loop closes, and the
            // line number is assigned a frame later - either missing means the graph is still
            // settling. Waypoints without a stop are legitimate: they only shape the path.
            if (!snapshot.IsComplete || snapshot.RouteNumber <= 0) return false;
            for (int i = 0; i < waypoints.Length; i++)
                if (!string.IsNullOrEmpty(waypoints[i].StopPrefabName))
                    return true;
            return false;
        }

        /// <summary>
        /// Captures the route's owned waypoint entities and their optional Connected stop. A
        /// transiently invalid reference makes the whole snapshot unavailable; publishing only part
        /// of a route would be worse than retrying on the next scan.
        /// </summary>
        private bool TryCaptureWaypoints(Entity route, out RouteWaypointIntent[] result)
        {
            result = null;
            if (!EntityManager.HasBuffer<RouteWaypoint>(route)) return false;
            DynamicBuffer<RouteWaypoint> waypoints =
                EntityManager.GetBuffer<RouteWaypoint>(route, isReadOnly: true);
            if (waypoints.Length < 2 || waypoints.Length > RouteCreateCommand.MaxWaypoints)
                return false;

            var captured = new RouteWaypointIntent[waypoints.Length];
            for (int i = 0; i < waypoints.Length; i++)
            {
                Entity waypointEntity = waypoints[i].m_Waypoint;
                if (waypointEntity == Entity.Null || !EntityManager.Exists(waypointEntity) ||
                    !EntityManager.HasComponent<Position>(waypointEntity))
                    return false;

                float3 position =
                    EntityManager.GetComponentData<Position>(waypointEntity).m_Position;
                RouteWaypointIntent value = new RouteWaypointIntent
                {
                    X = position.x,
                    Y = position.y,
                    Z = position.z,
                };

                // No connection component at all means a path-shaping waypoint; a connection that
                // was cleared (its stop bulldozed) leaves the same empty intent behind.
                if (EntityManager.HasComponent<Connected>(waypointEntity))
                {
                    Entity stop =
                        EntityManager.GetComponentData<Connected>(waypointEntity).m_Connected;
                    if (stop != Entity.Null && !TryCaptureStopIdentity(stop, ref value))
                        return false;
                }
                captured[i] = value;
            }
            result = captured;
            return true;
        }

        private bool TryCaptureStopIdentity(Entity stop, ref RouteWaypointIntent value)
        {
            if (!EntityManager.Exists(stop) ||
                !EntityManager.HasComponent<PrefabRef>(stop) ||
                !EntityManager.HasComponent<global::Game.Objects.Transform>(stop))
                return false;

            Entity stopPrefab = EntityManager.GetComponentData<PrefabRef>(stop).m_Prefab;
            string stopName = _prefabSystem.GetPrefabName(stopPrefab);
            if (string.IsNullOrEmpty(stopName)) return false;
            global::Game.Objects.Transform stopTransform =
                EntityManager.GetComponentData<global::Game.Objects.Transform>(stop);
            value.StopPrefabName = stopName;
            value.StopX = stopTransform.m_Position.x;
            value.StopY = stopTransform.m_Position.y;
            value.StopZ = stopTransform.m_Position.z;

            // The owner only disambiguates identical platforms of one station. A stop that has none,
            // or whose chain cannot be walked, still has a usable prefab-and-position identity.
            Entity topOwner;
            if (!TryFindTopOwner(stop, out topOwner) || topOwner == Entity.Null ||
                !EntityManager.HasComponent<PrefabRef>(topOwner) ||
                !EntityManager.HasComponent<global::Game.Objects.Transform>(topOwner))
                return true;

            string ownerName = _prefabSystem.GetPrefabName(
                EntityManager.GetComponentData<PrefabRef>(topOwner).m_Prefab);
            if (string.IsNullOrEmpty(ownerName)) return true;
            global::Game.Objects.Transform ownerTransform =
                EntityManager.GetComponentData<global::Game.Objects.Transform>(topOwner);
            value.OwnerPrefabName = ownerName;
            value.OwnerX = ownerTransform.m_Position.x;
            value.OwnerY = ownerTransform.m_Position.y;
            value.OwnerZ = ownerTransform.m_Position.z;
            return true;
        }

        private bool TryFindTopOwner(Entity entity, out Entity topOwner)
        {
            topOwner = Entity.Null;
            Entity cursor = entity;
            for (int depth = 0; depth < 64 && EntityManager.HasComponent<Owner>(cursor); depth++)
            {
                Entity next = EntityManager.GetComponentData<Owner>(cursor).m_Owner;
                if (next == Entity.Null || next == cursor || !EntityManager.Exists(next))
                    return false;
                topOwner = next;
                cursor = next;
            }
            return cursor == entity || !EntityManager.HasComponent<Owner>(cursor);
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
                    string name = _prefabSystem.GetPrefabName(
                        EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab);
                    if (string.IsNullOrEmpty(name)) continue;

                    RouteSnapshot snapshot;
                    if (!TryCaptureSnapshot(entity, out snapshot))
                    {
                        if (!_baselinePendingRoutes.Contains(entity))
                            _needsCreateCapture.Add(entity);
                        continue;
                    }

                    if (_baselinePendingRoutes.Remove(entity))
                    {
                        _needsCreateCapture.Remove(entity);
                        _knownRoutes[entity] = snapshot;
                        continue;
                    }
                    RouteSnapshot known;
                    if (_knownRoutes.TryGetValue(entity, out known) &&
                        SnapshotsEqual(known, snapshot))
                    {
                        _needsCreateCapture.Remove(entity);
                        continue;
                    }
                    _needsCreateCapture.Remove(entity);
                    PublishCreate(session, entity, name, snapshot, now);
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
                    string name = _prefabSystem.GetPrefabName(
                        EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab);
                    if (string.IsNullOrEmpty(name)) continue;

                    // Owned waypoint entities remain readable through ModificationEnd.
                    float3 first;
                    int routeNumber;
                    if (EntityManager.HasBuffer<RouteWaypoint>(entity))
                    {
                        DynamicBuffer<RouteWaypoint> waypoints =
                            EntityManager.GetBuffer<RouteWaypoint>(entity, isReadOnly: true);
                        if (waypoints.Length != 0 &&
                            EntityManager.HasComponent<Position>(waypoints[0].m_Waypoint))
                        {
                            first = EntityManager
                                .GetComponentData<Position>(waypoints[0].m_Waypoint).m_Position;
                            routeNumber = RouteNumberOf(entity);
                        }
                        else if (!TryGetDeleteFallback(entity, out first, out routeNumber))
                            continue;
                    }
                    else if (!TryGetDeleteFallback(entity, out first, out routeNumber))
                        continue;

                    bool guarded = _guard.Consume(
                        RouteKey("routedel", name, routeNumber, first), now);
                    guarded |= _guard.Consume(
                        RouteKey("routedel", name, 0, first), now);
                    if (!guarded)
                    {
                        var command = new RouteDeleteCommand
                        {
                            PrefabName = name,
                            RouteNumber = routeNumber,
                            WaypointX = first.x,
                            WaypointY = first.y,
                            WaypointZ = first.z,
                        };
                        session.SendCommand(0, RouteDeleteCommand.Id, command.Encode());
                    }
                    _needsCreateCapture.Remove(entity);
                    _baselinePendingRoutes.Remove(entity);
                    _knownRoutes.Remove(entity);
                }
            }
            finally
            {
                entities.Dispose();
            }
        }

        private void PublishCreate(MultiplayerSession session, Entity entity, string name,
            RouteSnapshot snapshot, long now)
        {
            float3 first = WaypointPosition(snapshot.Waypoints[0]);
            bool guarded = _guard.Consume(
                RouteKey("route", name, snapshot.RouteNumber, first), now);
            guarded |= _guard.Consume(
                RouteShapeKey("route", name, snapshot.Waypoints), now);
            guarded |= MatchesPendingCreate(name, snapshot.Waypoints);
            if (!guarded)
            {
                var command = new RouteCreateCommand
                {
                    PrefabName = name,
                    RouteNumber = snapshot.RouteNumber,
                    IsComplete = snapshot.IsComplete,
                    ColorR = (byte)snapshot.Rgba,
                    ColorG = (byte)(snapshot.Rgba >> 8),
                    ColorB = (byte)(snapshot.Rgba >> 16),
                    ColorA = (byte)(snapshot.Rgba >> 24),
                    Waypoints = snapshot.Waypoints,
                };
                session.SendCommand(0, RouteCreateCommand.Id, command.Encode());
                Mod.Verbose("[MP] RouteSync captured line '" + name + "' (" +
                            DescribeShape(snapshot.Waypoints) + ", number " +
                            snapshot.RouteNumber + ").");
            }
            _knownRoutes[entity] = snapshot;
        }

        private bool MatchesPendingCreate(string prefabName,
            RouteWaypointIntent[] waypoints)
        {
            for (int i = 0; i < _pendingCreateMetadata.Count; i++)
            {
                PendingCreateMetadata pending = _pendingCreateMetadata[i];
                if (string.Equals(pending.PrefabName, prefabName,
                        StringComparison.Ordinal) &&
                    WaypointsMatchIntent(waypoints, pending.Waypoints))
                    return true;
            }
            return false;
        }

        private bool TryGetDeleteFallback(Entity entity, out float3 first,
            out int routeNumber)
        {
            RouteSnapshot snapshot;
            if (_knownRoutes.TryGetValue(entity, out snapshot) &&
                snapshot.Waypoints != null && snapshot.Waypoints.Length != 0)
            {
                first = WaypointPosition(snapshot.Waypoints[0]);
                routeNumber = snapshot.RouteNumber;
                return true;
            }
            first = default(float3);
            routeNumber = 0;
            return false;
        }

        private static bool SnapshotsEqual(RouteSnapshot a, RouteSnapshot b)
        {
            return a.RouteNumber == b.RouteNumber &&
                   a.IsComplete == b.IsComplete &&
                   a.Rgba == b.Rgba &&
                   WaypointsEqual(a.Waypoints, b.Waypoints);
        }

        private static bool WaypointsEqual(RouteWaypointIntent[] a, RouteWaypointIntent[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (math.distancesq(WaypointPosition(a[i]), WaypointPosition(b[i])) > 0.01f)
                    return false;
                if (!string.Equals(a[i].StopPrefabName, b[i].StopPrefabName,
                        StringComparison.Ordinal) ||
                    !string.Equals(a[i].OwnerPrefabName, b[i].OwnerPrefabName,
                        StringComparison.Ordinal))
                    return false;
                if (!string.IsNullOrEmpty(a[i].StopPrefabName) &&
                    math.distancesq(StopPosition(a[i]), StopPosition(b[i])) > 0.01f)
                    return false;
                if (!string.IsNullOrEmpty(a[i].OwnerPrefabName) &&
                    math.distancesq(OwnerPosition(a[i]), OwnerPosition(b[i])) > 0.01f)
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Compares a source-world intent with a receiver-world snapshot. Connected objects can
        /// differ slightly in position after independent terrain/object realization, so this uses
        /// the same bounded tolerances as stop resolution while retaining exact prefab identity.
        /// The owner is only a disambiguator: it is compared when both sides recorded one.
        /// </summary>
        private static bool WaypointsMatchIntent(RouteWaypointIntent[] local,
            RouteWaypointIntent[] intent)
        {
            if (local == null || intent == null || local.Length != intent.Length)
                return false;
            for (int i = 0; i < local.Length; i++)
            {
                bool hasStop = !string.IsNullOrEmpty(intent[i].StopPrefabName);
                if (hasStop != !string.IsNullOrEmpty(local[i].StopPrefabName)) return false;
                if (hasStop && !string.Equals(local[i].StopPrefabName,
                        intent[i].StopPrefabName, StringComparison.Ordinal))
                    return false;

                if (!hasStop)
                {
                    if (math.distancesq(WaypointPosition(local[i]),
                            WaypointPosition(intent[i])) > FreeWaypointMatchDistanceSq)
                        return false;
                    continue;
                }

                if (!StopPositionsMatch(WaypointPosition(local[i]),
                        WaypointPosition(intent[i])) ||
                    !StopPositionsMatch(StopPosition(local[i]), StopPosition(intent[i])))
                    return false;
                if (string.IsNullOrEmpty(local[i].OwnerPrefabName) ||
                    string.IsNullOrEmpty(intent[i].OwnerPrefabName))
                    continue;
                if (!string.Equals(local[i].OwnerPrefabName,
                        intent[i].OwnerPrefabName, StringComparison.Ordinal) ||
                    !OwnerPositionsMatch(OwnerPosition(local[i]), OwnerPosition(intent[i])))
                    return false;
            }
            return true;
        }

        private uint ColorOf(Entity route)
        {
            if (!EntityManager.HasComponent<Color>(route)) return 0;
            UnityEngine.Color32 c = EntityManager.GetComponentData<Color>(route).m_Color;
            return (uint)(c.r | (c.g << 8) | (c.b << 16) | (c.a << 24));
        }

        private int RouteNumberOf(Entity route) =>
            EntityManager.HasComponent<RouteNumber>(route)
                ? EntityManager.GetComponentData<RouteNumber>(route).m_Number
                : 0;

        private static float3 WaypointPosition(RouteWaypointIntent waypoint) =>
            new float3(waypoint.X, waypoint.Y, waypoint.Z);

        private static float3 StopPosition(RouteWaypointIntent waypoint) =>
            new float3(waypoint.StopX, waypoint.StopY, waypoint.StopZ);

        private static float3 OwnerPosition(RouteWaypointIntent waypoint) =>
            new float3(waypoint.OwnerX, waypoint.OwnerY, waypoint.OwnerZ);

        /// <summary>
        /// Content comparison catches edits that do not reliably surface as Created/Deleted:
        /// waypoint/stop changes, recolors, completion changes, and line renumbering.
        /// </summary>
        private void ScanForEdits(MultiplayerSession session, long now)
        {
            if (now - _lastEditScanMs < EditScanIntervalMs) return;
            _lastEditScanMs = now;

            if (_needsCreateCapture.Count != 0)
                _needsCreateCapture.RemoveWhere(entity =>
                    !EntityManager.Exists(entity) ||
                    EntityManager.HasComponent<Deleted>(entity));
            if (_baselinePendingRoutes.Count != 0)
                _baselinePendingRoutes.RemoveWhere(entity =>
                    !EntityManager.Exists(entity) ||
                    EntityManager.HasComponent<Deleted>(entity));
            _nextRoutes.Clear();
            NativeArray<Entity> entities = _liveRoutes.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity entity = entities[i];
                    RouteSnapshot snapshot;
                    if (!TryCaptureSnapshot(entity, out snapshot))
                    {
                        RouteSnapshot retained;
                        if (_knownRoutes.TryGetValue(entity, out retained))
                            _nextRoutes[entity] = retained;
                        continue;
                    }

                    if (_pendingUpdateCommit != null &&
                        _pendingUpdateCommit.Route == entity &&
                        IsExpectedPendingUpdateState(snapshot, _pendingUpdateCommit))
                    {
                        RouteSnapshot retained;
                        if (_knownRoutes.TryGetValue(entity, out retained))
                            _nextRoutes[entity] = retained;
                        continue;
                    }

                    if (_baselinePendingRoutes.Remove(entity))
                    {
                        _needsCreateCapture.Remove(entity);
                        _nextRoutes[entity] = snapshot;
                        continue;
                    }

                    // A route that became readable only now is new to this world only if we are not
                    // already tracking it: a line realized from a remote command is finalized into
                    // the known set, and republishing it would echo it back to its author.
                    if (_needsCreateCapture.Remove(entity) && !_knownRoutes.ContainsKey(entity))
                    {
                        string delayedName = _prefabSystem.GetPrefabName(
                            EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab);
                        if (!string.IsNullOrEmpty(delayedName))
                            PublishCreate(session, entity, delayedName, snapshot, now);
                    }

                    RouteSnapshot old;
                    bool had = _knownRoutes.TryGetValue(entity, out old);
                    _nextRoutes[entity] = snapshot;
                    if (!had || SnapshotsEqual(old, snapshot)) continue;

                    string name = _prefabSystem.GetPrefabName(
                        EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab);
                    if (string.IsNullOrEmpty(name)) continue;
                    float3 first = WaypointPosition(snapshot.Waypoints[0]);
                    bool guarded = _guard.Consume(
                        RouteKey("routeupd", name, snapshot.RouteNumber, first), now);
                    guarded |= _guard.Consume(
                        RouteShapeKey("routeupd", name, snapshot.Waypoints), now);
                    if (guarded) continue;

                    var command = new RouteUpdateCommand
                    {
                        PrefabName = name,
                        AnchorX = old.Waypoints[0].X,
                        AnchorY = old.Waypoints[0].Y,
                        AnchorZ = old.Waypoints[0].Z,
                        AnchorRouteNumber = old.RouteNumber,
                        RouteNumber = snapshot.RouteNumber,
                        IsComplete = snapshot.IsComplete,
                        ColorR = (byte)snapshot.Rgba,
                        ColorG = (byte)(snapshot.Rgba >> 8),
                        ColorB = (byte)(snapshot.Rgba >> 16),
                        ColorA = (byte)(snapshot.Rgba >> 24),
                        Waypoints = snapshot.Waypoints,
                    };
                    session.SendCommand(0, RouteUpdateCommand.Id, command.Encode());
                    Mod.Verbose("[MP] RouteSync captured edit of line '" + name + "' (" +
                                DescribeShape(snapshot.Waypoints) + ", number " +
                                snapshot.RouteNumber + ").");
                }
            }
            finally
            {
                entities.Dispose();
            }

            Dictionary<Entity, RouteSnapshot> swap = _knownRoutes;
            _knownRoutes = _nextRoutes;
            _nextRoutes = swap;
        }

        private static bool IsExpectedPendingUpdateState(RouteSnapshot snapshot,
            PendingUpdateCommit pending)
        {
            bool expectedGraph =
                WaypointsMatchIntent(snapshot.Waypoints, pending.Original.Waypoints) ||
                WaypointsMatchIntent(snapshot.Waypoints, pending.Desired.Waypoints);
            bool expectedCompletion =
                snapshot.IsComplete == pending.Original.IsComplete ||
                snapshot.IsComplete == pending.Desired.IsComplete;
            return expectedGraph && expectedCompletion &&
                   snapshot.RouteNumber == pending.Desired.RouteNumber &&
                   snapshot.Rgba == pending.Desired.Rgba;
        }
    }
}
