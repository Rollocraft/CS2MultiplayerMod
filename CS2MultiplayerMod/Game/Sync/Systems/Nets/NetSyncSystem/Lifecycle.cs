using System.Collections.Concurrent;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Unity.Entities;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Infrastructure;
using CS2MultiplayerMod.Game.Sync.Commands;
namespace CS2MultiplayerMod.Game.Sync.Systems.Net
{
    public partial class NetSyncSystem
    {
        protected override void OnCreate()
        {
            base.OnCreate();

            // A connector beside a standing building names a live owner; found by asking what stands there.
            _ownerSearch = new ObjectSearch(
                World.GetOrCreateSystemManaged<global::Game.Objects.SearchSystem>());
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _prefabIndex = new PrefabIndex(_prefabSystem, GetEntityQuery(ComponentType.ReadOnly<PrefabData>()));

            _toolSystem = World.GetOrCreateSystemManaged<global::Game.Tools.ToolSystem>();
            _applyNetSystem = World.GetOrCreateSystemManaged<global::Game.Tools.ApplyNetSystem>();
            _applyObjectsSystem = World.GetOrCreateSystemManaged<global::Game.Tools.ApplyObjectsSystem>();
            _applyAreasSystem = World.GetOrCreateSystemManaged<global::Game.Tools.ApplyAreasSystem>();
            _applyBrushesSystem = World.GetOrCreateSystemManaged<global::Game.Tools.ApplyBrushesSystem>();
            _applyRoutesSystem = World.GetOrCreateSystemManaged<global::Game.Tools.ApplyRoutesSystem>();
            _netSearchSystem = World.GetOrCreateSystemManaged<global::Game.Net.SearchSystem>();
            _terrainSystem = World.GetOrCreateSystemManaged<global::Game.Simulation.TerrainSystem>();
            _waterSystem = World.GetOrCreateSystemManaged<global::Game.Simulation.WaterSystem>();
            // The net apply pass's structural query, including Temps already Deleted.
            _netTransactionTemps = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<Temp>(),
                Any = SyncQuery.ReadOnly<Node, Edge, Lane, Aggregate>(),
            });

