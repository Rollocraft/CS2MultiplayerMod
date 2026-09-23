using System;
using Game.Prefabs;
using Game.Routes;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.Infrastructure;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    public partial class RouteSyncSystem
    {
        /// <summary>A line that serves no stop is a truncated graph.</summary>
        private bool ValidateRouteContract(Entity routePrefab,
            RouteWaypointIntent[] waypoints, string prefabName)
        {
            if (waypoints == null || waypoints.Length < 2 ||
                waypoints.Length > RouteCreateCommand.MaxWaypoints)
            {
                SyncInbox.RequestResync(CS2MultiplayerMod.Game.Diagnostics.ResyncReport
                    .Create("invalid route topology", "route",
                        CS2MultiplayerMod.Game.Diagnostics.ResyncEvidence.Contradiction)
                    .About("line topology")
                    .Tried("nothing - the described line does not form a valid route here"));
                return false;
            }

            if (!EntityManager.HasComponent<TransportLineData>(routePrefab)) return true;
            for (int i = 0; i < waypoints.Length; i++)
                if (!string.IsNullOrEmpty(waypoints[i].StopPrefabName))
                    return true;

            SyncInbox.RequestResync(CS2MultiplayerMod.Game.Diagnostics.ResyncReport
                .Create("public transport route without any stop rejected", "route",
                    CS2MultiplayerMod.Game.Diagnostics.ResyncEvidence.Contradiction)
                .About("line with no stops")
                .Tried("nothing - a public transport line with no stops cannot be created"));
            SyncLog.Warn(LogTopic.Routes, "RouteSync rejected public-transport line '" + prefabName +
                "' because none of its waypoints is connected to a stop.");
            return false;
        }

        /// <summary>
        /// Maps each waypoint to a live local stop. A connected waypoint takes its stop's own transform,
        /// as the route tool records it.
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
                    // Prefabs can share a name, so check each candidate's own prefab.
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

        /// <summary>A stop that does not serve the line's purpose is a validation error in the game.</summary>
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
            if (_prefabNames.TryGetValue(prefab, out string name)) return name;
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

            if (!TryFindTopOwner(stop, out Entity topOwner) || topOwner == Entity.Null ||
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

            // Generation compares the closing position exactly: repeat the same value.
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
    }
}
