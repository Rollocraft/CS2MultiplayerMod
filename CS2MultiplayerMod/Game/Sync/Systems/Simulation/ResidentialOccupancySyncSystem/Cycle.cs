using System;
using System.Collections.Generic;
using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.Infrastructure;
using Game.Buildings;
using Game.Common;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    public partial class ResidentialOccupancySyncSystem
    {
        /// <summary>Also called from the pump: GameSimulation stops while paused.</summary>
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

        /// <summary>Every frame before the native move-away consumer; MovingAway can be short-lived.</summary>
        internal void ProcessHouseholdLifecycleBoundary()
        {
            MultiplayerService service = Mod.Service;
            if (service == null || !service.SimulationSyncReady) return;
            if (service.Session.Role == SessionRole.Host)
                ScanHostDepartures(service.NowMs);
            else
                CancelClientLifecycleDecisions();
        }

        /// <summary>
        /// Strips HouseholdBehaviorSystem's move proposals; host-requested retirements are whitelisted.
        /// </summary>
        private void CancelClientLifecycleDecisions()
        {
            // Issued in bulk: one structural change per family would be one sync point each.
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

            // PropertySeeker is enableable: clear the bits a chunk at a time.
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

        /// <summary>Session end and in-session world replacement; authority returns only on session end.</summary>
        internal void ResetPending()
        {
            DrainForWorldChange();
            MultiplayerService service = Mod.Service;
            if (service != null && service.Session.Role == SessionRole.Client)
                ApplyLocalAuthority(service.Session);
            else if (service == null || !service.SimulationSyncReady)
                RestoreLocalAuthority();
        }

        /// <summary>Called by the state channel on the receiving side; never requests a resync.</summary>
        internal void Enqueue(ResidentialOccupancySnapshot snapshot)
        {
            if (snapshot != null) _droppedPages += _propertyState.Enqueue(snapshot);
        }

        // Authority is maintained from the pump too: it also runs while paused.
        bool Channels.IPagedPropertyRuntime<ResidentialOccupancySnapshot>.Capture(NetworkWriter writer) => Capture(writer);
        void Channels.IPagedPropertyRuntime<ResidentialOccupancySnapshot>.Enqueue(ResidentialOccupancySnapshot snapshot) => Enqueue(snapshot);
        void Channels.IPagedPropertyRuntime<ResidentialOccupancySnapshot>.Pump() { MaintainAuthority(); PumpIncoming(); }
        void Channels.IPagedPropertyRuntime<ResidentialOccupancySnapshot>.ResetPending() => ResetPending();

        internal void DrainForWorldChange()
        {
            lock (_incoming) SyncInbox.Clear(_incoming);
            RestoreAllStagedTransferLinks();
            _cache.Clear();
            _appliedState.Clear();
            _hostScanCadence.Reset();
            _repairScanCadence.Reset();
            _reapplyRequested.Clear();
            _cacheScratch.Clear();
            _authorizedMoveAways.Clear();
            _authorizedMoveAwayScratch.Clear();
            _lifecyclePropertyScratch.Clear();
            ClearBuckets(_cacheBuckets);
            ClearBucketSets(_cacheBucketMembers);
            Array.Clear(_cacheBucketCursor, 0, _cacheBucketCursor.Length);
            _dirty.Clear();
            _dirtyMembers.Clear();
            _propertyState.ClearPending();
            _pendingMoveIns.Clear();
            while (_pendingMoveInOrder.TryDequeue(out ulong discardedMoveIn)) { }
            _stagedTransfers.Clear();
            _stagedTransferCooldownUntil.Clear();
            _stagedTransferScratch.Clear();
            _pendingCitizenRetirementIds.Clear();
            while (_pendingCitizenRetirements.TryDequeue(out ulong discardedCitizenRetirement)) { }
            _settling.Clear();
            _unreachableSince.Clear();
            _unboundHouseholdSince.Clear();
            _unboundCitizenSince.Clear();
            _bootstrapHouseholdIndex.Clear();
            _bootstrapCitizenIndex.Clear();
            _bootstrapIdentityIndexBuilt = false;
            _unreachableSeen.Clear();
            _localHouseholds.Clear();
            _localHouseholdMembers.Clear();
            _reconciledHouseholdIds.Clear();
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
            _applyWarned = false;
            _arrivalSourceWarned = false;
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
            while (_priorityOrder.TryDequeue(out PropertyIdentity discardedPriority)) { }
            _hostDepartures.Clear();
            _hostDepartureOrderMembers.Clear();
            while (_hostDepartureOrder.TryDequeue(out ulong discardedDeparture)) { }
            _hostCitizenDepartures.Clear();
            _hostCitizenDepartureOrderMembers.Clear();
            while (_hostCitizenDepartureOrder.TryDequeue(out ulong discardedCitizenDeparture)) { }
            _hostCitizens.Clear();
            _hostCitizenOrderMembers.Clear();
            while (_hostCitizenOrder.TryDequeue(out ulong discardedTrackedCitizen)) { }
            _hostHouseholds.Clear();
            _hostRenterMembership.Reset();
            _hostHouseholdOrderMembers.Clear();
            while (_hostHouseholdOrder.TryDequeue(out ulong discardedTrackedHousehold)) { }
            _hostHouseholdCitizens.Clear();
            _propertyState.ResetSweep();
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
                Diagnostics.SyncLog.Detail(LogTopic.Residential, "Occupancy/30s host: pages=" +
                    _sentPages + ", properties=" + _sentProperties + ", bytes=" + _sentBytes +
                    ", clients=" + clients + ", estimatedFanoutBytes=" + _sentBytes * clients +
                    ", transportPendingBytes=" + session.PendingSendBytes + ", changedPriority=" +
                    _priorityChanges + ", priorityQueued=" + _priority.Count + ", priorityDropped=" +
                    _priorityDrops + ", departuresTracked=" + _hostDepartures.Count +
                    ", citizenDeparturesTracked=" + _hostCitizenDepartures.Count +
                    ", lifecyclePriority=" + _lifecyclePrioritySignals +
                    ", captureSkipped=" + _captureSkips + ", observed=" + _observedProperties +
                    ", probeSkipped=" + _probeSkipped + ".");
            }
            else
            {
                Diagnostics.SyncLog.Detail(LogTopic.Residential, "Occupancy/30s client: pages=" +
                    _receivedPages + ", queueDropped=" + _droppedPages + ", cached=" + _cache.Count +
                    ", pending=" + _pending.Count + ", resolved=" + _resolved + ", unresolved=" +
                    _unresolved + ", ambiguous=" + _ambiguous + ", expired=" + _expired + ", stale=" +
                    _stalePages + ", pruned=" + _pruned + ", cacheDropped=" + _cacheDrops +
                    ", appliedProperties=" + _appliedProperties +
                    ", reconcileSkipped=" + _reconcileSkipped + ", unchangedProperties=" +
                    _unchangedProperties + ", households +" +
                    _createdHouseholds + "/-" + _retiredHouseholds + ", citizens +" +
                    _createdCitizens + "/-" + _removedCitizens + "/~" + _rewrittenCitizens +
                    ", healthCorrections=" + _healthProblemCorrections + ", hostDeaths=" +
                    _hostDeathTransitions + ", lifecycleRepairs=" + _lifecycleRepairSignals +
                    ", renterRepairs=" + _clientRenterRepairSignals +
                    ", pets +" + _createdPets + ", renamed=" + _renamedEntities + ", vehicles +" +
                    _createdVehicles + ", rentActions=" + _rentActions + ", refusedMoveIns=" +
                    _refusedMoveIns + ", buildRatesAligned=" + _alignedBuildRates +
                    ", forcedCompletions=" + _forcedCompletions + ", prefabCorrections=" +
                    _forcedPrefabCorrections + ", deferredForConstruction=" +
                    _deferredForConstruction + ", economyCorrections=" + _economyCorrections +
                    "/deferred " + _economyDeferred + ", incomeCorrections=" +
                    _incomeCorrections + "/deferred " + _incomeDeferred +
                    ", feeInputs=" + _feeInputCorrections +
                    "/deferred " + _feeInputDeferred + ", pendingMoveIns=" +
                    _pendingMoveIns.Count +
                    ", dirty=" + _dirty.Count + ".");
            }
            _sentPages = _sentProperties = _priorityChanges = _priorityDrops = _captureSkips = 0;
            _observedProperties = _probeSkipped = _reconcileSkipped = _unchangedProperties = 0;
            _sentBytes = 0;
            _receivedPages = _droppedPages = _resolved = _unresolved = _ambiguous = 0;
            _expired = _stalePages = _pruned = _cacheDrops = _appliedProperties = 0;
            _createdHouseholds = _createdCitizens = _createdPets = _createdVehicles = 0;
            _retiredHouseholds = _removedCitizens = _rewrittenCitizens = 0;
            _healthProblemCorrections = _hostDeathTransitions = 0;
            _lifecyclePrioritySignals = _lifecycleRepairSignals = 0;
            _clientRenterRepairSignals = 0;
            _rentActions = _refusedMoveIns = 0;
            _forcedCompletions = _forcedPrefabCorrections = _alignedBuildRates = 0;
            _deferredForConstruction = 0;
            _renamedEntities = _economyCorrections = _economyDeferred = 0;
            _incomeCorrections = _incomeDeferred = 0;
            _feeInputCorrections = _feeInputDeferred = 0;
        }
    }
}
