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
    // Realizing a peer's route command: create, update or delete a transport line by driving the
    // game's own route tool with the waypoints the sender used.
    //
    // Resolving those waypoints against local stops is in RealizeConnections.cs, finding the route
    // a command refers to in RealizeMatch.cs, and the commit that finishes a create or update in
    // RealizeCommit.cs.
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
                SyncInbox.RequestResync(CS2MultiplayerMod.Game.Diagnostics.ResyncReport
                    .Create("unknown route prefab during creation", "route",
                        CS2MultiplayerMod.Game.Diagnostics.ResyncEvidence.StreamLoss)
                    .About("route prefab on creation")
                    .Tried("nothing - this game does not have the transport line prefab the other player used"));
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
                    SyncInbox.RequestResync(CS2MultiplayerMod.Game.Diagnostics.ResyncReport
                        .Create("pending route number conflict", "route",
                            CS2MultiplayerMod.Game.Diagnostics.ResyncEvidence.Contradiction)
                        .About("pending line number")
                        .Tried("nothing - another line being created in this batch already claimed that number"));
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
                SyncInbox.RequestResync(CS2MultiplayerMod.Game.Diagnostics.ResyncReport
                    .Create("route number conflict during creation", "route",
                        CS2MultiplayerMod.Game.Diagnostics.ResyncEvidence.Contradiction)
                    .About("line number on creation")
                    .Tried("nothing - an established line here already uses that number"));
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
                    SyncInbox.RequestResync(CS2MultiplayerMod.Game.Diagnostics.ResyncReport
                        .Create("route metadata conflict during idempotent creation", "route",
                            CS2MultiplayerMod.Game.Diagnostics.ResyncEvidence.Contradiction)
                        .About("line metadata on re-creation")
                        .Tried("nothing - the line already exists here with different metadata"));
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
                SyncInbox.RequestResync(CS2MultiplayerMod.Game.Diagnostics.ResyncReport
                    .Create("route creation failed", "route",
                        CS2MultiplayerMod.Game.Diagnostics.ResyncEvidence.Contradiction)
                    .About("line creation")
                    .Tried("nothing - creation threw and was rolled back"));
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
                SyncInbox.RequestResync(CS2MultiplayerMod.Game.Diagnostics.ResyncReport
                    .Create("unknown route prefab during update", "route",
                        CS2MultiplayerMod.Game.Diagnostics.ResyncEvidence.StreamLoss)
                    .About("route prefab on update")
                    .Tried("nothing - this game does not have the transport line prefab the other player used"));
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
                SyncInbox.RequestResync(CS2MultiplayerMod.Game.Diagnostics.ResyncReport
                    .Create("route number conflict during update", "route",
                        CS2MultiplayerMod.Game.Diagnostics.ResyncEvidence.Contradiction)
                    .About("line number on update")
                    .Tried("nothing - another line here already uses the number this update assigns"));
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
                    SyncInbox.RequestResync(CS2MultiplayerMod.Game.Diagnostics.ResyncReport
                        .Create("route number conflict during update", "route",
                            CS2MultiplayerMod.Game.Diagnostics.ResyncEvidence.Contradiction)
                        .About("line number on update")
                        .Tried("nothing - another line here already uses the number this update assigns"));
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
                SyncInbox.RequestResync(CS2MultiplayerMod.Game.Diagnostics.ResyncReport
                    .Create("route update failed", "route",
                        CS2MultiplayerMod.Game.Diagnostics.ResyncEvidence.Contradiction)
                    .About("line update")
                    .Tried("nothing - the update threw and was rolled back"));
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
                SyncInbox.RequestResync(CS2MultiplayerMod.Game.Diagnostics.ResyncReport
                    .Create("unknown route prefab during deletion", "route",
                        CS2MultiplayerMod.Game.Diagnostics.ResyncEvidence.StreamLoss)
                    .About("route prefab on deletion")
                    .Tried("nothing - this game does not have the transport line prefab the other player used"));
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
    }
}
