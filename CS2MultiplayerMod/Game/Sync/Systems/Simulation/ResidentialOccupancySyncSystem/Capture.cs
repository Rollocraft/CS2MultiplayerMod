using System.Collections.Generic;
using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Game.Sync.Commands;
using Unity.Collections;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    // The host's page: priority properties, then departures, then the rotating sweep.
    public partial class ResidentialOccupancySyncSystem
    {
        private static readonly int[] EmptyNameIndices = new int[0];
        private static readonly string[] EmptyVehiclePrefabs = new string[0];
        private readonly HashSet<string> _pageEntryNames = new HashSet<string>();

        private enum PageAddResult
        {
            Added,
            Duplicate,
            Full,
            Invalid,
        }

        private sealed class PageBudget
        {
            public readonly HashSet<PropertyIdentity> Identities =
                new HashSet<PropertyIdentity>();
            public readonly HashSet<ulong> HouseholdIds = new HashSet<ulong>();
            public readonly HashSet<ulong> CitizenIds = new HashSet<ulong>();
            public readonly HashSet<ulong> DepartureIds = new HashSet<ulong>();
            public readonly HashSet<ulong> CitizenDepartureIds = new HashSet<ulong>();
            public readonly HashSet<string> Names = new HashSet<string>();
            public int Bytes = 24;
            public int Households;
            public int Citizens;
            public int Pets;
            public int Vehicles;
        }

        /// <summary>Called once per city-state snapshot on the host.</summary>
        internal bool Capture(NetworkWriter writer)
        {
            if (writer == null) return false;
            MultiplayerService service = Mod.Service;
            if (service == null || !service.SimulationSyncReady ||
                service.Session.Role != Core.Session.SessionRole.Host) return false;

            if (_hostSweepEntities == null && !BeginHostSweep()) return WriteEmptySweep(writer);
            if (_captureCursor < 0 || _captureCursor >= _hostSweepEntities.Length)
            {
                _hostSweepEntities = null;
                _captureCursor = 0;
                AdvanceHostSweep();
                if (!BeginHostSweep()) return WriteEmptySweep(writer);
            }

            var snapshot = new ResidentialOccupancySnapshot
            {
                SweepId = _captureSweepId,
                PageIndex = _capturePageIndex,
            };
            var budget = new PageBudget();
            bool baselineNeedsEmptyPage = _captureBaselineNeedsEmptyPage;
            _captureBaselineNeedsEmptyPage = false;
            if (!baselineNeedsEmptyPage)
            {
                // Departure records first: a dense property may overrun the soft page target.
                AddDepartureRecords(snapshot, budget, service.NowMs);
                AddPriorityProperties(snapshot, budget);
            }

            int index = _captureCursor;
            bool baselineAdvanced = false;
            while (index < _hostSweepEntities.Length &&
                   snapshot.Properties.Count < ResidentialOccupancySnapshot.MaxProperties &&
                   (budget.Bytes < PageByteBudget || !baselineAdvanced))
            {
                if (TryCaptureProperty(_hostSweepEntities[index], out OccupancyProperty property))
                {
                    PageAddResult result = TryAddPageEntry(snapshot, budget, property);
                    if (result == PageAddResult.Added)
                        TraceSentRoster(_hostSweepEntities[index], property);
                    if (result == PageAddResult.Full)
                    {
                        // Close this page without consuming the baseline entity; the next page starts empty.
                        if (snapshot.Properties.Count > 0 || snapshot.Departures.Count > 0 ||
                            snapshot.CitizenDepartures.Count > 0)
                        {
                            // Next capture skips priority extras once, so any valid baseline property can progress.
                            _captureBaselineNeedsEmptyPage = true;
                            break;
                        }
                        _captureSkips++;
                        _captureSweepHadSkips = true;
                    }
                    else if (result == PageAddResult.Invalid)
                    {
                        _captureSkips++;
                        _captureSweepHadSkips = true;
                    }
                }
                else
                {
                    _captureSkips++;
                    _captureSweepHadSkips = true;
                }
                index++;
                baselineAdvanced = true;
            }

            bool cappedSweep = _capturePageIndex + 1 >= ResidentialOccupancySnapshot.MaxPagesPerSweep;
            snapshot.EndOfSweep = index >= _hostSweepEntities.Length || cappedSweep;
            snapshot.SweepComplete = snapshot.EndOfSweep && index >= _hostSweepEntities.Length &&
                                     !_captureSweepHadSkips;
            snapshot.RevisionWatermark = LastHostRevision();
            if (snapshot.Properties.Count == 0 && snapshot.Departures.Count == 0 &&
                snapshot.CitizenDepartures.Count == 0 && !snapshot.EndOfSweep) return false;

            // Encode before committing traversal state, so a failure does not skip unsent properties.
            byte[] encoded = snapshot.Encode();
            if (snapshot.EndOfSweep)
            {
                _hostSweepEntities = null;
                _captureCursor = 0;
                AdvanceHostSweep();
            }
            else
            {
                _captureCursor = index;
                _capturePageIndex++;
            }

            int before = writer.Length;
            writer.WriteBytes(encoded, 0, encoded.Length);
            _sentBytes += writer.Length - before;
            _sentPages++;
            _sentProperties += snapshot.Properties.Count;
            return true;
        }

        private bool BeginHostSweep()
        {
            NativeArray<Entity> properties = _properties.ToEntityArray(Allocator.Temp);
            try
            {
                if (properties.Length == 0) return false;
                _hostSweepEntities = new Entity[properties.Length];
                for (int i = 0; i < properties.Length; i++) _hostSweepEntities[i] = properties[i];
                return true;
            }
            finally { properties.Dispose(); }
        }

        /// <summary>A city with no residential property still closes its sweep, so clients prune.</summary>
        private bool WriteEmptySweep(NetworkWriter writer)
        {
            var empty = new ResidentialOccupancySnapshot
            {
                SweepId = _captureSweepId,
                PageIndex = 0,
                EndOfSweep = true,
                SweepComplete = true,
            };
            var budget = new PageBudget();
            MultiplayerService service = Mod.Service;
            AddDepartureRecords(empty, budget, service != null ? service.NowMs : 0);
            empty.RevisionWatermark = LastHostRevision();
            int before = writer.Length;
            empty.Write(writer);
            _sentBytes += writer.Length - before;
            _sentPages++;
            AdvanceHostSweep();
            return true;
        }

        private void AddPriorityProperties(ResidentialOccupancySnapshot snapshot, PageBudget budget)
        {
            int added = 0;
            while (added < PriorityPropertiesPerPage && _priorityOrder.Count > 0 &&
                   snapshot.Properties.Count < ResidentialOccupancySnapshot.MaxProperties &&
                   budget.Bytes < PriorityByteBudget)
            {
                if (!_priorityOrder.TryDequeue(out PropertyIdentity identity)) break;
                if (!_priority.TryGetValue(identity, out Entity entity)) continue;
                // Recapture at send time so a stale priority copy cannot beat a later baseline.
                if (!TryCaptureProperty(entity, out OccupancyProperty property))
                {
                    _priority.Remove(identity);
                    continue;
                }
                PageAddResult result = TryAddPageEntry(snapshot, budget, property);
                if (result == PageAddResult.Full)
                {
                    // Keep the signal for the next empty page instead of silently consuming it.
                    _priorityOrder.Enqueue(identity);
                    break;
                }
                _priority.Remove(identity);
                if (result == PageAddResult.Added)
                {
                    TraceSentRoster(entity, property);
                    added++;
                }
            }
        }
    }
}