            _netOperationTemps = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<Temp>(),
                Any = SyncQuery.ReadOnly<global::Game.Objects.Object, Node, Edge, Lane, Aggregate,
                    global::Game.Areas.Area>(),
            });

            _objectTransactionTemps = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<Temp>(),
                Any = SyncQuery.ReadOnly<global::Game.Objects.Object, Node, Edge, Lane, Aggregate,
                    global::Game.Areas.Area>(),
            });

            _routeTransactionTemps = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<Temp>(),
                Any = SyncQuery.ReadOnly<global::Game.Routes.Route, global::Game.Routes.Waypoint,
                    global::Game.Routes.Segment>(),
            });

            // Zoning previews are excluded: no isolated commit reads Block/Cell, and isolating the marquee
            // would discard the gesture.
            _standingTemps = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<Temp>(),
                None = SyncQuery.ReadOnly<Deleted, global::Game.Zones.Block>(),
            });

            // Untagged tool definitions are exactly what ToolOutputSystem consumes on Apply; sync ones carry
            // Deleted from birth.
            _standingLocalDefinitions = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<CreationDefinition>(),
                None = SyncQuery.ReadOnly<Updated, Deleted>(),
            });

            _localBrushTemps = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<Temp, Brush>(),
                None = SyncQuery.ReadOnly<Deleted, RemoteTerrainBrush>(),
            });

            _createdEdges = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<Created, Edge, Curve, PrefabRef>(),
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                    // Hidden sub-networks owned by roads and buildings.
                    ComponentType.ReadOnly<Owner>(),
                },
            });

            // Owner-less, live nodes only: never hidden sub-nets, previews or dying nodes.
            _existingNodes = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<Node>(),
                None = SyncQuery.ReadOnly<Temp, Deleted, Owner>(),
            });

            // Read-only: standalone edges, used to classify an incoming endpoint as a mid-span tap.
            _existingEdges = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<Edge, Curve, PrefabRef>(),
                None = SyncQuery.ReadOnly<Temp, Deleted, Owner>(),
            });

            // Owned nodes, for utility connections only (see UtilityConnectLayers).
            _ownedNodes = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<Node, Owner, PrefabRef>(),
                None = SyncQuery.ReadOnly<Temp, Deleted>(),
            });

            // Kept out of fallback searches; explicit native intent searches this pool.
            _ownedEdges = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<Edge, Curve, Owner, PrefabRef>(),
                None = SyncQuery.ReadOnly<Temp, Deleted>(),
            });

            // Diagnostic: Updated-not-Created edges, i.e. an in-place split.
            _updatedEdges = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<Edge, Curve, Updated>(),
                None = SyncQuery.ReadOnly<Temp, Deleted, Created, Owner>(),
            });

            // Diagnostic: edges being removed this frame.
            _deletedEdges = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<Edge, Curve, Deleted>(),
                None = SyncQuery.ReadOnly<Temp, Owner>(),
            });

            _observer = SyncObserverBinding.Bind(
                () => new Observer(_incoming), DrainNetQueues);
        }

        protected override void OnDestroy()
        {
            SyncInbox.UnregisterDrain(DrainNetQueues);
            ReleaseAllIsolation();
            SyncObserverBinding.Unbind(_observer);
            base.OnDestroy();
        }

        private void DrainNetQueues()
        {
            MultiplayerService service = Mod.Service;
            if (service != null && service.WorldSyncBarrierActive && IsCommitBusy)
            {
                // A world-sync Begin stops admission; work the native pipeline already owns is driven to a clean
                // boundary before the snapshot.
                SyncInbox.Clear(_incoming);
                _remoteDeferred.Clear();
                _deferredSpanPieces.Clear();
                _cachedLocalCourses.Clear();
                _cachedLocalMixedOperation.Clear();
                _cachedFallbackOriginalEdges.Clear();
                _cachedMixedRejection = null;
                _atomicMixedApplyCapturedFrame = -1;
                return;
            }

            // Never leave an isolated remote transaction behind. Uncommitted work is cleared; a scheduled apply
            // is quarantined instead, since deleting it races native jobs.
            if (_protectedRemoteNetTemps.Count > 0)
            {
                TrackInvalidatedTemps(_protectedRemoteNetTemps);
                if (_awaitingDrain)
                    ReleaseTrackedTemps(_protectedRemoteNetTemps);
                else
                {
                    ClearTrackedTemps(_protectedRemoteNetTemps, clearPreview: true);
                    _protectedRemoteNetTemps.Clear();
                }
            }
            else if (_pendingApply)
            {
                TrackInvalidatedTemps(ActiveTransactionQuery());
                ClearTempEntities(ActiveTransactionQuery());
            }
            if (_committingRemoteNetTemps.Count > 0)
            {
                TrackInvalidatedTemps(_committingRemoteNetTemps);
                // Removes the commit shield only; the scheduled transaction finishes.
                ReleaseTrackedTemps(_committingRemoteNetTemps);
            }
            ReleaseAllIsolation();
            SyncInbox.Clear(_incoming);
            _remoteDeferred.Clear();
            _deferredSpanPieces.Clear();
            _cachedLocalCourses.Clear();
            _cachedLocalMixedOperation.Clear();
            _cachedFallbackOriginalEdges.Clear();
            _cachedMixedRejection = null;
            _atomicMixedApplyCapturedFrame = -1;
            _committedNetSideEffects.Clear();
            _atomicMixedOriginals.Clear();
            _atomicMixedOriginalsFrame = -1;
            _nativeTargetDeadlines.Clear();
            _operationAssemblyDeadlines.Clear();
            _nativeOperationHolds.Clear();
            // The world these described is being replaced; nothing left to withdraw or settle.
            _outstandingDrainSubjects.Clear();
            _drainRemainingTemps = int.MaxValue;
            _operationBuildFailures.Clear();
            _completedNetOperations.Clear();
            _armedNetOperations.Clear();
            _batchSplitClaims.Clear();
            _recentRealizedSpans.Clear();
            _pendingApply = false;
            _pendingTransactionKind = RemoteToolTransactionKind.None;
            _committingTransactionKind = RemoteToolTransactionKind.None;
            _awaitingDrain = false;
            _drainCleanFrames = 0;
            // Everything is already drained at the barrier; a fence would stall a paused recovery.
            _drainReleasedThisFrame = false;
            _pendingNetConstructionCharge = 0;
            _pendingNetConstructionChargeCourses = 0;
            _committingNetConstructionCharge = 0;
            _committingNetConstructionChargeCourses = 0;
            _onCommitLost = null;
            _onCommitComplete = null;
            _replayAfterInvalidatedDrain = null;
            PruneInvalidatedTemps();
            _invalidatedBatchDraining = TrackedInvalidatedTempsRemain();
            _invalidatedCleanFrames = 0;
            _invalidatedDrainTimedOut = false;
            if (_invalidatedBatchDraining && _invalidatedDrainArmTick == 0)
                _invalidatedDrainArmTick = System.Environment.TickCount;
            else if (!_invalidatedBatchDraining)
            {
                _invalidatedRemoteTemps.Clear();
                _invalidatedDrainArmTick = 0;
            }
            _applyReplayBudget.Reset();
            _pendingOwnerDefinitions.Clear();
            _describedOwners.Clear();
            _lastDescribedOwner = Entity.Null;
            _lastInvalidReason = null;
            _suppressCaptureThisFrame = false;
            _prepDoneThisFrame = false;
        }

        protected override void OnUpdate()
        {
            using (Diagnostics.SyncProfiler.Measure("NetSync"))
            {
                MultiplayerService service = Mod.Service;
                if (service == null) return;

                MultiplayerSession session = service.Session;
                if (!service.GameplaySyncReady)
                {
                    DrainNetQueues();
                    return;
                }

                long now = service.NowMs;
                _guard.Prune(now);
                PruneCommittedNetSideEffects(now);

                // Peak lifecycle tags, only paid when the verbose line is being read.
                if (SyncLog.IsEnabled(LogTopic.Nets))
                {
                    _peakCreated = System.Math.Max(_peakCreated, _createdEdges.CalculateEntityCount());
                    _peakUpdated = System.Math.Max(_peakUpdated, _updatedEdges.CalculateEntityCount());
                    _peakDeleted = System.Math.Max(_peakDeleted, _deletedEdges.CalculateEntityCount());
                }

                FlushDeferredSpanPieces(session);
                CaptureNewEdges(session, now);
                FlushDiagnostics(now);
            }
        }

        private sealed class Observer : SessionObserver
        {
            private readonly ConcurrentQueue<SimulationCommandMessage> _sink;
            public Observer(ConcurrentQueue<SimulationCommandMessage> sink) { _sink = sink; }

            public override void OnCommandReceived(SimulationCommandMessage command)
            {
                bool placement = command.CommandId == NetPlacementCommand.Id;
                bool mixed = command.CommandId == NetToolOperationCommand.Id;
                if (!placement && !mixed) return;
                int cap = mixed
                    ? NetToolOperationCommand.MaxEncodedBytes
                    : NetPlacementCommand.MaxEncodedBytes;
                if (command.Body == null || command.Body.Length > cap) return;
                if (mixed && _sink.Count >= MixedNetInboxAdmissionCap)
                {
                    SyncLog.Warn(LogTopic.Nets,
                        "NetSync: mixed-operation inbox admission cap reached; " +
                        "requesting recovery instead of dropping an atomic edit silently.");
                    SyncInbox.RequestResync(CS2MultiplayerMod.Game.Diagnostics.ResyncReport
                        .Create("mixed net operation inbox overflow", "net",
                            CS2MultiplayerMod.Game.Diagnostics.ResyncEvidence.StreamLoss)
                        .About("mixed net inbox")
                        .Tried("nothing - the edit was refused at the door rather than dropped silently")
                        .Fact("queued mixed operations", _sink.Count)
                        .Fact("admission cap", MixedNetInboxAdmissionCap));
                    return;
                }
                // Remote work waits while a local tool is active: a larger bounded inbox.
                SyncInbox.Push(_sink, command, NetInboxCap);
            }
        }
    }
}
