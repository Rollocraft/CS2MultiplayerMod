using System.Collections.Concurrent;
using System.Collections.Generic;
using Game;
using Game.Common;
using Game.Prefabs;
using Game.Routes;
using Game.Tools;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Sync.Infrastructure;
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.Systems.Net;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Replicates a route and its owned waypoint/segment graph. Route numbers provide the primary
    /// portable identity; connected transport stops are resolved from prefab, transform, and owner.
    /// </summary>
    public partial class RouteSyncSystem : GameSystemBase
    {
        private const long EditScanIntervalMs = 1000;
        // A stop a line depends on is often still being realized from its own command. Waiting is
        // free; giving up costs a full world transfer, so the window is generous.
        private const long RetryWindowMs = 30000;
        private const long InitialRetryDelayMs = 100;
        private const long MaximumRetryDelayMs = 1000;
        private const int MaxPendingCommands = 128;
        private const int MaxCommandsPerFrame = 16;

        private readonly ConcurrentQueue<SimulationCommandMessage> _incoming =
            new ConcurrentQueue<SimulationCommandMessage>();
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
        private CommandObserver _observer;
        private NetSyncSystem _netSync;

        protected override void OnCreate()
        {
            base.OnCreate();

            Mod.log.Info(nameof(RouteSyncSystem) + " ready.");
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _prefabIndex = new PrefabIndex(_prefabSystem,
                GetEntityQuery(ComponentType.ReadOnly<PrefabData>()));
            _netSync = World.GetOrCreateSystemManaged<NetSyncSystem>();

            _createdRoutes = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Created>(),
                    ComponentType.ReadOnly<Route>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                },
            });

            _deletedRoutes = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Route>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                },
            });

            _liveRoutes = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Route>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                },
            });

            _transportStops = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<global::Game.Routes.TransportStop>(),
                    ComponentType.ReadOnly<ConnectedRoute>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<global::Game.Objects.Transform>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                },
            });

            if (Mod.Service != null)
            {
                _observer = new CommandObserver(_incoming, RouteCreateCommand.Id,
                    RouteUpdateCommand.Id, RouteDeleteCommand.Id)
                {
                    MaxBodyBytes = RouteCreateCommand.MaxEncodedBytes,
                };
                Mod.Service.Session.AddObserver(_observer);
            }
            SyncInbox.RegisterDrain(DrainQueue);
        }

        protected override void OnDestroy()
        {
            SyncInbox.UnregisterDrain(DrainQueue);
            if (_observer != null && Mod.Service != null)
                Mod.Service.Session.RemoveObserver(_observer);
            base.OnDestroy();
        }

        protected override void OnUpdate()
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

        /// <summary>
        /// Finish identities for already committed route graphs even while a later network
        /// transaction is backlogged. This never creates tool definitions.
        /// </summary>
        public void FinalizePending()
        {
            MultiplayerService service = Mod.Service;
            if (service == null) return;
            if (!service.GameplaySyncReady) return;

            _mutatedRoutesThisFrame.Clear();
            FinalizeCreatedRoutes(service.NowMs);
        }

        /// <summary>Called by <see cref="SyncRealizeSystem"/> during ToolUpdate.</summary>
        public void RealizePending()
        {
            MultiplayerService service = Mod.Service;
            if (service == null) return;

            MultiplayerSession session = service.Session;
            if (!service.GameplaySyncReady) return;

            long now = service.NowMs;
            if (_netSync == null || !_netSync.CanBuildDefinitions) return;

            int budget = MaxCommandsPerFrame;
            int retries = System.Math.Min(_pendingCommands.Count, MaxCommandsPerFrame / 2);
            for (int i = 0; i < retries; i++)
            {
                // Round-robin prevents one unavailable station from monopolizing the retry budget
                // and guarantees that fresh route commands continue to drain every frame.
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

            SimulationCommandMessage message;
            while (budget > 0 && _incoming.TryDequeue(out message))
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
                    SyncInbox.RequestResync("malformed route command rejected");
                    Mod.log.Warn("[MP] RouteSync: dropping malformed command: " + ex.Message);
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

            // Every unmet dependency looks the same from outside: the command simply keeps
            // retrying. Recording why the last attempt gave up makes an expired command
            // attributable from the log instead of only visible as a missing line.
            if (_lastRealizeFailure == null) return result;
            if (pending.LastFailure == null)
                Diagnostics.FlightRecorder.Note("route dependency unresolved: " +
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
            SyncInbox.RequestResync("route retry queue overflow");
            Mod.log.Warn("[MP] RouteSync retry queue overflowed; cleared it and requested a fresh world sync.");
        }

        private void ExpirePending(PendingRouteCommand pending)
        {
            string operation = pending.Create != null ? "creation" :
                pending.Update != null ? "update" : "deletion";
            string prefabName = pending.Create != null ? pending.Create.PrefabName :
                pending.Update != null ? pending.Update.PrefabName : pending.Delete.PrefabName;

            // An unmatched delete is idempotent only when the line really is absent. Ambiguous
            // candidates or a still-live target mean we deliberately declined a destructive guess.
            bool needsRecovery = pending.Delete == null ||
                                 DeleteStillNeedsRecovery(pending.Delete);
            if (needsRecovery)
                SyncInbox.RequestResync("route " + operation + " dependency did not resolve");
            Mod.log.Warn("[MP] RouteSync " + operation + " for '" + prefabName +
                         "' did not resolve within " + (RetryWindowMs / 1000) + " s" +
                         (pending.LastFailure != null ? " (" + pending.LastFailure + ")" : string.Empty) +
                         (needsRecovery
                             ? "; requested a fresh world sync."
                             : "; line is already absent."));
        }

        private void DrainQueue()
        {
            SyncInbox.Clear(_incoming);
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

        private static void HashCoordinate(ref uint hash, float value)
        {
            hash = (hash ^ (uint)(int)math.round(value * 10f)) * 16777619u;
        }

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
