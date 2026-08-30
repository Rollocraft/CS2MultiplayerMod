using System.Collections.Concurrent;
using System.Collections.Generic;
using Game;
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
    // Creating the system and tearing it down, draining every queue on a world change, and the
    // observer that feeds them.
    public partial class NetSyncSystem
    {
        protected override void OnCreate()
        {
            base.OnCreate();

            // An owned connector re-cut beside an already-standing building names an owner that is
            // live, not part of the transaction. Owner resolution only ever matches a Temp to a
            // Temp, so that link has to be found by asking what stands at the described point.
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
            // Mirror the net apply pass's structural query, including any Temp already carrying
            // Deleted. The operation-level query below expands this with native side-effect domains.
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

            // Zone cell blocks are excluded: an isolated commit only ever drives the object, net,
            // area and route apply passes, none of which read Block/Cell, so a zoning preview can
            // never ride along. It is also the one preview a player builds across many frames and
            // commits in a single one (the marquee), so isolating it discards the whole gesture.
            _standingTemps = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<Temp>(),
                None = SyncQuery.ReadOnly<Deleted, global::Game.Zones.Block>(),
            });

            // Tool definitions lose Updated after the frame that materializes their preview. Those
            // untagged definitions are therefore the exact graph ToolOutputSystem consumes on Apply.
            // Sync-created definitions carry Deleted from birth and must never be recaptured.
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
                    // Exclude sub-networks owned by a road/building (the invisible
                    // pedestrian/car/road paths and lane connectors the game auto-creates).
                    ComponentType.ReadOnly<Owner>(),
                },
            });

            // Standalone net nodes we can snap incoming segment endpoints onto. Owner-less so
            // we only ever connect to real roads/paths, never to a building's or road's hidden
            // sub-network nodes; Temp/Deleted excluded so we never snap to a preview or a node
            // that is being torn down this frame.
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

            // OWNED nodes — building sub-net stubs among them. A power line / pipe endpoint may
            // connect to one of these when its net layers say so (see UtilityConnectLayers and
            // FindUtilityNodeAt); everything else keeps ignoring them, exactly like _existingNodes.
            _ownedNodes = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<Node, Owner, PrefabRef>(),
                None = SyncQuery.ReadOnly<Temp, Deleted>(),
            });

            // Owned connector edges are kept out of all fallback searches. Captured native intent
            // may target one explicitly, in which case ResolveIntent searches this separate pool.
            _ownedEdges = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<Edge, Curve, Owner, PrefabRef>(),
                None = SyncQuery.ReadOnly<Temp, Deleted>(),
            });

            // Diagnostic: pre-existing edges whose geometry CHANGED this frame (Updated but NOT
            // freshly Created) — exactly what an in-place split of the original edge looks like.
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
                // A world-sync Begin is an admission barrier, not permission to tear down work that
                // the native pipeline already owns. Drop commands which have not started, but retain
                // the armed/committing/quarantined graph and its validation/callback state so
                // RealizePending can drive it to a clean boundary before the snapshot is taken.
                SyncInbox.Clear(_incoming);
                _remoteDeferred.Clear();
                _deferredSpanPieces.Clear();
                _cachedLocalCourses.Clear();
                _cachedLocalMixedOperation.Clear();
                _cachedFallbackOriginalEdges.Clear();
                _cachedNeedsFinalEdgeFallback = false;
                _atomicMixedApplyCapturedFrame = -1;
                return;
            }

            // Never leave an isolated remote Temp transaction behind for a later local click. Which
            // side is enabled depends on whether this frame had protected the remote batch.
            // Uncommitted work is safe to clear. Once an apply pass has been scheduled, however,
            // deleting its graph manually can race native apply/cleanup jobs; quarantine it and wait
            // for its exact entities to leave Temp state instead.
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
                // Also removes a short-lived commit shield, if present. The entities themselves
                // remain untouched so the already-scheduled native transaction can finish safely.
                ReleaseTrackedTemps(_committingRemoteNetTemps);
            }
            ReleaseAllIsolation();
            SyncInbox.Clear(_incoming);
            _remoteDeferred.Clear();
            _deferredSpanPieces.Clear();
            _cachedLocalCourses.Clear();
            _cachedLocalMixedOperation.Clear();
            _cachedFallbackOriginalEdges.Clear();
            _cachedNeedsFinalEdgeFallback = false;
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
            // A world-sync barrier has already closed gameplay and drained every feeder. Keeping
            // a release-frame admission fence here could otherwise make recovery wait for another
            // ToolUpdate while the simulation is paused, even though no new native work can enter.
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
            DeferForTerrain = false;
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

                // Sample net-edge lifecycle tags every frame (peak over the 5 s window). Runs at
                // ModificationEnd where the one-frame Created/Updated/Deleted tags are still alive.
                // Each count walks every matching chunk, and the only thing they feed is a verbose
                // line - so they are not paid at all unless someone is reading it.
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
                // Remote Temp work intentionally waits while a local interactive tool is active.
                // Keep a larger, still-hard-bounded road inbox so a long local drawing gesture does
                // not immediately shed a partner's reliable ordered course stream.
                SyncInbox.Push(_sink, command, NetInboxCap);
                // Network thread: log on RECEIPT so a missing realize can be told apart from a missing
                // send. The body is the encoded Bézier; we don't decode here (cheap + thread-safe).
            }
        }
    }
}
