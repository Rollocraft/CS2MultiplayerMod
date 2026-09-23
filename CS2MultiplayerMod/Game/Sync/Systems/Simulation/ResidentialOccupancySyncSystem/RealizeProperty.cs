using CS2MultiplayerMod.Game.Sync.Infrastructure;
using System;
using System.Collections.Generic;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Commands;
using Game.Buildings;
using Game.Citizens;
using Game.Common;
using Game.Prefabs;
using Game.Simulation;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    public partial class ResidentialOccupancySyncSystem
    {
        /// <summary>
        /// Reconciles a window of one cached partition per update, resuming where it stopped. Membership
        /// is maintained while compacting; <see cref="AddToCacheBucket"/> already refuses duplicates.
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
                if (!_cache.TryGetValue(property, out CachedProperty cached))
                {
                    members.Remove(property);
                    continue;
                }
                // Stale entry left behind by a partition move; the live cache belongs to the new bucket.
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
                if (IsReconciled(property, cached))
                {
                    _reconcileSkipped++;
                    continue;
                }
                ApplyOne(property);
            }
            // Reconciling can append to this bucket: drop only the window's gap.
            if (write < end) entities.RemoveRange(write, end - write);
            _cacheBucketCursor[bucket] = write >= entities.Count ? 0 : write;
        }

        private void ApplyOne(Entity property)
        {
            // Once per update: a second reconcile would create a household whose move-in is still queued.
            if (!_appliedThisUpdate.Add(property)) return;
            if (!_cache.TryGetValue(property, out CachedProperty cached)) return;
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
                // One malformed property must not stop the reconcile; drop its cache to re-resolve it.
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
            else if (applied)
            {
                // Only a finished reconcile counts as settled.
                if (!_reapplyRequested.Contains(property) && !_budget.Exhausted)
                    NoteReconciled(property, cached);
                else _appliedState.Remove(property);
            }
            _budget.Properties++;
            _appliedProperties++;
        }

        /// <summary>
        /// Skips a property while the host revision has not advanced and the local roster still hashes to
        /// what we last left. The dirty queue ignores this, so real changes still reconcile at once.
        /// </summary>
        private bool IsReconciled(Entity property, CachedProperty cached)
        {
            if (!_appliedState.TryGetValue(property, out AppliedState state) ||
                state.Revision != cached.Revision) return false;
            return TryHashProperty(property, out int hash) && hash == state.Hash;
        }

        private void NoteReconciled(Entity property, CachedProperty cached)
        {
            if (!TryHashProperty(property, out int hash))
            {
                _appliedState.Remove(property);
                return;
            }
            _appliedState[property] = new AppliedState
            {
                Revision = cached.Revision,
                Hash = hash,
            };
        }

        /// <summary>
        /// Valid while the same residential building stands on the same spot; the prefab may change when
        /// it levels up.
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
            _reconciledHouseholdIds.Clear();
            _claimedHouseholds.Clear();
            _wantedHouseholdIds.Clear();

            // Bind a fresh world's identical families by fingerprint once; afterwards only by host id.
            for (int i = 0; i < wanted.Length; i++)
            {
                OccupancyHousehold desired = wanted[i];
                if (desired.Departing)
                {
                    if (!TryResolveHousehold(desired.HouseholdId, out Entity leaving))
                    {
                        leaving = FindBootstrapHousehold(desired);
                        if (leaving != Entity.Null)
                            BindHousehold(desired.HouseholdId, leaving);
                    }
                    // Not claimed: the unmatched pass below runs the native move-away for this identity.
                    continue;
                }
                if (!IsHouseholdDesiredHere(desired.HouseholdId, property)) continue;
                _wantedHouseholdIds.Add(desired.HouseholdId);

                if (!TryResolveHousehold(desired.HouseholdId, out Entity household))
                {
                    household = FindBootstrapHousehold(desired);
                    if (household != Entity.Null) BindHousehold(desired.HouseholdId, household);
                }
                if (household == Entity.Null) continue;
                _claimedHouseholds.Add(household);

                // Membership was verified during collection.
                if (!_localHouseholdMembers.Contains(household) &&
                    !IsHouseholdAtProperty(household, property)) continue;
                CancelUnauthorizedDeparture(household);
                ApplyHousehold(household, property, desired);
                NotePlacedHousehold(cached, desired, household);
                _reconciledHouseholdIds.Add(desired.HouseholdId);
            }

            bool settling = IsSettling(property);
            if (settling) ScheduleReapply(property);

            // No moving into a local building site when the host's is finished; completion lands next update.
            bool hostFinished = cached.ConstructionSpeed == 0;
            bool deferMoveIns = localUnderConstruction && hostFinished;
            if (deferMoveIns)
            {
                _deferredForConstruction++;
                ScheduleReapply(property);
            }

            // Identities placed elsewhere transfer through the rent queue; absent ones take move-away.
            for (int i = 0; i < _localHouseholds.Count; i++)
            {
                Entity local = _localHouseholds[i];
                if (_claimedHouseholds.Contains(local)) continue;

                bool hasLocalId = TryGetBoundHouseholdId(local, out ulong localId);
                if (hasLocalId &&
                    TryGetDesiredPropertyIdentity(localId, out PropertyIdentity desiredIdentity) &&
                    TryGetPropertyIdentity(property, out PropertyIdentity localIdentity))
                {
                    if (desiredIdentity.Equals(localIdentity))
                    {
                        // Preserve identity until a move page arrives or the host marks it departing.
                        ScheduleReapply(property);
                        continue;
                    }

                    if (!TryGetDesiredProperty(localId, out Entity destination) ||
                        destination == property || !CanStageTransferTo(destination))
                    {
                        // Keep the native two-way source link intact until the destination exists locally.
                        ScheduleReapply(property);
                        if (destination != Entity.Null && destination != property)
                            MarkDirty(destination);
                        continue;
                    }

                    // Stage an outgoing transfer by freeing only the source buffer slot. Keep the
                    // PropertyRenter component until the native rent action changes it.
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
                    // A vanished shell may be a split: wait for destination rosters or citizen tombstones.
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
                // Only outstanding move-ins need this second pass.
                if (_reconciledHouseholdIds.Contains(desired.HouseholdId)) continue;
                if (desired.Departing) continue;
                if (!IsHouseholdDesiredHere(desired.HouseholdId, property)) continue;

                if (TryResolveHousehold(desired.HouseholdId, out Entity existing))
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
                    // Fewer homes than the host's building, normally a level change not yet here. Retried.
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
        /// Adopts the host's build rate (drawn per machine) and forces completion once the host is done,
        /// so the building finishes at the same time on both peers. Returns whether it is still a site.
        /// </summary>
        private bool ApplyConstruction(Entity property, CachedProperty cached)
        {
            byte hostSpeed = cached.ConstructionSpeed;
            bool localConstructing =
                EntityManager.HasComponent<global::Game.Objects.UnderConstruction>(property);

            if (hostSpeed != 0)
            {
                // While the host builds, only the clock is aligned; the level command owns the target.
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

            bool canRepairPrefab = _prefabIndex.TryResolve(cached.Identity.PrefabName,
                    candidate => EntityManager.HasComponent<BuildingPropertyData>(candidate) &&
                                 EntityManager.HasComponent<SpawnableBuildingData>(candidate) &&
                                 !EntityManager.HasComponent<SignatureBuildingData>(candidate),
                    out Entity hostPrefab) && hostPrefab != Entity.Null;
            if (canRepairPrefab)
            {
                global::Game.Objects.UnderConstruction completion = localConstructing
                    ? EntityManager.GetComponentData<global::Game.Objects.UnderConstruction>(property)
                    : default(global::Game.Objects.UnderConstruction);
                // Already queued for the construction system.
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

            // Non-growables are not safe prefab-replacement targets.
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
            _localHouseholdMembers.Clear();
            var renters = new BufferEdit<Renter>(EntityManager, property);
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
                if (!_localHouseholdMembers.Add(renter))
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
