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
    // Turning a command's waypoints into local connections. A stop is named by position and owner
    // rather than id, so each one is matched against what stands there now, and a route whose
    // prefab contract does not hold is refused rather than built wrong.
    public partial class RouteSyncSystem
    {
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
    }
}
