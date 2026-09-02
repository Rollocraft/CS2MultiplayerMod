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
    // Realizing a host occupancy page on a client, entry point and shared state. The rest of
    // the work is split by topic across the sibling Realize*.cs files: resolving a page's
    // properties (RealizeResolve), the staged-transfer and retirement sweeps (RealizeStaging),
    // applying one property (RealizeProperty), matching a client's own pre-join households
    // (RealizeBootstrap), and the household, vehicle, citizen, creation, move-in and support
    // halves that follow.
    public partial class ResidentialOccupancySyncSystem
    {
        /// <summary>
        /// Citizen flag bits the host owns: who the person is. The rest of the word is local
        /// behaviour — walking to an outside connection, looking for a partner, riding a bicycle —
        /// and overwriting it would interrupt whatever this machine's citizen is in the middle of.
        /// </summary>
        private const short HostOwnedCitizenFlags =
            (short)(CitizenFlags.AgeBit1 | CitizenFlags.AgeBit2 | CitizenFlags.Male |
                    CitizenFlags.EducationBit1 | CitizenFlags.EducationBit2 |
                    CitizenFlags.EducationBit3 | CitizenFlags.FailedEducationBit1 |
                    CitizenFlags.FailedEducationBit2 | CitizenFlags.Tourist |
                    CitizenFlags.Commuter);

        // MovedIn is deliberately local: setting the host's bit before this peer's residents
        // arrive suppresses the native CitizensMovedIn statistic and leaves population short.
        private const byte HouseholdFlagMask =
            (byte)(HouseholdFlags.Tourist | HouseholdFlags.Commuter);

        /// <summary>
        /// Simulation frames a freshly created household is left alone for. Its citizens and pets
        /// only enter their buffers when the game's own initialization systems run, one frame
        /// later; counting members before that would create the same family twice.
        /// </summary>
        private const uint SettleFrames = 2 * UpdateIntervalFrames;

        /// <summary>Cap on the queue of just-changed properties; the bucket rotation is the backstop.</summary>
        private const int MaxDirtyProperties = 8192;

        /// <summary>
        /// How long a household with nowhere to live is left alone before it is retired. Long
        /// enough that a client which just loaded the host's world does not evict the families the
        /// host is in the middle of re-housing.
        /// </summary>
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
        /// <summary>
        /// What the rolling walk last left a property at. See <see cref="IsReconciled"/>; the pair
        /// is what lets the walk skip a property nothing has said anything new about.
        /// </summary>
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

        /// <summary>
        /// Turn arrived pages into resolved cache entries. Read-only against ECS and cheap enough
        /// to run from the city-state pump every frame; the structural writes stay in
        /// <see cref="ApplyPending"/>, which runs at the simulation cadence.
        /// </summary>
        internal void PumpIncoming()
        {
            MultiplayerService service = Mod.Service;
            if (service == null || !service.GameplaySyncReady) return;
            if (service.Session.Role == SessionRole.Host)
            {
                DropIncomingPages();
                return;
            }

            long now = service.NowMs;
            bool retryDue = _pending.Count > 0 && now >= _nextPendingPumpMs;
            if (_incoming.IsEmpty && !retryDue) return;

            ObjectSearch.Batch search = _objectSearch.BeginBatch();
            var candidates = new NativeList<Entity>(16, Allocator.Temp);
            try
            {
                DrainIncoming(now, search, candidates, MaxPumpPages);
                if (retryDue)
                {
                    RetryPending(now, search, candidates);
                    _nextPendingPumpMs = now + ResolveRetryMs;
                }
            }
            finally
            {
                candidates.Dispose();
            }
        }

        private void DrainIncoming(long now, ObjectSearch.Batch search,
            NativeList<Entity> candidates, int maxPages)
        {
            ResidentialOccupancySnapshot snapshot;
            int pages = 0;
            while (pages < maxPages && _incoming.TryDequeue(out snapshot))
            {
                pages++;
                _receivedPages++;
                bool trackedSweep = NotePageContinuity(snapshot);
                for (int i = 0; i < snapshot.Departures.Count; i++)
                    ObserveDepartureRecord(snapshot.Departures[i], snapshot.SweepId);
                for (int i = 0; i < snapshot.CitizenDepartures.Count; i++)
                    ObserveCitizenDepartureRecord(snapshot.CitizenDepartures[i], snapshot.SweepId);
                for (int i = 0; i < snapshot.Properties.Count; i++)
                    ResolveOrPend(snapshot.Properties[i], snapshot.SweepId, now, search, candidates);
                if (trackedSweep && snapshot.EndOfSweep && snapshot.SweepComplete &&
                    _clientSweepIntact &&
                    snapshot.SweepId == _clientSweepId &&
                    snapshot.PageIndex + 1 == _clientNextPage)
                    PruneCacheAfterCompleteSweep(snapshot.SweepId,
                        snapshot.RevisionWatermark);
            }
        }

        private bool NotePageContinuity(ResidentialOccupancySnapshot snapshot)
        {
            if (snapshot.SweepId != _clientSweepId)
            {
                if (_clientSweepId != 0 && !IsNewerSerial(snapshot.SweepId, _clientSweepId))
                    return false;
                _clientSweepId = snapshot.SweepId;
                _clientNextPage = 0;
                _clientSweepIntact = snapshot.PageIndex == 0;
            }
            if (snapshot.PageIndex != _clientNextPage) _clientSweepIntact = false;
            if (snapshot.PageIndex >= _clientNextPage)
                _clientNextPage = snapshot.PageIndex + 1;
            return true;
        }

        private static bool IsNewerSerial(uint candidate, uint current) =>
            candidate != current && unchecked((int)(candidate - current)) > 0;
    }
}
