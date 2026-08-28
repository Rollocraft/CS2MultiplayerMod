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
    // Finishing a route the tool has created. Its number and colour can only be applied once the
    // route entity exists, so they are held until the commit lands - and replayed if it is lost.
    public partial class RouteSyncSystem
    {
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
                SyncInbox.RequestResync(CS2MultiplayerMod.Game.Diagnostics.ResyncReport
                    .Create(ambiguous ? "ambiguous created route" : "created route did not materialize",
                        "route", ambiguous
                            ? CS2MultiplayerMod.Game.Diagnostics.ResyncEvidence.Contradiction
                            : CS2MultiplayerMod.Game.Diagnostics.ResyncEvidence.MissingTarget)
                    .About("created line " + pending.PrefabName + " number " + pending.RouteNumber)
                    .Tried(ambiguous
                        ? "declined to guess between two equally good candidate lines"
                        : "waited for the line the game builds from this definition, not counting " +
                          "time route realization was held back")
                    .Fact("line number", pending.RouteNumber));
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
                    SyncInbox.RequestResync(CS2MultiplayerMod.Game.Diagnostics.ResyncReport
                        .Create("route number conflict after creation", "route",
                            CS2MultiplayerMod.Game.Diagnostics.ResyncEvidence.Contradiction)
                        .About("line number after creation")
                        .Tried("assigned every line finalized in this batch together before rejecting the conflict"));
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
            // Gameplay not being ready means the world is already being replaced. The replacement
            // supersedes this commit, so asking for another one is noise - and asking for a world
            // reload BECAUSE a world reload is under way is how a session gets into a loop.
            if (service == null || !service.GameplaySyncReady)
            {
                Mod.log.Warn("[MP] RouteSync " + operation +
                             " commit was lost while the world was being replaced; the incoming " +
                             "world supersedes it.");
                return;
            }
            if (now >= command.DeadlineMs ||
                _pendingCommands.Count >= MaxPendingCommands)
            {
                SyncInbox.RequestResync(CS2MultiplayerMod.Game.Diagnostics.ResyncReport
                    .Create("route commit could not be replayed", "route",
                        CS2MultiplayerMod.Game.Diagnostics.ResyncEvidence.StreamLoss)
                    .About("route " + operation + " commit")
                    .Tried("nothing - the armed commit was wiped and its window had already closed")
                    .Fact("route commands still queued", _pendingCommands.Count));
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
