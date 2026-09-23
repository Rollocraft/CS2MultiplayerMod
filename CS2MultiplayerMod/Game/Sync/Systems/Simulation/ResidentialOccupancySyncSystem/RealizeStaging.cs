using System.Collections.Generic;
using CS2MultiplayerMod.Game.Sync.Commands;
using Game.Buildings;
using Game.Citizens;
using Game.Common;
using Unity.Collections;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
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
                bool mappingValid = TryResolveHousehold(householdId, out Entity mapped) &&
                                    mapped == staged.Household;
                bool destinationValid = TryGetDesiredProperty(householdId,
                    out Entity desiredDestination) && desiredDestination == staged.Destination;
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
        /// If a staged swap's rent action never commits, repair whichever live property PropertyRenter
        /// names now.
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
                    // A dangling one-way link cannot be completed; the reconciler enqueues a clean one.
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

        private void StartStagedTransferCooldown(ulong householdId, uint now) =>
            _stagedTransferCooldownUntil[householdId] = now + UnreachableGraceFrames;

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
            if (_stagedTransfers.TryGetValue(householdId, out StagedTransfer existing))
            {
                existing.Household = household;
                existing.Source = source;
                existing.Destination = destination;
                return true;
            }
            uint now = _simulationSystem.frameIndex;
            if (_stagedTransferCooldownUntil.TryGetValue(householdId, out uint cooldownUntil))
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
                if (!_desiredCitizens.TryGetValue(citizenId, out DesiredCitizenLocation desired) || desired.Active)
                    continue;

                if (!TryResolveCitizen(citizenId, out Entity citizen)) continue;

                // Whole-household departures belong to HouseholdMoveAwaySystem.
                if (desired.HouseholdId != 0 &&
                    _desiredHouseholds.TryGetValue(desired.HouseholdId,
                        out DesiredHouseholdLocation householdLocation) &&
                    !householdLocation.Active &&
                    TryResolveHousehold(desired.HouseholdId, out Entity household) &&
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
        /// The system that re-houses families is held on a client, so a homeless household emigrates
        /// after a grace period.
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
                    bool bound = TryGetBoundHouseholdId(household, out ulong householdId);
                    if (bound && IsHouseholdDesiredUnhoused(householdId))
                    {
                        _unreachableSince.Remove(household);
                        continue;
                    }
                    if (bound && TryGetDesiredPropertyIdentity(householdId, out PropertyIdentity desiredIdentity) &&
                        (TryGetDesiredProperty(householdId, out Entity desiredProperty) ||
                         _pending.ContainsKey(desiredIdentity)))
                    {
                        // A positive host location outranks the local homeless scan.
                        _unreachableSince.Remove(household);
                        continue;
                    }
                    if (!_unreachableSince.TryGetValue(household, out uint since))
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
