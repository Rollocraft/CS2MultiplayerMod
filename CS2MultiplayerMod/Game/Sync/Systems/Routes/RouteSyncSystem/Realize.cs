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
using CS2MultiplayerMod.Game.Sync.Infrastructure;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    public partial class RouteSyncSystem
    {
        // Horizontal identity stays tight, because distinct stops of one prefab are metres apart.
        // The vertical band is wide: two machines can hold the same stop at different heights after
        // independent terrain grading, and a stacked platform is still separated horizontally.
        private const float StopMatchRadiusSq = 16f;
        private const float StopMatchHeight = 10f;
        private const float OwnerMatchRadiusSq = 64f;
        private const float OwnerMatchHeight = 20f;
        private const float FreeWaypointMatchDistanceSq = 0.25f;
        private const float RouteAnchorMatchDistanceSq = 256f;

        private static bool StopPositionsMatch(float3 a, float3 b) =>
            math.distancesq(a.xz, b.xz) <= StopMatchRadiusSq &&
            math.abs(a.y - b.y) <= StopMatchHeight;

        private static bool OwnerPositionsMatch(float3 a, float3 b) =>
            math.distancesq(a.xz, b.xz) <= OwnerMatchRadiusSq &&
            math.abs(a.y - b.y) <= OwnerMatchHeight;

        private RealizeResult RealizeCreate(RouteCreateCommand command, int originPlayerId, long now)
        {
            if (_netSync == null || !_netSync.CanBuildDefinitions)
                return RealizeResult.Retry;

            Entity prefab;
            if (!_prefabIndex.TryResolve(command.PrefabName, out prefab))
            {
                SyncInbox.RequestResync("unknown route prefab during creation");
                Mod.log.Warn("[MP] RouteSync create: unknown prefab '" +
                             command.PrefabName + "'; skipping.");
                return RealizeResult.Rejected;
            }
            if (!ValidateRouteContract(prefab, command.Waypoints, command.PrefabName))
                return RealizeResult.Rejected;

            for (int i = 0; i < _pendingCreateMetadata.Count; i++)
            {
                PendingCreateMetadata pending = _pendingCreateMetadata[i];
                if (!string.Equals(pending.PrefabName, command.PrefabName,
                        StringComparison.Ordinal))
                    continue;
                bool sameShape = WaypointsMatchIntent(pending.Waypoints,
                    command.Waypoints);
                if (pending.RouteNumber == command.RouteNumber && sameShape)
                    return RealizeResult.Applied;
                if (command.RouteNumber > 0 &&
                    pending.RouteNumber == command.RouteNumber)
                {
                    SyncInbox.RequestResync("pending route number conflict");
                    Mod.log.Warn("[MP] RouteSync create: two different pending lines claim number " +
                                 command.RouteNumber + " for '" + command.PrefabName + "'.");
                    return RealizeResult.Rejected;
                }
                // Two distinct lines may legitimately use the same stops. Serialize that shape so
                // the newly generated route can be distinguished from the already-finalized one.
                if (sameShape) return RealizeResult.Retry;
            }

            bool numberConflict;
            Entity existing = FindExistingCreate(prefab, command.RouteNumber,
                command.Waypoints, out numberConflict);
            if (numberConflict)
            {
                SyncInbox.RequestResync("route number conflict during creation");
                Mod.log.Warn("[MP] RouteSync create: route number " + command.RouteNumber +
                             " for '" + command.PrefabName +
                             "' already belongs to a different line; requested a fresh world sync.");
                return RealizeResult.Rejected;
            }
            if (existing != Entity.Null)
            {
                if (_mutatedRoutesThisFrame.Contains(existing))
                    return RealizeResult.Retry;
                _mutatedRoutesThisFrame.Add(existing);
                if (!TryApplyMetadata(existing, prefab, command.RouteNumber,
                        PackColor(command.ColorR, command.ColorG, command.ColorB, command.ColorA)))
                {
                    SyncInbox.RequestResync("route metadata conflict during idempotent creation");
                    return RealizeResult.Rejected;
                }
                MarkCreateGuards(command, now);
                return RealizeResult.Applied;
            }

            Entity[] connections;
            float3[] positions;
            if (!TryResolveConnections(prefab, command.Waypoints, out connections,
                    out positions, out _lastRealizeFailure))
                return RealizeResult.Retry;
            if (_pendingCreateMetadata.Count >= MaxPendingCommands)
                return RealizeResult.Retry;
            HashSet<Entity> preexistingShapeMatches =
                CaptureShapeMatches(prefab, command.Waypoints);

            Entity definition = Entity.Null;
            PendingCreateMetadata metadata = null;
            bool commitArmed = false;
            try
            {
                _netSync.PrepareDefinitionFrame();
                definition = EntityManager.CreateEntity();
                EntityManager.AddComponentData(definition, new CreationDefinition
                {
                    m_Prefab = prefab,
                    m_RandomSeed = 0,
                    m_Flags = CreationFlags.Permanent,
                });
                AddWaypointDefinitions(definition, connections, positions,
                    Entity.Null, appendClosure: command.IsComplete);
                EntityManager.AddComponentData(definition, new ColorDefinition
                {
                    m_Color = new UnityEngine.Color32(command.ColorR, command.ColorG,
                        command.ColorB, command.ColorA),
                });
                EntityManager.AddComponent<Updated>(definition);
                EntityManager.AddComponent<Deleted>(definition);

                metadata = new PendingCreateMetadata
                {
                    Prefab = prefab,
                    PrefabName = command.PrefabName,
                    Waypoints = command.Waypoints,
                    PreexistingShapeMatches = preexistingShapeMatches,
                    RouteNumber = command.RouteNumber,
                    Rgba = PackColor(command.ColorR, command.ColorG,
                        command.ColorB, command.ColorA),
                    DeadlineMs = now + RetryWindowMs,
                    Source = command,
                    OriginPlayerId = originPlayerId,
                };
                _pendingCreateMetadata.Add(metadata);
                commitArmed = _netSync.ArmRouteCommit(
                        () => ReplayCreateAfterCommitLoss(metadata),
                        () => CompleteCreateCommit(metadata),
                        "create");
                if (!commitArmed)
                {
                    _pendingCreateMetadata.Remove(metadata);
                    EntityManager.DestroyEntity(definition);
                    _netSync.CancelPreparedDefinitionFrame();
                    return RealizeResult.Retry;
                }

                MarkCreateGuards(command, now);
                Diagnostics.FlightRecorder.Note("route create definition armed " +
                                                  DescribeShape(command.Waypoints));
                Mod.Verbose("[MP] RouteSync create: submitted line '" +
                            command.PrefabName + "' (" + DescribeShape(command.Waypoints) +
                            ", number " + command.RouteNumber + ") from player " +
                            originPlayerId + ".");
                return RealizeResult.Applied;
            }
            catch (Exception ex)
            {
                if (!commitArmed)
                {
                    if (metadata != null) _pendingCreateMetadata.Remove(metadata);
                    if (definition != Entity.Null && EntityManager.Exists(definition))
                        EntityManager.DestroyEntity(definition);
                    if (_netSync != null) _netSync.CancelPreparedDefinitionFrame();
                }
                SyncInbox.RequestResync("route creation failed");
                Mod.log.Error("[MP] RouteSync create FAILED for '" +
                              command.PrefabName + "': " + ex);
                return RealizeResult.Rejected;
            }
        }

        private RealizeResult RealizeUpdate(RouteUpdateCommand command, int originPlayerId, long now)
        {
            Entity prefab;
            if (!_prefabIndex.TryResolve(command.PrefabName, out prefab))
            {
                SyncInbox.RequestResync("unknown route prefab during update");
                Mod.log.Warn("[MP] RouteSync update: unknown prefab '" +
                             command.PrefabName + "'; skipping.");
                return RealizeResult.Rejected;
            }
            if (!ValidateRouteContract(prefab, command.Waypoints, command.PrefabName))
                return RealizeResult.Rejected;

            bool ambiguous;
            Entity route = FindRoute(prefab, command.AnchorRouteNumber,
                new float3(command.AnchorX, command.AnchorY, command.AnchorZ),
                RouteAnchorMatchDistanceSq, out ambiguous);
            if (route == Entity.Null)
            {
                if (ambiguous)
                    Mod.Verbose("[MP] RouteSync update: multiple local candidates for '" +
                                command.PrefabName + "' number " +
                                command.AnchorRouteNumber +
                                "; waiting instead of editing the wrong line.");
                return RealizeResult.Retry;
            }
            if (_mutatedRoutesThisFrame.Contains(route))
                return RealizeResult.Retry;

            Entity[] connections;
            float3[] positions;
            if (!TryResolveConnections(prefab, command.Waypoints, out connections,
                    out positions, out _lastRealizeFailure))
                return RealizeResult.Retry;

            RouteSnapshot local;
            if (!TryCaptureSnapshot(route, out local)) return RealizeResult.Retry;
            uint rgba = PackColor(command.ColorR, command.ColorG,
                command.ColorB, command.ColorA);
            if (!RouteNumberAvailable(route, prefab, command.RouteNumber))
            {
                SyncInbox.RequestResync("route number conflict during update");
                Mod.log.Warn("[MP] RouteSync update: requested number " +
                             command.RouteNumber + " is already in use for '" +
                             command.PrefabName + "'.");
                return RealizeResult.Rejected;
            }
            _mutatedRoutesThisFrame.Add(route);

            bool rebuildGraph = !RouteGraphMatches(route, connections, positions) ||
                                local.IsComplete != command.IsComplete;
            Entity definition = Entity.Null;
            PendingUpdateCommit pendingCommit = null;
            bool commitArmed = false;
            try
            {
                if (rebuildGraph)
                {
                    pendingCommit = new PendingUpdateCommit
                    {
                        Route = route,
                        Source = command,
                        OriginPlayerId = originPlayerId,
                        DeadlineMs = now + RetryWindowMs,
                        Original = local,
                        Desired = new RouteSnapshot
                        {
                            Waypoints = command.Waypoints,
                            Rgba = rgba,
                            RouteNumber = command.RouteNumber,
                            IsComplete = command.IsComplete,
                        },
                    };
                    _pendingUpdateCommit = pendingCommit;
                    _netSync.PrepareDefinitionFrame();
                    definition = EntityManager.CreateEntity();
                    EntityManager.AddComponentData(definition, new CreationDefinition
                    {
                        m_Prefab = prefab,
                        m_Original = route,
                        m_RandomSeed = 0,
                        m_Flags = CreationFlags.Permanent,
                    });

                    // Modified routes already close their last segment back to index zero. Only a
                    // brand-new route uses a repeated first definition as the completion signal.
                    AddWaypointDefinitions(definition, connections, positions,
                        route, appendClosure: false);
                    EntityManager.AddComponent<Updated>(definition);
                    EntityManager.AddComponent<Deleted>(definition);

                    commitArmed = _netSync.ArmRouteCommit(
                            () => ReplayUpdateAfterCommitLoss(pendingCommit),
                            () => CompleteUpdateCommit(pendingCommit),
                            "update");
                    if (!commitArmed)
                    {
                        EntityManager.DestroyEntity(definition);
                        _netSync.CancelPreparedDefinitionFrame();
                        _pendingUpdateCommit = null;
                        return RealizeResult.Retry;
                    }
                }

                // GenerateRoutesSystem retains the original route color during an edit, so metadata
                // is applied explicitly even when the waypoint graph is rebuilt in the same frame.
                if (!TryApplyMetadata(route, prefab, command.RouteNumber, rgba))
                {
                    SyncInbox.RequestResync("route number conflict during update");
                    Mod.log.Warn("[MP] RouteSync update: requested number " +
                                 command.RouteNumber + " is already in use for '" +
                                 command.PrefabName + "'.");
                    return RealizeResult.Rejected;
                }

                float3 first = WaypointPosition(command.Waypoints[0]);
                _guard.Mark(RouteKey("routeupd", command.PrefabName,
                    command.RouteNumber, first), now);
                _guard.Mark(RouteShapeKey("routeupd", command.PrefabName,
                    command.Waypoints), now);
                if (!rebuildGraph)
                {
                    // Record what this world actually holds, not what was asked for: its waypoints
                    // sit on its own stops, and a synthesized snapshot would read as a local edit
                    // on the next scan.
                    RouteSnapshot applied;
                    _knownRoutes[route] = TryCaptureSnapshot(route, out applied)
                        ? applied
                        : new RouteSnapshot
                        {
                            Waypoints = command.Waypoints,
                            Rgba = rgba,
                            RouteNumber = command.RouteNumber,
                            IsComplete = command.IsComplete,
                        };
                }
                else
                {
                    Diagnostics.FlightRecorder.Note("route update definition armed " +
                                                      DescribeShape(command.Waypoints));
                }
                Mod.Verbose("[MP] RouteSync update: applied line '" +
                            command.PrefabName + "' (" + DescribeShape(command.Waypoints) +
                            ", number " + command.RouteNumber + ") from player " +
                            originPlayerId + ".");
                return RealizeResult.Applied;
            }
            catch (Exception ex)
            {
                if (!commitArmed)
                {
                    if (pendingCommit != null && _pendingUpdateCommit == pendingCommit)
                        _pendingUpdateCommit = null;
                    if (definition != Entity.Null && EntityManager.Exists(definition))
                        EntityManager.DestroyEntity(definition);
                    if (_netSync != null) _netSync.CancelPreparedDefinitionFrame();
                    TryApplyMetadata(route, prefab, local.RouteNumber, local.Rgba);
                }
                SyncInbox.RequestResync("route update failed");
                Mod.log.Error("[MP] RouteSync update FAILED for '" +
                              command.PrefabName + "': " + ex);
                return RealizeResult.Rejected;
            }
        }

        private RealizeResult RealizeDelete(RouteDeleteCommand command, long now)
        {
            Entity prefab;
            if (!_prefabIndex.TryResolve(command.PrefabName, out prefab))
            {
                SyncInbox.RequestResync("unknown route prefab during deletion");
                Mod.log.Warn("[MP] RouteSync delete: unknown prefab '" +
                             command.PrefabName + "'; skipping.");
                return RealizeResult.Rejected;
            }

            float3 first = new float3(command.WaypointX, command.WaypointY,
                command.WaypointZ);
            bool ambiguous;
            Entity route = FindRoute(prefab, command.RouteNumber, first,
                RouteAnchorMatchDistanceSq, out ambiguous);
            if (route == Entity.Null)
            {
                if (ambiguous)
                    Mod.Verbose("[MP] RouteSync delete: multiple local candidates for '" +
                                command.PrefabName + "' number " + command.RouteNumber +
                                "; waiting instead of deleting the wrong line.");
                return RealizeResult.Retry;
            }
            if (_mutatedRoutesThisFrame.Contains(route))
                return RealizeResult.Retry;
            _mutatedRoutesThisFrame.Add(route);

            _guard.Mark(RouteKey("routedel", command.PrefabName,
                command.RouteNumber, first), now);
            _guard.Mark(RouteKey("routedel", command.PrefabName, 0, first), now);
            if (!EntityManager.HasComponent<Deleted>(route))
                EntityManager.AddComponent<Deleted>(route);
            _knownRoutes.Remove(route);
            Mod.Verbose("[MP] RouteSync deleted line '" + command.PrefabName +
                        "' number " + command.RouteNumber + ".");
            return RealizeResult.Applied;
        }

        /// <summary>
        /// A line may carry waypoints that only shape its path, but it is meaningless - and a sign
        /// of a truncated graph - if it serves no stop at all.
        /// </summary>
        private bool ValidateRouteContract(Entity routePrefab,
            RouteWaypointIntent[] waypoints, string prefabName)
        {
            if (waypoints == null || waypoints.Length < 2 ||
                waypoints.Length > RouteCreateCommand.MaxWaypoints)
            {
                SyncInbox.RequestResync("invalid route topology");
                return false;
            }

            if (!EntityManager.HasComponent<TransportLineData>(routePrefab)) return true;
            for (int i = 0; i < waypoints.Length; i++)
                if (!string.IsNullOrEmpty(waypoints[i].StopPrefabName))
                    return true;

            SyncInbox.RequestResync("public transport route without any stop rejected");
            Mod.log.Warn("[MP] RouteSync rejected public-transport line '" + prefabName +
                         "' because none of its waypoints is connected to a stop.");
            return false;
        }

        /// <summary>
        /// Maps each waypoint's portable stop identity onto a live local stop. Also returns the
        /// waypoint positions to submit: a connected waypoint takes its resolved stop's own
        /// transform, which is what the route tool records for a locally drawn line.
        /// </summary>
        private bool TryResolveConnections(Entity routePrefab,
            RouteWaypointIntent[] waypoints, out Entity[] result, out float3[] positions,
            out string failure)
        {
            failure = null;
            result = new Entity[waypoints.Length];
            positions = new float3[waypoints.Length];
            bool needsStops = false;
            for (int i = 0; i < waypoints.Length; i++)
            {
                positions[i] = WaypointPosition(waypoints[i]);
                needsStops |= !string.IsNullOrEmpty(waypoints[i].StopPrefabName);
            }
            if (!needsStops) return true;

            TransportLineData lineData = default(TransportLineData);
            bool hasLineData = EntityManager.HasComponent<TransportLineData>(routePrefab);
            if (hasLineData)
                lineData = EntityManager.GetComponentData<TransportLineData>(routePrefab);

            NativeArray<Entity> stops = _transportStops.ToEntityArray(Allocator.Temp);
            try
            {
                var stopNames = new string[stops.Length];
                var stopServes = new bool[stops.Length];
                var stopPositions = new float3[stops.Length];
                for (int s = 0; s < stops.Length; s++)
                {
                    // Two prefabs can share a name, so identity is checked on each candidate's own
                    // prefab rather than by resolving the name to a single entity.
                    Entity candidatePrefab =
                        EntityManager.GetComponentData<PrefabRef>(stops[s]).m_Prefab;
                    stopNames[s] = PrefabNameOf(candidatePrefab);
                    stopServes[s] = !hasLineData || StopServesLine(candidatePrefab, lineData);
                    stopPositions[s] = EntityManager
                        .GetComponentData<global::Game.Objects.Transform>(stops[s]).m_Position;
                }

                for (int i = 0; i < waypoints.Length; i++)
                {
                    RouteWaypointIntent wanted = waypoints[i];
                    if (string.IsNullOrEmpty(wanted.StopPrefabName)) continue;

                    Entity best = Entity.Null;
                    float3 bestPosition = default(float3);
                    float bestScore = 0f;
                    bool bestOwnerMatch = false;
                    int sameName = 0;
                    int wrongPurpose = 0;
                    float nearest = float.MaxValue;
                    float3 wantedPosition = StopPosition(wanted);

                    for (int s = 0; s < stops.Length; s++)
                    {
                        Entity candidate = stops[s];
                        if (!string.Equals(stopNames[s], wanted.StopPrefabName,
                                StringComparison.Ordinal))
                            continue;
                        sameName++;
                        if (!stopServes[s])
                        {
                            wrongPurpose++;
                            continue;
                        }

                        float3 candidatePosition = stopPositions[s];
                        nearest = math.min(nearest,
                            math.distance(candidatePosition, wantedPosition));
                        if (!StopPositionsMatch(candidatePosition, wantedPosition)) continue;

                        // The owner is the only thing separating identical platforms of one
                        // station, so an owner-identified candidate outranks an anonymous one.
                        bool ownerMatch = StopOwnerMatches(candidate, wanted);
                        float score = math.distancesq(candidatePosition, wantedPosition);
                        bool better = best == Entity.Null ||
                                      (ownerMatch && !bestOwnerMatch) ||
                                      (ownerMatch == bestOwnerMatch && score < bestScore);
                        if (!better) continue;
                        best = candidate;
                        bestPosition = candidatePosition;
                        bestScore = score;
                        bestOwnerMatch = ownerMatch;
                    }

                    if (best == Entity.Null)
                    {
                        failure = "waypoint " + i + " found no live '" + wanted.StopPrefabName +
                                  "' stop near " + Describe(wantedPosition) + " (" + sameName +
                                  " with that name" +
                                  (wrongPurpose != 0
                                      ? ", " + wrongPurpose + " not serving this line"
                                      : string.Empty) +
                                  (nearest < float.MaxValue
                                      ? ", nearest " + nearest.ToString("0.0") + " m)"
                                      : ")");
                        return false;
                    }
                    result[i] = best;
                    positions[i] = bestPosition;
                }
                return true;
            }
            finally
            {
                stops.Dispose();
            }
        }

        /// <summary>
        /// A line whose own purpose the stop does not serve is a blocking validation error in the
        /// game, so a captured line never used one.
        /// </summary>
        private bool StopServesLine(Entity stopPrefab, TransportLineData lineData)
        {
            if (!EntityManager.HasComponent<TransportStopData>(stopPrefab)) return false;
            TransportStopData stopData =
                EntityManager.GetComponentData<TransportStopData>(stopPrefab);
            return stopData.m_TransportType == lineData.m_TransportType &&
                   (!lineData.m_PassengerTransport || stopData.m_PassengerTransport) &&
                   (!lineData.m_CargoTransport || stopData.m_CargoTransport);
        }

        private string PrefabNameOf(Entity prefab)
        {
            string name;
            if (_prefabNames.TryGetValue(prefab, out name)) return name;
            name = _prefabSystem.GetPrefabName(prefab) ?? string.Empty;
            _prefabNames[prefab] = name;
            return name;
        }

        private static string Describe(float3 position) =>
            "(" + position.x.ToString("0") + "," + position.y.ToString("0") + "," +
            position.z.ToString("0") + ")";

        private bool StopOwnerMatches(Entity stop, RouteWaypointIntent wanted)
        {
            if (string.IsNullOrEmpty(wanted.OwnerPrefabName)) return true;

            Entity topOwner;
            if (!TryFindTopOwner(stop, out topOwner) || topOwner == Entity.Null ||
                !EntityManager.HasComponent<PrefabRef>(topOwner) ||
                !EntityManager.HasComponent<global::Game.Objects.Transform>(topOwner))
                return false;
            string ownerName = PrefabNameOf(
                EntityManager.GetComponentData<PrefabRef>(topOwner).m_Prefab);
            if (!string.Equals(ownerName, wanted.OwnerPrefabName,
                    StringComparison.Ordinal))
                return false;
            float3 ownerPosition = EntityManager
                .GetComponentData<global::Game.Objects.Transform>(topOwner).m_Position;
            return OwnerPositionsMatch(ownerPosition, OwnerPosition(wanted));
        }

        private void AddWaypointDefinitions(Entity definition, Entity[] connections,
            float3[] positions, Entity originalRoute, bool appendClosure)
        {
            Entity[] originals = MatchOriginalWaypoints(originalRoute, connections, positions);
            DynamicBuffer<WaypointDefinition> buffer =
                EntityManager.AddBuffer<WaypointDefinition>(definition);
            for (int i = 0; i < positions.Length; i++)
            {
                buffer.Add(new WaypointDefinition
                {
                    m_Position = positions[i],
                    m_Connection = connections[i],
                    m_Original = originals[i],
                });
            }

            // Route generation reads a repeated first position as "this loop closes", and compares
            // it exactly - so the closing entry repeats the same value, never a recomputed one.
            if (appendClosure)
            {
                buffer.Add(new WaypointDefinition
                {
                    m_Position = positions[0],
                    m_Connection = connections[0],
                    m_Original = Entity.Null,
                });
            }
        }

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

        private void FinalizeCreatedRoutes(long now)
        {
            var claimed = new HashSet<Entity>();
            var ready = new Dictionary<PendingCreateMetadata, Entity>();
            for (int i = _pendingCreateMetadata.Count - 1; i >= 0; i--)
            {
                PendingCreateMetadata pending = _pendingCreateMetadata[i];
                if (!pending.GraphCommitted) continue;
                bool ambiguous;
                Entity route = FindMetadataTarget(pending, claimed, out ambiguous);
                if (route != Entity.Null)
                {
                    claimed.Add(route);
                    ready.Add(pending, route);
                    continue;
                }

                if (now < pending.DeadlineMs) continue;
                _pendingCreateMetadata.RemoveAt(i);
                SyncInbox.RequestResync(ambiguous
                    ? "ambiguous created route"
                    : "created route did not materialize");
                Mod.log.Warn("[MP] RouteSync could not finalize created line '" +
                             pending.PrefabName + "' number " + pending.RouteNumber +
                             "; requested a fresh world sync.");
            }

            // The game's initializer may temporarily give several routes created in one batch the
            // same free number. Treat every route finalized here as a coordinated assignment, while
            // still rejecting conflicts with established routes outside this batch.
            var readyRoutes = new HashSet<Entity>(ready.Values);
            foreach (KeyValuePair<PendingCreateMetadata, Entity> pair in ready)
            {
                PendingCreateMetadata pending = pair.Key;
                Entity route = pair.Value;
                _mutatedRoutesThisFrame.Add(route);
                if (!TryApplyMetadata(route, pending.Prefab,
                        pending.RouteNumber, pending.Rgba, readyRoutes))
                {
                    SyncInbox.RequestResync("route number conflict after creation");
                    Mod.log.Warn("[MP] RouteSync could not assign number " +
                                 pending.RouteNumber + " to '" + pending.PrefabName +
                                 "'; requested a fresh world sync.");
                }
                else
                {
                    RouteSnapshot snapshot;
                    if (TryCaptureSnapshot(route, out snapshot))
                        _knownRoutes[route] = snapshot;
                    Diagnostics.FlightRecorder.Note("route create finalized number=" +
                                                      pending.RouteNumber + " stops=" +
                                                      pending.Waypoints.Length);
                    Mod.Verbose("[MP] RouteSync finalized line '" +
                                pending.PrefabName + "' number " +
                                pending.RouteNumber + ".");
                }
                _pendingCreateMetadata.Remove(pending);
            }
        }

        private Entity FindMetadataTarget(PendingCreateMetadata pending,
            HashSet<Entity> claimed, out bool ambiguous)
        {
            ambiguous = false;
            Entity shape = Entity.Null;
            int shapeCount = 0;
            NativeArray<Entity> routes = _liveRoutes.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < routes.Length; i++)
                {
                    Entity candidate = routes[i];
                    if (claimed.Contains(candidate) ||
                        (pending.PreexistingShapeMatches != null &&
                         pending.PreexistingShapeMatches.Contains(candidate)) ||
                        EntityManager.GetComponentData<PrefabRef>(candidate).m_Prefab !=
                        pending.Prefab)
                        continue;
                    RouteSnapshot snapshot;
                    if (!TryCaptureSnapshot(candidate, out snapshot) ||
                        !WaypointsMatchIntent(snapshot.Waypoints,
                            pending.Waypoints))
                        continue;
                    shape = candidate;
                    shapeCount++;
                }
            }
            finally
            {
                routes.Dispose();
            }
            if (shapeCount == 1) return shape;
            ambiguous = shapeCount > 1;
            return Entity.Null;
        }

        private bool DeleteStillNeedsRecovery(RouteDeleteCommand command)
        {
            Entity prefab;
            if (!_prefabIndex.TryResolve(command.PrefabName, out prefab)) return true;
            bool ambiguous;
            Entity route = FindRoute(prefab, command.RouteNumber,
                new float3(command.WaypointX, command.WaypointY, command.WaypointZ),
                RouteAnchorMatchDistanceSq, out ambiguous);
            return ambiguous || route != Entity.Null;
        }

        private void CompleteCreateCommit(PendingCreateMetadata pending)
        {
            if (!_pendingCreateMetadata.Contains(pending)) return;
            pending.GraphCommitted = true;
            if (Mod.Service != null)
                pending.DeadlineMs = Mod.Service.NowMs + RetryWindowMs;
            Diagnostics.FlightRecorder.Note("route create graph committed; awaiting identity");
        }

        private void ReplayCreateAfterCommitLoss(PendingCreateMetadata pending)
        {
            if (!_pendingCreateMetadata.Remove(pending)) return;
            QueueCommitReplay(new PendingRouteCommand
            {
                Create = pending.Source,
                OriginPlayerId = pending.OriginPlayerId,
                DeadlineMs = pending.DeadlineMs,
            }, "create");
        }

        private void CompleteUpdateCommit(PendingUpdateCommit pending)
        {
            if (_pendingUpdateCommit != pending) return;
            _pendingUpdateCommit = null;

            RouteSnapshot snapshot;
            if (EntityManager.Exists(pending.Route) &&
                TryCaptureSnapshot(pending.Route, out snapshot))
                _knownRoutes[pending.Route] = snapshot;
            else
                _knownRoutes[pending.Route] = pending.Desired;
            Diagnostics.FlightRecorder.Note("route update graph committed");
        }

        private void ReplayUpdateAfterCommitLoss(PendingUpdateCommit pending)
        {
            if (_pendingUpdateCommit != pending) return;
            _pendingUpdateCommit = null;
            QueueCommitReplay(new PendingRouteCommand
            {
                Update = pending.Source,
                OriginPlayerId = pending.OriginPlayerId,
                DeadlineMs = pending.DeadlineMs,
            }, "update");
        }

        private void QueueCommitReplay(PendingRouteCommand command, string operation)
        {
            MultiplayerService service = Mod.Service;
            long now = service != null ? service.NowMs : 0;
            if (service == null || !service.GameplaySyncReady ||
                now >= command.DeadlineMs ||
                _pendingCommands.Count >= MaxPendingCommands)
            {
                SyncInbox.RequestResync("route " + operation +
                                         " commit could not be replayed");
                Mod.log.Warn("[MP] RouteSync " + operation +
                             " commit was lost and could not be replayed safely.");
                return;
            }

            command.NextAttemptMs = now;
            command.RetryDelayMs = InitialRetryDelayMs;
            _pendingCommands.Insert(0, command);
            Diagnostics.FlightRecorder.Note("route " + operation +
                                              " commit re-queued");
        }

        private void MarkCreateGuards(RouteCreateCommand command, long now)
        {
            float3 first = WaypointPosition(command.Waypoints[0]);
            _guard.Mark(RouteKey("route", command.PrefabName,
                command.RouteNumber, first), now);
            _guard.Mark(RouteShapeKey("route", command.PrefabName,
                command.Waypoints), now);
        }

        private static uint PackColor(byte r, byte g, byte b, byte a) =>
            (uint)(r | (g << 8) | (b << 16) | (a << 24));

        private static UnityEngine.Color32 UnpackColor(uint rgba) =>
            new UnityEngine.Color32((byte)rgba, (byte)(rgba >> 8),
                (byte)(rgba >> 16), (byte)(rgba >> 24));
    }
}
