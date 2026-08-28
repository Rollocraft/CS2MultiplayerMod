using System;
using System.Collections.Generic;
using System.Diagnostics;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.Infrastructure;
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
    // Two sweeps that run alongside the apply pass: keeping a household staged mid-transfer
    // linked to somewhere it can live, and retiring the citizens and households the host's
    // roster no longer accounts for.
    public partial class ResidentialOccupancySyncSystem
    {
        private void RepairStagedTransfers()
        {
            uint now = _simulationSystem.frameIndex;
            PruneStagedTransferCooldowns(now);
            if (_stagedTransfers.Count == 0) return;
            _stagedTransferScratch.Clear();
            foreach (KeyValuePair<ulong, StagedTransfer> pair in _stagedTransfers)
            {
                ulong householdId = pair.Key;
                StagedTransfer staged = pair.Value;
                Entity mapped, desiredDestination;
                bool mappingValid = TryResolveHousehold(householdId, out mapped) &&
                                    mapped == staged.Household;
                bool destinationValid = TryGetDesiredProperty(householdId,
                    out desiredDestination) && desiredDestination == staged.Destination;
                if (!mappingValid || !destinationValid)
                {
                    RepairStagedTransferLink(staged);
                    _stagedTransferScratch.Add(householdId);
                    continue;
                }
                if (IsHouseholdAtProperty(staged.Household, staged.Destination))
                {
                    _stagedTransferCooldownUntil.Remove(householdId);
                    _stagedTransferScratch.Add(householdId);
                    continue;
                }
                if (now - staged.StartedFrame >= UnreachableGraceFrames)
                {
                    RepairStagedTransferLink(staged);
                    StartStagedTransferCooldown(householdId, now);
                    MarkDirty(staged.Destination);
                    _stagedTransferScratch.Add(householdId);
                    continue;
                }
                MarkDirty(staged.Destination);
            }
            for (int i = 0; i < _stagedTransferScratch.Count; i++)
                _stagedTransfers.Remove(_stagedTransferScratch[i]);
            _stagedTransferScratch.Clear();
        }

        /// <summary>
        /// A staged swap removes the source buffer entry while retaining PropertyRenter until the
        /// native rent action commits. If that action never commits, repair whichever live property
        /// PropertyRenter currently names. This avoids both a missing source entry and restoring an
        /// obsolete source after the native action already selected a different valid property.
        /// </summary>
        private void RepairStagedTransferLink(StagedTransfer staged)
        {
            if (staged == null || staged.Household == Entity.Null ||
                !EntityManager.Exists(staged.Household) ||
                !EntityManager.HasComponent<Household>(staged.Household) ||
                EntityManager.HasComponent<Deleted>(staged.Household)) return;

            if (EntityManager.HasComponent<PropertyRenter>(staged.Household))
            {
                Entity current = EntityManager.GetComponentData<PropertyRenter>(staged.Household)
                    .m_Property;
                if (IsLiveProperty(current))
                {
                    IsHouseholdAtProperty(staged.Household, current);
                    MarkDirty(current);
                }
                else
                {
                    // A dangling one-way link cannot be completed. Removing it lets the ordinary
                    // desired-location reconciler enqueue a clean rent action on its next pass.
                    EntityManager.RemoveComponent<PropertyRenter>(staged.Household);
                }
            }

            if (IsLiveProperty(staged.Source)) MarkDirty(staged.Source);
            if (IsLiveProperty(staged.Destination)) MarkDirty(staged.Destination);
        }

        private void RestoreAllStagedTransferLinks()
        {
            foreach (KeyValuePair<ulong, StagedTransfer> pair in _stagedTransfers)
                RepairStagedTransferLink(pair.Value);
        }

        private void StartStagedTransferCooldown(ulong householdId, uint now)
        {
            _stagedTransferCooldownUntil[householdId] = now + UnreachableGraceFrames;
        }

        private void PruneStagedTransferCooldowns(uint now)
        {
            if (_stagedTransferCooldownUntil.Count == 0) return;
            _stagedTransferScratch.Clear();
            foreach (KeyValuePair<ulong, uint> pair in _stagedTransferCooldownUntil)
                if (!FramePrecedes(now, pair.Value)) _stagedTransferScratch.Add(pair.Key);
            for (int i = 0; i < _stagedTransferScratch.Count; i++)
                _stagedTransferCooldownUntil.Remove(_stagedTransferScratch[i]);
            _stagedTransferScratch.Clear();
        }

        private bool TrackStagedTransfer(ulong householdId, Entity household, Entity source,
            Entity destination)
        {
            StagedTransfer existing;
            if (_stagedTransfers.TryGetValue(householdId, out existing))
            {
                existing.Household = household;
                existing.Source = source;
                existing.Destination = destination;
                return true;
            }
            uint now = _simulationSystem.frameIndex;
            uint cooldownUntil;
            if (_stagedTransferCooldownUntil.TryGetValue(householdId, out cooldownUntil))
            {
                if (FramePrecedes(now, cooldownUntil)) return false;
                _stagedTransferCooldownUntil.Remove(householdId);
            }
            if (_stagedTransfers.Count >= MaxStagedTransfers) return false;
            _stagedTransfers[householdId] = new StagedTransfer
            {
                Household = household,
                Source = source,
                Destination = destination,
                StartedFrame = now,
            };
            return true;
        }

        private static bool FramePrecedes(uint frame, uint deadline) =>
            unchecked((int)(frame - deadline)) < 0;

        private void ApplyCitizenRetirements()
        {
            int remaining = MaxCitizensRetiredPerUpdate;
            while (remaining-- > 0 && _pendingCitizenRetirements.TryDequeue(out ulong citizenId))
            {
                _pendingCitizenRetirementIds.Remove(citizenId);
                DesiredCitizenLocation desired;
                if (!_desiredCitizens.TryGetValue(citizenId, out desired) || desired.Active)
                    continue;

                Entity citizen;
                if (!TryResolveCitizen(citizenId, out citizen)) continue;

                // A whole-household departure is executed by HouseholdMoveAwaySystem. Leave a
                // resident still linked to that family for the native emigration transaction; the
                // exact-person path below is for death/individual departure and detached strays.
                DesiredHouseholdLocation householdLocation;
                Entity household;
                if (desired.HouseholdId != 0 &&
                    _desiredHouseholds.TryGetValue(desired.HouseholdId, out householdLocation) &&
                    !householdLocation.Active &&
                    TryResolveHousehold(desired.HouseholdId, out household) &&
                    CitizenBelongsToHousehold(citizen, household))
                {
                    if (EntityManager.HasComponent<PropertyRenter>(household))
                    {
                        Entity property = EntityManager.GetComponentData<PropertyRenter>(household)
                            .m_Property;
                        if (property != Entity.Null && EntityManager.Exists(property))
                            MarkDirty(property);
                    }
                    continue;
                }

                UnbindCitizen(citizenId);
                if (!EntityManager.HasComponent<Deleted>(citizen))
                    EntityManager.AddComponent<Deleted>(citizen);
                _removedCitizens++;
            }
        }

        /// <summary>
        /// On a client every household that belongs in the city is a renter the host reported.
        /// Nothing re-houses the rest: the system that would is held, so a family whose building
        /// was demolished would otherwise stay in the city forever with no home and no way out.
        /// After a grace period they take the game's ordinary emigration path.
        /// </summary>
        private void SweepUnreachableHouseholds()
        {
            if (_unreachableHouseholds.IsEmptyIgnoreFilter)
            {
                if (_unreachableSince.Count > 0) _unreachableSince.Clear();
                return;
            }
            uint now = _simulationSystem.frameIndex;
            NativeArray<Entity> households = default(NativeArray<Entity>);
            try
            {
                households = _unreachableHouseholds.ToEntityArray(Allocator.Temp);
                _unreachableSeen.Clear();
                int retired = 0;
                for (int i = 0; i < households.Length; i++)
                {
                    Entity household = households[i];
                    _unreachableSeen.Add(household);
                    if (IsSettling(household)) continue;
                    ulong householdId;
                    PropertyRentIdentity desiredIdentity;
                    bool bound = TryGetBoundHouseholdId(household, out householdId);
                    if (bound && IsHouseholdDesiredUnhoused(householdId))
                    {
                        _unreachableSince.Remove(household);
                        continue;
                    }
                    if (bound && TryGetDesiredPropertyIdentity(householdId, out desiredIdentity) &&
                        (TryGetDesiredProperty(householdId, out Entity desiredProperty) ||
                         _pending.ContainsKey(desiredIdentity)))
                    {
                        // The destination may still be unresolved or its native rent action may be
                        // pending. A positive host location always outranks the local homeless scan.
                        _unreachableSince.Remove(household);
                        continue;
                    }
                    uint since;
                    if (!_unreachableSince.TryGetValue(household, out since))
                    {
                        _unreachableSince[household] = now;
                        continue;
                    }
                    if (now - since < UnreachableGraceFrames) continue;
                    if (retired >= MaxUnreachableRetiredPerUpdate) continue;
                    if (!Retire(household)) continue;
                    _unreachableSince.Remove(household);
                    UnbindDepartingHousehold(household);
                    retired++;
                    _retiredHouseholds++;
                }
                PruneUnreachable();
            }
            finally
            {
                if (households.IsCreated) households.Dispose();
                _unreachableSeen.Clear();
            }
        }

        /// <summary>Forget households that found a home again, so their grace period restarts.</summary>
        private void PruneUnreachable()
        {
            if (_unreachableSince.Count == 0) return;
            _settlingScratch.Clear();
            foreach (KeyValuePair<Entity, uint> pair in _unreachableSince)
                if (!_unreachableSeen.Contains(pair.Key)) _settlingScratch.Add(pair.Key);
            for (int i = 0; i < _settlingScratch.Count; i++)
                _unreachableSince.Remove(_settlingScratch[i]);
            _settlingScratch.Clear();
        }
    }
}
