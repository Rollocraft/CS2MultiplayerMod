using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.Infrastructure;
using Game;
using Game.Buildings;
using Game.Common;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    // The work that runs around the apply pass rather than inside it: holding the client's own
    // household decisions at the lifecycle boundary, the page plumbing either peer needs, the host
    // sweep's revision counter, and the periodic stats line.
    public partial class ResidentialOccupancySyncSystem
    {
        /// <summary>
        /// Kept engaged from the city-state pump as well as from <see cref="OnUpdate"/>. The
        /// GameSimulation phase stops ticking the moment a player pauses, so a client that leaves
        /// a session while paused would otherwise keep its household systems held forever.
        /// </summary>
        internal void MaintainAuthority()
        {
            MultiplayerService service = Mod.Service;
            if (service == null || service.Session.Role != SessionRole.Client)
            {
                RestoreLocalAuthority();
                return;
            }
            ApplyLocalAuthority(service.Session);
        }

        /// <summary>
        /// Called every simulation frame immediately before the native move-away consumer. The
        /// main occupancy system runs at a wider interval and can otherwise miss a short-lived
        /// MovingAway entity entirely.
        /// </summary>
        internal void ProcessHouseholdLifecycleBoundary()
        {
            MultiplayerService service = Mod.Service;
            if (service == null || !service.GameplaySyncReady) return;
            if (service.Session.Role == SessionRole.Host)
                ScanHostDepartures(service.NowMs);
            else
                CancelClientLifecycleDecisions();
        }

        /// <summary>
        /// HouseholdBehaviorSystem must run locally because it produces shopping needs and car
        /// demand. It also proposes moves. Remove only those proposals at the last safe boundary;
        /// retirements explicitly requested by the received host roster are whitelisted.
        /// </summary>
        private void CancelClientLifecycleDecisions()
        {
            // This runs every simulation frame. A large city proposes hundreds of these decisions
            // per frame, and cancelling them one entity at a time makes each removal its own
            // structural change; both cancellations are therefore issued in bulk.
            if (!_departingHouseholds.IsEmptyIgnoreFilter)
            {
                if (_authorizedMoveAways.Count == 0)
                {
                    EntityManager.RemoveComponent<global::Game.Agents.MovingAway>(
                        _departingHouseholds);
                }
                else
                {
                    NativeArray<Entity> departures =
                        _departingHouseholds.ToEntityArray(Allocator.Temp);
                    NativeList<Entity> cancelled =
                        new NativeList<Entity>(departures.Length, Allocator.Temp);
                    try
                    {
                        for (int i = 0; i < departures.Length; i++)
                        {
                            Entity household = departures[i];
                            if (_authorizedMoveAways.Contains(household)) continue;
                            cancelled.Add(household);
                        }
                        if (cancelled.Length > 0)
                            EntityManager.RemoveComponent<global::Game.Agents.MovingAway>(
                                cancelled.AsArray());
                    }
                    finally
                    {
                        cancelled.Dispose();
                        departures.Dispose();
                    }
                }
            }

            // PropertySeeker is enableable, so this query holds exactly the households whose flag
            // is set - including any that were departing above. Clearing the bits a chunk at a
            // time replaces one main-thread call per family.
            if (!_clientPropertySeekers.IsEmptyIgnoreFilter)
                EntityManager.SetComponentEnabled<global::Game.Agents.PropertySeeker>(
                    _clientPropertySeekers, false);

            if (_authorizedMoveAways.Count <= 4096) return;
            _authorizedMoveAwayScratch.Clear();
            foreach (Entity household in _authorizedMoveAways)
                if (!EntityManager.Exists(household) || EntityManager.HasComponent<Deleted>(household))
                    _authorizedMoveAwayScratch.Add(household);
            for (int i = 0; i < _authorizedMoveAwayScratch.Count; i++)
                _authorizedMoveAways.Remove(_authorizedMoveAwayScratch[i]);
            _authorizedMoveAwayScratch.Clear();
        }

        /// <summary>
        /// The channel's reset. Called both when a session ends and on an in-session world
        /// replacement, so authority is only handed back in the first case.
        /// </summary>
        internal void ResetPending()
        {
            DrainForWorldChange();
            MultiplayerService service = Mod.Service;
            if (service != null && service.Session.Role == SessionRole.Client)
                ApplyLocalAuthority(service.Session);
            else if (service == null || !service.GameplaySyncReady)
                RestoreLocalAuthority();
        }

        /// <summary>Called by the state channel on the receiving side; never requests a resync.</summary>
        internal void Enqueue(ResidentialOccupancySnapshot snapshot)
        {
            if (snapshot == null) return;
            lock (_incoming)
            {
                _incoming.Enqueue(snapshot);
                while (_incoming.Count > MaxIncomingPages)
                {
                    ResidentialOccupancySnapshot dropped;
                    if (!_incoming.TryDequeue(out dropped)) break;
                    _droppedPages++;
                }
            }
        }

        internal void DrainForWorldChange()
        {
            lock (_incoming) SyncInbox.Clear(_incoming);
            RestoreAllStagedTransferLinks();
            _cache.Clear();
            _cacheScratch.Clear();
            _authorizedMoveAways.Clear();
            _authorizedMoveAwayScratch.Clear();
            ClearBuckets(_cacheBuckets);
            ClearBucketSets(_cacheBucketMembers);
            Array.Clear(_cacheBucketCursor, 0, _cacheBucketCursor.Length);
            _dirty.Clear();
            _dirtyMembers.Clear();
            _pending.Clear();
            PropertyRentIdentity discardedPending;
            while (_pendingOrder.TryDequeue(out discardedPending)) { }
            _pendingMoveIns.Clear();
            ulong discardedMoveIn;
            while (_pendingMoveInOrder.TryDequeue(out discardedMoveIn)) { }
            _stagedTransfers.Clear();
            _stagedTransferCooldownUntil.Clear();
            _stagedTransferScratch.Clear();
            _pendingCitizenRetirementIds.Clear();
            ulong discardedCitizenRetirement;
            while (_pendingCitizenRetirements.TryDequeue(out discardedCitizenRetirement)) { }
            _settling.Clear();
            _unreachableSince.Clear();
            _unboundHouseholdSince.Clear();
            _unboundCitizenSince.Clear();
            _bootstrapHouseholdIndex.Clear();
            _bootstrapCitizenIndex.Clear();
            _bootstrapIdentityIndexBuilt = false;
            _unreachableSeen.Clear();
            _localHouseholds.Clear();
            _memberScratch.Clear();
            _claimedHouseholds.Clear();
            _claimedCitizens.Clear();
            _claimedPets.Clear();
            _wantedHouseholdIds.Clear();
            _wantedCitizenIds.Clear();
            _missingPetPrefabs.Clear();
            _localVehiclePrefabCounts.Clear();
            _matchedVehiclePrefabCounts.Clear();
            _vehicleSpawnWarnings.Clear();
            _arrivalSources.Clear();
            _settlingScratch.Clear();
            _appliedThisUpdate.Clear();
            _reapply.Clear();
            ClearIdentityState();
            ClearRentAuthorityState();
            _economyCursor = 0;
            _applyWarned = false;
            _arrivalSourceWarned = false;
            _nextPendingPumpMs = 0;
            _prefabIndex = new PrefabIndex(_prefabSystem, _prefabs);
            _citizenCreationPrefab = Entity.Null;

            _hostObserved.Clear();
            ClearBuckets(_hostObservedBuckets);
            Array.Clear(_hostBucketInitialized, 0, _hostBucketInitialized.Length);
            Array.Clear(_hostBucketCursor, 0, _hostBucketCursor.Length);
            _traceSentRosterHashes.Clear();
            _traceReceivedRosterHashes.Clear();
            _tracePlacedHouseholds.Clear();
            _priority.Clear();
            PropertyRentIdentity discardedPriority;
            while (_priorityOrder.TryDequeue(out discardedPriority)) { }
            _hostDepartures.Clear();
            _hostDepartureOrderMembers.Clear();
            ulong discardedDeparture;
            while (_hostDepartureOrder.TryDequeue(out discardedDeparture)) { }
            _hostCitizenDepartures.Clear();
            _hostCitizenDepartureOrderMembers.Clear();
            ulong discardedCitizenDeparture;
            while (_hostCitizenDepartureOrder.TryDequeue(out discardedCitizenDeparture)) { }
            _hostCitizens.Clear();
            _hostCitizenOrderMembers.Clear();
            ulong discardedTrackedCitizen;
            while (_hostCitizenOrder.TryDequeue(out discardedTrackedCitizen)) { }
            _hostHouseholds.Clear();
            _hostHouseholdOrderMembers.Clear();
            ulong discardedTrackedHousehold;
            while (_hostHouseholdOrder.TryDequeue(out discardedTrackedHousehold)) { }
            _hostHouseholdCitizens.Clear();
            _clientSweepId = 0;
            _clientNextPage = 0;
            _clientSweepIntact = false;
            _hostCaptureRevision = 1;
            RestartHostSweep();
        }

        private static void ClearBuckets(List<Entity>[] buckets)
        {
            for (int i = 0; i < buckets.Length; i++) buckets[i].Clear();
        }

        private static void ClearBucketSets(HashSet<Entity>[] buckets)
        {
            for (int i = 0; i < buckets.Length; i++) buckets[i].Clear();
        }

        private void RestartHostSweep()
        {
            _hostSweepEntities = null;
            _captureCursor = 0;
            _capturePageIndex = 0;
            _captureSweepId = 1;
            _captureSweepHadSkips = false;
            _captureBaselineNeedsEmptyPage = false;
        }

        private ulong NextHostRevision()
        {
            ulong revision = _hostCaptureRevision++;
            if (revision != 0) return revision;
            revision = _hostCaptureRevision++;
            return revision == 0 ? 1UL : revision;
        }

        private ulong LastHostRevision()
        {
            ulong revision = _hostCaptureRevision - 1;
            return revision == 0 ? 1UL : revision;
        }

        private void AdvanceHostSweep()
        {
            _capturePageIndex = 0;
            _captureSweepId = unchecked(_captureSweepId + 1);
            if (_captureSweepId == 0) _captureSweepId = 1;
            _captureSweepHadSkips = false;
            _captureBaselineNeedsEmptyPage = false;
        }

        private void DropIncomingPages()
        {
            if (_incoming.IsEmpty) return;
            lock (_incoming)
            {
                ResidentialOccupancySnapshot ignored;
                while (_incoming.TryDequeue(out ignored)) _droppedPages++;
            }
        }

        private bool IsLiveProperty(Entity property) =>
            property != Entity.Null && EntityManager.Exists(property) &&
            EntityManager.HasComponent<Building>(property) &&
            EntityManager.HasComponent<ResidentialProperty>(property) &&
            EntityManager.HasBuffer<Renter>(property) &&
            EntityManager.HasComponent<PrefabRef>(property) &&
            EntityManager.HasComponent<global::Game.Objects.Transform>(property) &&
            EntityManager.HasComponent<UpdateFrame>(property) &&
            !EntityManager.HasComponent<Temp>(property) &&
            !EntityManager.HasComponent<Deleted>(property) &&
            !EntityManager.HasComponent<Owner>(property);

        private void ReportStats(MultiplayerSession session, long now)
        {
            if (_lastStatsMs == 0) { _lastStatsMs = now; return; }
            if (now - _lastStatsMs < StatsIntervalMs) return;
            _lastStatsMs = now;

            if (session.Role == SessionRole.Host)
            {
                int clients = 0;
                foreach (Peer peer in session.Peers) if (peer.Handshaked) clients++;
                Diagnostics.SyncLog.Write(Diagnostics.LogTopic.Residential, "Occupancy/30s host: pages=" + _sentPages + ", properties=" +
                            _sentProperties + ", bytes=" + _sentBytes + ", clients=" + clients +
                            ", estimatedFanoutBytes=" + _sentBytes * clients +
                            ", transportPendingBytes=" + session.PendingSendBytes +
                            ", changedPriority=" + _priorityChanges + ", priorityQueued=" +
                            _priority.Count + ", priorityDropped=" + _priorityDrops +
                            ", departuresTracked=" + _hostDepartures.Count +
                            ", citizenDeparturesTracked=" + _hostCitizenDepartures.Count +
                            ", captureSkipped=" + _captureSkips + ", observed=" +
                            _observedProperties + ".");
            }
            else
            {
                Diagnostics.SyncLog.Write(Diagnostics.LogTopic.Residential, "Occupancy/30s client: pages=" + _receivedPages +
                            ", queueDropped=" + _droppedPages + ", cached=" + _cache.Count +
                            ", pending=" + _pending.Count + ", resolved=" + _resolved +
                            ", unresolved=" + _unresolved + ", ambiguous=" + _ambiguous +
                            ", expired=" + _expired + ", stale=" + _stalePages +
                            ", pruned=" + _pruned + ", cacheDropped=" + _cacheDrops +
                            ", appliedProperties=" + _appliedProperties + ", households +" +
                            _createdHouseholds + "/-" + _retiredHouseholds + ", citizens +" +
                            _createdCitizens + "/-" + _removedCitizens + "/~" +
                            _rewrittenCitizens + ", pets +" + _createdPets + ", renamed=" +
                            _renamedEntities + ", vehicles +" + _createdVehicles +
                            ", rentActions=" + _rentActions +
                            ", refusedMoveIns=" + _refusedMoveIns + ", buildRatesAligned=" +
                            _alignedBuildRates + ", forcedCompletions=" + _forcedCompletions +
                            ", deferredForConstruction=" + _deferredForConstruction +
                            ", economyCorrections=" + _economyCorrections +
                            "/deferred " + _economyDeferred +
                            ", pendingMoveIns=" + _pendingMoveIns.Count + ", dirty=" +
                            _dirty.Count + ".");
            }
            _sentPages = _sentProperties = _priorityChanges = _priorityDrops = _captureSkips = 0;
            _observedProperties = 0;
            _sentBytes = 0;
            _receivedPages = _droppedPages = _resolved = _unresolved = _ambiguous = 0;
            _expired = _stalePages = _pruned = _cacheDrops = _appliedProperties = 0;
            _createdHouseholds = _createdCitizens = _createdPets = _createdVehicles = 0;
            _retiredHouseholds = _removedCitizens = _rewrittenCitizens = 0;
            _rentActions = _refusedMoveIns = 0;
            _forcedCompletions = _alignedBuildRates = _deferredForConstruction = 0;
            _renamedEntities = _economyCorrections = _economyDeferred = 0;
        }
    }
}
