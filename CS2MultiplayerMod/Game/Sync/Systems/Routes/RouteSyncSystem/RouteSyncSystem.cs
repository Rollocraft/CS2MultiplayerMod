using System.Collections.Generic;
using Game.Common;
using Game.Prefabs;
using Game.Routes;
using Game.Tools;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Infrastructure;
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.Systems.Net;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Replicates routes and their waypoint graph. Route number is the portable identity; stops
    /// resolve from prefab, transform and owner.
    /// </summary>
    public partial class RouteSyncSystem : CommandSyncSystem, IRealizeStage
    {
        private const long EditScanIntervalMs = 1000;
        // Stops are often still being realized; giving up costs a world transfer.
        private const long RetryWindowMs = 30000;
        private const long InitialRetryDelayMs = 100;
        private const long MaximumRetryDelayMs = 1000;
        private const int MaxPendingCommands = 128;
        private const int MaxCommandsPerFrame = 16;

        private readonly ReplicationGuard _guard = new ReplicationGuard();
        private Dictionary<Entity, RouteSnapshot> _knownRoutes = new Dictionary<Entity, RouteSnapshot>();
        private Dictionary<Entity, RouteSnapshot> _nextRoutes = new Dictionary<Entity, RouteSnapshot>();
        private readonly HashSet<Entity> _needsCreateCapture = new HashSet<Entity>();
        private readonly HashSet<Entity> _baselinePendingRoutes = new HashSet<Entity>();
        private readonly HashSet<Entity> _mutatedRoutesThisFrame = new HashSet<Entity>();
        private readonly List<PendingRouteCommand> _pendingCommands = new List<PendingRouteCommand>();
        private readonly List<PendingCreateMetadata> _pendingCreateMetadata =
            new List<PendingCreateMetadata>();
        private readonly Dictionary<Entity, string> _prefabNames = new Dictionary<Entity, string>();
        private PendingUpdateCommit _pendingUpdateCommit;
        private string _lastRealizeFailure;
        private long _lastEditScanMs;
        private bool _wasGameplaySyncReady;

        private struct RouteSnapshot
        {
            public RouteWaypointIntent[] Waypoints;
            public uint Rgba;
            public int RouteNumber;
            public bool IsComplete;
        }

        private sealed class PendingRouteCommand
        {
            public RouteCreateCommand Create;
            public RouteUpdateCommand Update;
            public RouteDeleteCommand Delete;
            public int OriginPlayerId;
            public long DeadlineMs;
            public long NextAttemptMs;
            public long RetryDelayMs;
            public string LastFailure;
        }

        private sealed class PendingCreateMetadata
        {
            public Entity Prefab;
            public string PrefabName;
            public RouteWaypointIntent[] Waypoints;
            public HashSet<Entity> PreexistingShapeMatches;
            public int RouteNumber;
            public uint Rgba;
            public long DeadlineMs;
            public RouteCreateCommand Source;
            public int OriginPlayerId;
            public bool GraphCommitted;
        }

        private sealed class PendingUpdateCommit
        {
            public Entity Route;
            public RouteUpdateCommand Source;
            public int OriginPlayerId;
            public long DeadlineMs;
            public RouteSnapshot Original;
            public RouteSnapshot Desired;
        }

        private enum RealizeResult : byte
        {
            Applied,
            Retry,
            Rejected,
        }

        private PrefabSystem _prefabSystem;
        private PrefabIndex _prefabIndex;
        private EntityQuery _createdRoutes;
        private EntityQuery _deletedRoutes;
        private EntityQuery _liveRoutes;
        private EntityQuery _transportStops;
        private NetSyncSystem _netSync;

        protected override void OnCreate()
        {
            base.OnCreate();

            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _prefabIndex = new PrefabIndex(_prefabSystem,
                GetEntityQuery(ComponentType.ReadOnly<PrefabData>()));
            _netSync = World.GetOrCreateSystemManaged<NetSyncSystem>();

            _createdRoutes = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<Created, Route, PrefabRef>(),
                None = SyncQuery.ReadOnly<Temp, Deleted>(),
            });

            _deletedRoutes = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<Deleted, Route, PrefabRef>(),
                None = SyncQuery.ReadOnly<Temp>(),
            });

            _liveRoutes = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<Route, PrefabRef>(),
                None = SyncQuery.ReadOnly<Temp, Deleted>(),
            });

            _transportStops = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<global::Game.Routes.TransportStop, ConnectedRoute,
                    PrefabRef, global::Game.Objects.Transform>(),
                None = SyncQuery.ReadOnly<Temp, Deleted>(),
            });

            ListenFor(new[] { RouteCreateCommand.Id, RouteUpdateCommand.Id, RouteDeleteCommand.Id },
                RouteCreateCommand.MaxEncodedBytes);
        }

        protected override void OnUpdate()
        {
            using (Diagnostics.SyncProfiler.Measure("RouteSync"))
            {
                MultiplayerService service = Mod.Service;
                if (service == null) return;

                MultiplayerSession session = service.Session;
                if (!service.GameplaySyncReady)
                {
                    _wasGameplaySyncReady = false;
                    if (_knownRoutes.Count > 0) _knownRoutes.Clear();
                    if (_nextRoutes.Count > 0) _nextRoutes.Clear();
                    if (_needsCreateCapture.Count > 0) _needsCreateCapture.Clear();
                    if (_baselinePendingRoutes.Count > 0) _baselinePendingRoutes.Clear();
                    return;
                }

                long now = service.NowMs;
                _guard.Prune(now);
                if (!_wasGameplaySyncReady)
                {
                    _wasGameplaySyncReady = true;
                    BaselineLiveRoutes();
                    return;
                }
                CaptureCreated(session, now);
                CaptureDeleted(session, now);
                ScanForEdits(session, now);
            }
        }

        /// <summary>Finishes committed route graphs even while net work is backlogged.</summary>
        private void FinalizePending()
        {
            MultiplayerService service = Mod.Service;
            if (service == null) return;
            if (!service.GameplaySyncReady) { ExtendCreateMetadataWindows(service.NowMs); return; }

            // Not gated itself, but it waits on routes RealizeCommands submits, which is gated.
            ExtendCreateMetadataWindows(service.NowMs);

            _mutatedRoutesThisFrame.Clear();
            FinalizeCreatedRoutes(service.NowMs);
        }

        private readonly Infrastructure.HeldTime _createMetadataHold = new Infrastructure.HeldTime();

        private void ExtendCreateMetadataWindows(long nowMs)
        {
            long heldMs = _createMetadataHold.Observe(nowMs,
                Infrastructure.RealizeGate.WorldBuildingHeld ||
                _netSync == null || !_netSync.CanBuildDefinitions);
            if (heldMs <= 0) return;
            for (int i = 0; i < _pendingCreateMetadata.Count; i++)
                _pendingCreateMetadata[i].DeadlineMs += heldMs;
        }

        /// <summary>When this system last got to attempt its pending commands.</summary>
        private long _lastRouteRealizeMs;

        private void ExtendPendingRouteWindows(long nowMs)
        {
            long heldMs = _lastRouteRealizeMs == 0 ? 0 : nowMs - _lastRouteRealizeMs;
            _lastRouteRealizeMs = nowMs;
            if (heldMs <= 0) return;
            for (int i = 0; i < _pendingCommands.Count; i++)
            {
                _pendingCommands[i].DeadlineMs += heldMs;
                _pendingCommands[i].NextAttemptMs += heldMs;
            }
        }

        /// <summary>Called by <see cref="SyncRealizeSystem"/> during ToolUpdate.</summary>
        public void RealizePending()
        {
            // Two independent passes: a fault while finalizing must not also strand new routes.
            try { FinalizePending(); }
            finally { RealizeCommands(); }
        }

        private void RealizeCommands()
        {
            MultiplayerService service = Mod.Service;
            if (service == null) return;

            // Held time does not count against a route's retry window.
            if (Infrastructure.RealizeGate.WorldBuildingHeld)
            {
                ExtendPendingRouteWindows(service.NowMs);
                return;
            }

            MultiplayerSession session = service.Session;
            if (!service.GameplaySyncReady) { ExtendPendingRouteWindows(service.NowMs); return; }

            long now = service.NowMs;
            if (_netSync == null || !_netSync.CanBuildDefinitions)
            {
                ExtendPendingRouteWindows(now);
                return;
            }
            _lastRouteRealizeMs = now;

            int budget = MaxCommandsPerFrame;
            int retries = System.Math.Min(_pendingCommands.Count, MaxCommandsPerFrame / 2);
            for (int i = 0; i < retries; i++)
            {
                // Round-robin so one unavailable station cannot monopolize the retry budget.
                PendingRouteCommand pending = _pendingCommands[0];
                _pendingCommands.RemoveAt(0);
                if (now >= pending.DeadlineMs)
                {
                    ExpirePending(pending);
                    continue;
                }
                if (now < pending.NextAttemptMs)
                {
                    _pendingCommands.Add(pending);
                    continue;
                }

                RealizeResult result = TryRealize(pending, now);
                budget--;
                if (result == RealizeResult.Retry) QueueRetry(pending, now);
            }

            while (budget > 0 && _incoming.TryDequeue(out SimulationCommandMessage message))
            {
                if (message.OriginPlayerId == session.LocalPlayerId) continue;
                budget--;
                try
                {
                    PendingRouteCommand pending;
                    if (message.CommandId == RouteCreateCommand.Id)
                        pending = new PendingRouteCommand
                        {
                            Create = RouteCreateCommand.Decode(message.Body),
                            OriginPlayerId = message.OriginPlayerId,
                            DeadlineMs = now + RetryWindowMs,
                        };
                    else if (message.CommandId == RouteUpdateCommand.Id)
                        pending = new PendingRouteCommand
                        {
                            Update = RouteUpdateCommand.Decode(message.Body),
                            OriginPlayerId = message.OriginPlayerId,
                            DeadlineMs = now + RetryWindowMs,
                        };
                    else if (message.CommandId == RouteDeleteCommand.Id)
                        pending = new PendingRouteCommand
                        {
                            Delete = RouteDeleteCommand.Decode(message.Body),
                            OriginPlayerId = message.OriginPlayerId,
                            DeadlineMs = now + RetryWindowMs,
                        };
                    else
                        continue;

                    if (TryRealize(pending, now) == RealizeResult.Retry)
                        QueueRetry(pending, now);
                }
                catch (System.Exception ex)
                {
                    SyncInbox.RequestResync(CS2MultiplayerMod.Game.Diagnostics.ResyncReport
                        .Create("malformed route command rejected", "route",
                            CS2MultiplayerMod.Game.Diagnostics.ResyncEvidence.StreamLoss)
                        .About("malformed route command")
                        .Tried("nothing - the command could not be decoded"));
                    SyncLog.Warn(LogTopic.Routes, "RouteSync: dropping malformed command: " +
                        ex.Message);
                }
            }
        }

        private RealizeResult TryRealize(PendingRouteCommand pending, long now)
        {
            if (_netSync == null || !_netSync.CanBuildDefinitions)
                return RealizeResult.Retry;

            _lastRealizeFailure = null;
            RealizeResult result;
            if (pending.Create != null)
                result = RealizeCreate(pending.Create, pending.OriginPlayerId, now);
            else if (pending.Update != null)
                result = RealizeUpdate(pending.Update, pending.OriginPlayerId, now);
            else
                result = pending.Delete != null
                    ? RealizeDelete(pending.Delete, now)
                    : RealizeResult.Rejected;

            // Record why the last attempt gave up, for the expiry log.
            if (_lastRealizeFailure == null) return result;
            if (pending.LastFailure == null)
                SyncLog.Trace(LogTopic.Routes, "route dependency unresolved: " +
                    _lastRealizeFailure);
            pending.LastFailure = _lastRealizeFailure;
            return result;
        }

        private void QueueRetry(PendingRouteCommand pending, long now)
        {
            if (_pendingCommands.Count < MaxPendingCommands)
            {
                pending.RetryDelayMs = pending.RetryDelayMs == 0
                    ? InitialRetryDelayMs
                    : System.Math.Min(pending.RetryDelayMs * 2,
                        MaximumRetryDelayMs);
                pending.NextAttemptMs = now + pending.RetryDelayMs;
                _pendingCommands.Add(pending);
                return;
            }

            _pendingCommands.Clear();
            SyncInbox.RequestResync(CS2MultiplayerMod.Game.Diagnostics.ResyncReport
                .Create("route retry queue overflow", "route",
                    CS2MultiplayerMod.Game.Diagnostics.ResyncEvidence.StreamLoss)
                .About("route retry queue")
                .Tried("nothing - the retry queue was full and was cleared"));
            SyncLog.Warn(LogTopic.Routes,
                "RouteSync retry queue overflowed; cleared it and requested a fresh world sync.");
        }

        private void ExpirePending(PendingRouteCommand pending)
        {
            string operation = pending.Create != null ? "creation" :
                pending.Update != null ? "update" : "deletion";
            string prefabName = pending.Create != null ? pending.Create.PrefabName :
                pending.Update != null ? pending.Update.PrefabName : pending.Delete.PrefabName;

            // An unmatched delete is fine only when the line is truly absent, not ambiguous or still live.
            bool needsRecovery = pending.Delete == null ||
                                 DeleteStillNeedsRecovery(pending.Delete);
            if (needsRecovery)
                SyncInbox.RequestResync(CS2MultiplayerMod.Game.Diagnostics.ResyncReport
                    .Create("route " + operation + " dependency did not resolve", "route",
                        CS2MultiplayerMod.Game.Diagnostics.ResyncEvidence.MissingTarget)
                    .About(operation + " of '" + prefabName + "'")
                    .Tried("retried with backoff for 30 s of attempts, not counting time this system was held back")
                    .Fact("last failure", pending.LastFailure));
            SyncLog.Warn(LogTopic.Routes, "RouteSync " + operation + " for '" + prefabName +
                "' did not resolve within " + (RetryWindowMs / 1000) + " s" +
                (pending.LastFailure != null ? " (" + pending.LastFailure + ")" : string.Empty) +
                (needsRecovery ? "; requested a fresh world sync." : "; line is already absent."));
        }

        protected override void DrainQueue()
        {
            SyncInbox.Clear(_incoming);
            _lastRouteRealizeMs = 0;
            _pendingCommands.Clear();
            _pendingCreateMetadata.Clear();
            _pendingUpdateCommit = null;
            _needsCreateCapture.Clear();
            _baselinePendingRoutes.Clear();
            _mutatedRoutesThisFrame.Clear();
            _knownRoutes.Clear();
            _nextRoutes.Clear();
            _prefabNames.Clear();
            _guard.Clear();
            _lastRealizeFailure = null;
            _lastEditScanMs = 0;
            _wasGameplaySyncReady = false;
        }

        /// <summary>Stops served plus the waypoints that only shape the path between them.</summary>
        private static string DescribeShape(RouteWaypointIntent[] waypoints)
        {
            int stops = 0;
            for (int i = 0; i < waypoints.Length; i++)
                if (!string.IsNullOrEmpty(waypoints[i].StopPrefabName)) stops++;
            return stops + " stop(s)" + (waypoints.Length != stops
                ? " + " + (waypoints.Length - stops) + " path waypoint(s)"
                : string.Empty);
        }

        private static string RouteKey(string prefix, string prefabName, int routeNumber,
            float3 firstWaypoint) =>
            prefix + "|" + routeNumber + "|" + ReplicationGuard.Key(prefabName, firstWaypoint);

        private static string RouteShapeKey(string prefix, string prefabName,
            RouteWaypointIntent[] waypoints)
        {
            unchecked
            {
                uint hash = 2166136261u;
                for (int i = 0; i < waypoints.Length; i++)
                {
                    HashCoordinate(ref hash, waypoints[i].X);
                    HashCoordinate(ref hash, waypoints[i].Y);
                    HashCoordinate(ref hash, waypoints[i].Z);
                    HashString(ref hash, waypoints[i].StopPrefabName);
                    HashString(ref hash, waypoints[i].OwnerPrefabName);
                }
                return prefix + "|" + prefabName + "|" + waypoints.Length + "|" + hash;
            }
        }

        private static void HashCoordinate(ref uint hash, float value) =>
            hash = (hash ^ (uint)(int)math.round(value * 10f)) * 16777619u;

        private static void HashString(ref uint hash, string value)
        {
            if (value != null)
                for (int i = 0; i < value.Length; i++)
                    hash = (hash ^ value[i]) * 16777619u;
            // Field delimiter keeps ["ab", "c"] distinct from ["a", "bc"].
            hash = (hash ^ 0xffu) * 16777619u;
        }
    }
}
