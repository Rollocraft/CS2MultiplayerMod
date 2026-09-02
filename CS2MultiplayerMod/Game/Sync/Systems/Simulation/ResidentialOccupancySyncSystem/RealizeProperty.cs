using System;
using System.Collections.Generic;
using System.Diagnostics;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Commands;
using Game.Agents;
using Game.Buildings;
using Game.Citizens;
using Game.Common;
using Game.Companies;
using Game.Economy;
using Game.Prefabs;
using Game.Simulation;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    // Applying one property's roster: walk a bucket, check the cached page still describes the
    // property in front of us, then bring its households, construction state and local
    // occupants into line with what the host sent.
    public partial class ResidentialOccupancySyncSystem
    {
        /// <summary>
        /// Reconciles a window of one cached partition per update and resumes where it stopped, so
        /// the walk costs the same in a hamlet and in a metropolis. Membership is maintained as the
        /// window is compacted rather than rebuilt from the whole list: <see cref="AddToCacheBucket"/>
        /// is the only other writer, and it already refuses a duplicate.
        /// </summary>
        private void ApplyBucket(int bucket)
        {
            List<Entity> entities = _cacheBuckets[bucket];
            if (entities.Count == 0)
            {
                _cacheBucketCursor[bucket] = 0;
                return;
            }

            HashSet<Entity> members = _cacheBucketMembers[bucket];
            int start = _cacheBucketCursor[bucket];
            if (start >= entities.Count) start = 0;
            int examine = entities.Count - start;
            if (examine > MaxCachedPropertiesWalkedPerUpdate)
                examine = MaxCachedPropertiesWalkedPerUpdate;

            int end = start + examine;
            int write = start;
            for (int i = start; i < end; i++)
            {
                Entity property = entities[i];
                CachedProperty cached;
                if (!_cache.TryGetValue(property, out cached))
                {
                    members.Remove(property);
                    continue;
                }
                // A stale entry can remain in its old bucket list after a local partition move.
                // Do not delete the live cache the new bucket now owns.
                if (cached.Bucket != bucket)
                {
                    members.Remove(property);
                    continue;
                }
                if (!MatchesCachedProperty(property, cached))
                {
                    RemoveCachedProperty(property);
                    members.Remove(property);
                    continue;
                }
                int currentBucket = (int)(EntityManager
                    .GetSharedComponent<UpdateFrame>(property).m_Index % UpdatePartitions);
                if (currentBucket != cached.Bucket)
                {
                    cached.Bucket = currentBucket;
                    members.Remove(property);
                    AddToCacheBucket(currentBucket, property);
                    continue;
                }
                entities[write++] = property;
                if (_budget.Exhausted) continue;
                ApplyOne(property);
            }
            // Reconciling can append to this same bucket, so drop exactly the gap the window left
            // rather than everything past the write cursor.
            if (write < end) entities.RemoveRange(write, end - write);
            _cacheBucketCursor[bucket] = write >= entities.Count ? 0 : write;
        }

        private void ApplyOne(Entity property)
        {
            // A property can be both freshly changed and in the partition this update walks.
            // Reconciling it twice would create the same household twice, because the move-in it
            // asked for the first time is still queued.
            if (!_appliedThisUpdate.Add(property)) return;
            CachedProperty cached;
            if (!_cache.TryGetValue(property, out cached)) return;
            if (!MatchesCachedProperty(property, cached))
            {
                RemoveCachedProperty(property);
                return;
            }
            bool applied = false;
            try
            {
                ApplyProperty(property, cached);
                applied = true;
            }
            catch (Exception ex)
            {
                // One malformed property must not take the whole reconcile down. Drop its cache so
                // the next page re-resolves it from scratch.
                RemoveCachedProperty(property);
                if (!_applyWarned)
                {
                    _applyWarned = true;
                    SyncLog.Warn(LogTopic.Residential,
                        "Occupancy: reconcile failed for one property; dropped it " +
                        "until the next page (logged once): " + ex.Message);
                }
            }
            if (applied && cached.RemoveAfterApply)
            {
                if (HasResidentialRenter(property)) ScheduleReapply(property);
                else if (RemoveCachedProperty(property)) _pruned++;
            }
            _budget.Properties++;
            _appliedProperties++;
        }

        /// <summary>
        /// A cache entry stays valid as long as the same residential building still stands on the
        /// same spot. It deliberately does not require the prefab to be unchanged: a building that
        /// levels up keeps its entity and its position but swaps its prefab, and dropping the cache
        /// there would stop reconciling the house exactly when its two copies diverge.
        /// </summary>
        private bool MatchesCachedProperty(Entity property, CachedProperty cached)
        {
            if (!IsLiveProperty(property)) return false;
            if (!PositionMatchesAnchor(property, cached.Identity)) return false;
            cached.Prefab = EntityManager.GetComponentData<PrefabRef>(property).m_Prefab;
            return true;
        }

        private void ApplyProperty(Entity property, CachedProperty cached)
        {
            OccupancyHousehold[] wanted = cached.Households;
            ApplyPropertyFeeInputs(property, cached);
            bool localUnderConstruction = ApplyConstruction(property, cached);

            CollectLocalHouseholds(property);
            _claimedHouseholds.Clear();
            _wantedHouseholdIds.Clear();

            // Bind a freshly downloaded world's already-identical families by their semantic
            // fingerprint. After that one bootstrap, every reconcile is exclusively keyed by the
            // opaque host id; renter-buffer order never has identity meaning again.
            for (int i = 0; i < wanted.Length; i++)
            {
                OccupancyHousehold desired = wanted[i];
                if (desired.Departing)
                {
                    Entity leaving;
                    if (!TryResolveHousehold(desired.HouseholdId, out leaving))
                    {
                        leaving = FindBootstrapHousehold(desired);
                        if (leaving != Entity.Null)
                            BindHousehold(desired.HouseholdId, leaving);
                    }
                    // Do not claim it: the unmatched pass below invokes the native move-away
                    // lifecycle for this exact host identity.
                    continue;
                }
                if (!IsHouseholdDesiredHere(desired.HouseholdId, property)) continue;
                _wantedHouseholdIds.Add(desired.HouseholdId);

                Entity household;
                if (!TryResolveHousehold(desired.HouseholdId, out household))
                {
                    household = FindBootstrapHousehold(desired);
                    if (household != Entity.Null) BindHousehold(desired.HouseholdId, household);
                }
                if (household == Entity.Null) continue;
                _claimedHouseholds.Add(household);

                if (!IsHouseholdAtProperty(household, property)) continue;
                CancelUnauthorizedDeparture(household);
                ApplyHousehold(household, property, desired);
                NotePlacedHousehold(cached, desired, household);
            }

            bool settling = IsSettling(property);
            if (settling) ScheduleReapply(property);

            // Do not move a family into a building this peer is still putting up when the host's
            // is already finished: the two are describing different things, and the completion
            // just forced above lands on the next update anyway. Retirement and the numbers on
            // families already living here are unaffected.
            bool hostFinished = cached.ConstructionSpeed == 0;
            bool deferMoveIns = localUnderConstruction && hostFinished;
            if (deferMoveIns)
            {
                _deferredForConstruction++;
                ScheduleReapply(property);
            }

            // Remove every local identity that this absolute roster no longer places here. A
            // household desired at another resolved property is transferred through the normal
            // rent queue; an absent identity takes the ordinary move-away cleanup path.
            for (int i = 0; i < _localHouseholds.Count; i++)
            {
                Entity local = _localHouseholds[i];
                if (_claimedHouseholds.Contains(local)) continue;

                ulong localId;
                PropertyRentIdentity desiredIdentity, localIdentity;
                Entity destination;
                bool hasLocalId = TryGetBoundHouseholdId(local, out localId);
                if (hasLocalId &&
                    TryGetDesiredPropertyIdentity(localId, out desiredIdentity) &&
                    TryGetPropertyIdentity(property, out localIdentity))
                {
                    if (desiredIdentity.Equals(localIdentity))
                    {
                        // This property says "not here", but no received destination has superseded
                        // the last positive location. Preserve identity until a move page arrives or
                        // the host explicitly marks the household as departing.
                        ScheduleReapply(property);
                        continue;
                    }

                    if (!TryGetDesiredProperty(localId, out destination) ||
                        destination == property || !CanStageTransferTo(destination))
                    {
                        // Keep the native two-way source link intact until the destination exists
                        // locally. A page can arrive before its building resolves (or be the only
                        // surviving half of a move); breaking the link here would strand the
                        // household forever if that pending identity later expires.
                        ScheduleReapply(property);
                        if (destination != Entity.Null && destination != property)
                            MarkDirty(destination);
                        continue;
                    }

                    // Stage an outgoing transfer by freeing only the source buffer slot. Keep the
                    // PropertyRenter component until the native rent action changes it; this breaks
                    // full A<->B swaps without inventing a half-valid destination link.
                    if (!TrackStagedTransfer(localId, local, property, destination))
                    {
                        ScheduleReapply(property);
                        MarkDirty(destination);
                        continue;
                    }
                    RemoveRenterReference(property, local);
                    MarkDirty(destination);
                    ScheduleReapply(destination);
                    continue;
                }

                if (hasLocalId && IsHouseholdDesiredUnhoused(localId))
                {
                    ReleaseUnhousedHousehold(localId, local, property);
                    continue;
                }
                if (hasLocalId && HasActiveDesiredCitizenStillLinked(local))
                {
                    // A vanished household shell can be one side of a split. Its retained
                    // household tombstone does not prove that still-live members left the city;
                    // wait until their higher-revision destination rosters move them, or exact
                    // citizen tombstones make them inactive.
                    ScheduleReapply(property);
                    continue;
                }
                if (!hasLocalId && DeferUnboundRetirement(local, _unboundHouseholdSince))
                {
                    ScheduleReapply(property);
                    continue;
                }

                if (_budget.HouseholdsRetired >= MaxHouseholdsRetiredPerUpdate)
                {
                    ScheduleReapply(property);
                    break;
                }
                if (!Retire(local)) continue;
                _unboundHouseholdSince.Remove(local);
                RemoveRenterReference(property, local);
                UnbindDepartingHousehold(local);
                _budget.HouseholdsRetired++;
                _retiredHouseholds++;
            }

            if (settling || deferMoveIns) return;

            int free = FreeResidentialSlots(property);
            for (int i = 0; i < wanted.Length; i++)
            {
                OccupancyHousehold desired = wanted[i];
                if (desired.Departing) continue;
                if (!IsHouseholdDesiredHere(desired.HouseholdId, property)) continue;

                Entity existing;
                if (TryResolveHousehold(desired.HouseholdId, out existing))
                {
                    if (IsHouseholdAtProperty(existing, property))
                    {
                        CancelUnauthorizedDeparture(existing);
                        ApplyHousehold(existing, property, desired);
                        NotePlacedHousehold(cached, desired, existing);
                        continue;
                    }
                    if (free <= 0)
                    {
                        _refusedMoveIns++;
                        ScheduleReapply(property);
                        break;
                    }
                    if (!EnqueueRentAction(property, existing))
                    {
                        ScheduleReapply(property);
                        break;
                    }
                    CancelUnauthorizedDeparture(existing);
                    MarkSettling(existing);
                    MarkSettling(property);
                    TrackPendingMoveIn(desired, existing, property, cached.Revision, false);
                    ScheduleReapply(property);
                    free--;
                    continue;
                }

                int initialVehicleCount = desired.OwnedVehicles != null
                    ? desired.OwnedVehicles.Length : 0;
                if (_budget.HouseholdsCreated >= MaxHouseholdsCreatedPerUpdate ||
                    _budget.CitizensCreated + desired.Citizens.Length >
                    MaxCitizensCreatedPerUpdate ||
                    _budget.VehiclesCreated + initialVehicleCount >
                    MaxVehiclesCreatedPerUpdate) break;
                if (desired.Citizens.Length == 0) continue;
                if (free <= 0)
                {
                    // The local building has fewer homes than the host's - normally a level change
                    // that has not reached this peer yet. Retried on the next pass.
                    _refusedMoveIns++;
                    ScheduleReapply(property);
                    break;
                }
                Entity created = CreateHousehold(property, desired);
                if (created == Entity.Null) break;
                TrackPendingMoveIn(desired, created, property, cached.Revision, true);
                free--;
                _budget.HouseholdsCreated++;
                _createdHouseholds++;
                MarkSettling(property);
                ScheduleReapply(property);
            }
        }

        /// <summary>
        /// Keep this peer's building site in step with the host's, and report whether it is still
        /// one. The build rate is drawn independently on each machine — a house given 39 takes over
        /// twice as long as the same house given 88 — so without this the same building finishes
        /// minutes apart on the two cities. Adopting the host's rate makes them finish together;
        /// forcing completion when the host is already done closes the gap that is left.
        /// </summary>
        private bool ApplyConstruction(Entity property, CachedProperty cached)
        {
            byte hostSpeed = cached.ConstructionSpeed;
            bool localConstructing =
                EntityManager.HasComponent<global::Game.Objects.UnderConstruction>(property);

            if (hostSpeed != 0)
            {
                // While the host is building, PrefabName still describes the old prefab; only the
                // level command knows its target. Keep an existing local site's randomized clock
                // aligned, and let the later completed absolute page repair a missed target.
                if (!localConstructing) return false;
                global::Game.Objects.UnderConstruction active = EntityManager
                    .GetComponentData<global::Game.Objects.UnderConstruction>(property);
                if (active.m_Speed != hostSpeed)
                {
                    active.m_Speed = hostSpeed;
                    EntityManager.SetComponentData(property, active);
                    _alignedBuildRates++;
                }
                return true;
            }

            Entity currentPrefab = EntityManager.GetComponentData<PrefabRef>(property).m_Prefab;
            string currentName = _prefabIndex.NameOf(currentPrefab);
            if (!localConstructing &&
                string.Equals(currentName, cached.Identity.PrefabName,
                    StringComparison.Ordinal)) return false;

            Entity hostPrefab;
            bool canRepairPrefab = _prefabIndex.TryResolve(cached.Identity.PrefabName,
                    candidate => EntityManager.HasComponent<BuildingPropertyData>(candidate) &&
                                 EntityManager.HasComponent<SpawnableBuildingData>(candidate) &&
                                 !EntityManager.HasComponent<SignatureBuildingData>(candidate),
                    out hostPrefab) && hostPrefab != Entity.Null;
            if (canRepairPrefab)
            {
                global::Game.Objects.UnderConstruction completion = localConstructing
                    ? EntityManager.GetComponentData<global::Game.Objects.UnderConstruction>(property)
                    : default(global::Game.Objects.UnderConstruction);
                // Already queued for the native construction system. Do not keep rewriting the
                // component or inflate the correction counter while waiting for its partition.
                if (completion.m_NewPrefab == hostPrefab && completion.m_Progress >= 100)
                    return true;

                bool repairsWrongPrefab = currentPrefab != hostPrefab ||
                    (localConstructing && completion.m_NewPrefab != Entity.Null &&
                     completion.m_NewPrefab != hostPrefab);
                completion.m_NewPrefab = hostPrefab;
                completion.m_Progress = byte.MaxValue;
                if (completion.m_Speed == 0) completion.m_Speed = 1;
                if (localConstructing) EntityManager.SetComponentData(property, completion);
                else EntityManager.AddComponentData(property, completion);
                EntityManager.AddComponent<Updated>(property);
                if (repairsWrongPrefab) _forcedPrefabCorrections++;
                else _forcedCompletions++;
                return true;
            }

            // Non-growable residential buildings are not safe targets for prefab replacement.
            // Preserve the old completion-only behavior if one is locally still being built.
            if (!localConstructing) return false;
            global::Game.Objects.UnderConstruction site =
                EntityManager.GetComponentData<global::Game.Objects.UnderConstruction>(property);
            if (site.m_Progress < 100)
            {
                site.m_Progress = byte.MaxValue;
                EntityManager.SetComponentData(property, site);
                _forcedCompletions++;
            }
            return true;
        }

        private void CollectLocalHouseholds(Entity property)
        {
            _localHouseholds.Clear();
            DynamicBuffer<Renter> renters = EntityManager.GetBuffer<Renter>(property);
            bool changed = false;
            for (int i = renters.Length - 1; i >= 0; i--)
            {
                Entity renter = renters[i].m_Renter;
                if (renter == Entity.Null || !EntityManager.Exists(renter)) continue;
                if (!EntityManager.HasComponent<Household>(renter)) continue;
                if (EntityManager.HasComponent<Deleted>(renter) ||
                    !EntityManager.HasComponent<PropertyRenter>(renter) ||
                    EntityManager.GetComponentData<PropertyRenter>(renter).m_Property != property)
                {
                    renters.RemoveAt(i);
                    changed = true;
                    continue;
                }
                if (EntityManager.HasComponent<TouristHousehold>(renter) ||
                    EntityManager.HasComponent<CommuterHousehold>(renter)) continue;
                if (_localHouseholds.Contains(renter))
                {
                    renters.RemoveAt(i);
                    changed = true;
                    continue;
                }
                _localHouseholds.Add(renter);
            }
            if (changed) MarkRentersUpdated(property);
        }
    }
}
