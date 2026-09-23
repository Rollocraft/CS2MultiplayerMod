using System;
using System.Collections.Generic;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Sync.Infrastructure;
using Game.Citizens;
using Unity.Collections;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    public partial class ResidentialOccupancySyncSystem
    {
        /// <summary>Citizen flag bits the host owns; the rest is local behaviour in progress.</summary>
        private const short HostOwnedCitizenFlags =
            (short)(CitizenFlags.AgeBit1 | CitizenFlags.AgeBit2 | CitizenFlags.Male |
                    CitizenFlags.EducationBit1 | CitizenFlags.EducationBit2 |
                    CitizenFlags.EducationBit3 | CitizenFlags.FailedEducationBit1 |
                    CitizenFlags.FailedEducationBit2 | CitizenFlags.Tourist |
                    CitizenFlags.Commuter);

        // MovedIn stays local: importing it early suppresses CitizensMovedIn and undercounts population.
        private const byte HouseholdFlagMask =
            (byte)(HouseholdFlags.Tourist | HouseholdFlags.Commuter);

        /// <summary>
        /// Frames a new household is left alone: its members enter their buffers one frame later, and
        /// counting before that would create the family twice.
        /// </summary>
        private const uint SettleFrames = 2 * UpdateIntervalFrames;

        /// <summary>Cap on the queue of just-changed properties; the bucket rotation is the backstop.</summary>
        private const int MaxDirtyProperties = 8192;

        /// <summary>Grace before a homeless household is retired, so a fresh client does not evict re-housing families.</summary>
        private const uint UnreachableGraceFrames = 8192;
        private const uint BootstrapRetirementGraceFrames = 8192;

        private const int MaxUnreachableRetiredPerUpdate = 8;

        private readonly Dictionary<Entity, uint> _settling = new Dictionary<Entity, uint>();
        private readonly Dictionary<Entity, uint> _unreachableSince = new Dictionary<Entity, uint>();
        private readonly Dictionary<Entity, uint> _unboundHouseholdSince =
            new Dictionary<Entity, uint>();
        private readonly Dictionary<Entity, uint> _unboundCitizenSince =
            new Dictionary<Entity, uint>();
        private readonly Dictionary<int, List<Entity>> _bootstrapHouseholdIndex =
            new Dictionary<int, List<Entity>>();
        private readonly Dictionary<int, List<Entity>> _bootstrapCitizenIndex =
            new Dictionary<int, List<Entity>>();
        private bool _bootstrapIdentityIndexBuilt;
        private readonly List<Entity> _localHouseholds = new List<Entity>();
        private readonly HashSet<Entity> _localHouseholdMembers = new HashSet<Entity>();
        private readonly HashSet<ulong> _reconciledHouseholdIds = new HashSet<ulong>();
        private readonly List<Entity> _memberScratch = new List<Entity>();
        private readonly HashSet<Entity> _claimedHouseholds = new HashSet<Entity>();
        private readonly HashSet<Entity> _claimedCitizens = new HashSet<Entity>();
        private readonly HashSet<Entity> _claimedPets = new HashSet<Entity>();
        private readonly HashSet<ulong> _wantedHouseholdIds = new HashSet<ulong>();
        private readonly HashSet<ulong> _wantedCitizenIds = new HashSet<ulong>();
        private readonly List<string> _missingPetPrefabs = new List<string>();
        private readonly Dictionary<string, int> _localVehiclePrefabCounts =
            new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _matchedVehiclePrefabCounts =
            new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly HashSet<string> _vehicleSpawnWarnings =
            new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<Entity, Entity> _arrivalSources =
            new Dictionary<Entity, Entity>();
        /// <summary>What the rolling walk last left a property at; see <see cref="IsReconciled"/>.</summary>
        private struct AppliedState
        {
            public ulong Revision;
            public int Hash;
        }

        private readonly Dictionary<Entity, AppliedState> _appliedState =
            new Dictionary<Entity, AppliedState>();
        private readonly List<Entity> _settlingScratch = new List<Entity>();
        private readonly HashSet<Entity> _appliedThisUpdate = new HashSet<Entity>();
        private readonly HashSet<Entity> _unreachableSeen = new HashSet<Entity>();
        private readonly List<Entity> _reapply = new List<Entity>();
        private readonly HashSet<Entity> _reapplyRequested = new HashSet<Entity>();
        private readonly List<int> _bootstrapKeyScratch = new List<int>();
        private readonly Budget _budget = new Budget();
        private bool _applyWarned;
        private bool _arrivalSourceWarned;

        private sealed class Budget
        {
            public int Properties;
            public int HouseholdsCreated;
            public int CitizensCreated;
            public int VehiclesCreated;
            public int HouseholdsRetired;

            public void Reset()
            {
                Properties = 0;
                HouseholdsCreated = 0;
                CitizensCreated = 0;
                VehiclesCreated = 0;
                HouseholdsRetired = 0;
            }

            public bool Exhausted =>
                Properties >= MaxPropertiesAppliedPerUpdate ||
                HouseholdsCreated >= MaxHouseholdsCreatedPerUpdate ||
                CitizensCreated >= MaxCitizensCreatedPerUpdate ||
                VehiclesCreated >= MaxVehiclesCreatedPerUpdate ||
                HouseholdsRetired >= MaxHouseholdsRetiredPerUpdate;
        }

        /// <summary>Resolves arrived pages. Read-only; structural writes stay in <see cref="ApplyPending"/>.</summary>
        internal void PumpIncoming()
        {
            MultiplayerService service = Mod.Service;
            if (service == null || !service.SimulationSyncReady) return;
            if (service.Session.Role == SessionRole.Host)
            {
                _droppedPages += _propertyState.DropIncoming();
                return;
            }

            long now = service.NowMs;
            bool retryDue = _propertyState.RetryDue(now);
            if (_incoming.IsEmpty && !retryDue) return;

            using (var scope = new PropertySearchScope(_objectSearch))
            {
                DrainIncoming(now, scope.Batch, scope.Candidates, MaxPumpPages);
                if (!retryDue) return;
                RetryPending(now, scope.Batch, scope.Candidates);
                _propertyState.NextPendingPumpMs = now + ResolveRetryMs;
            }
        }

        private void DrainIncoming(long now, ObjectSearch.Batch search,
            NativeList<Entity> candidates, int maxPages)
        {
            _propertyState.PumpPages(maxPages, snapshot =>
            {
                _receivedPages++;
                // An older sweep may still contribute revisioned records, but never continuity or pruning.
                bool trackedSweep = _propertyState.NotePage(snapshot.SweepId, snapshot.PageIndex,
                    rejectOlder: true);
                for (int i = 0; i < snapshot.Departures.Count; i++)
                    ObserveDepartureRecord(snapshot.Departures[i], snapshot.SweepId);
                for (int i = 0; i < snapshot.CitizenDepartures.Count; i++)
                    ObserveCitizenDepartureRecord(snapshot.CitizenDepartures[i], snapshot.SweepId);
                for (int i = 0; i < snapshot.Properties.Count; i++)
                    ResolveOrPend(snapshot.Properties[i], snapshot.SweepId, now, search, candidates);
                if (trackedSweep && snapshot.EndOfSweep && snapshot.SweepComplete &&
                    _propertyState.CompletesSweep(snapshot.SweepId, snapshot.PageIndex))
                    PruneCacheAfterCompleteSweep(snapshot.SweepId,
                        snapshot.RevisionWatermark);
            });
        }
    }
}
