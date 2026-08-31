using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.Infrastructure;
using Game.Buildings;
using Game.Citizens;
using Game.Common;
using Game.Economy;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    // Building the host's occupancy page: what goes into one message, in what order, and within
    // what budget. Priority properties go first, then departures, then whatever the rotating
    // bucket sweep turns up.
    //
    // Departures and the host-side scans are in CaptureDeparture.cs, the bucket rotation and
    // change detection in CaptureScan.cs, reading one property/household/citizen in
    // CaptureEntity.cs, and the hashing and trace lines in CaptureHash.cs.
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
            public readonly HashSet<PropertyRentIdentity> Identities =
                new HashSet<PropertyRentIdentity>();
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
            if (service == null || !service.GameplaySyncReady ||
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
                // Lifecycle proof comes first. A very dense property is allowed to exceed the soft
                // page target, so appending tombstones afterward could otherwise starve the only
                // authoritative evidence that an entity left.
                AddDepartureRecords(snapshot, budget, service.NowMs);
                AddPriorityProperties(snapshot, budget);
            }

            int index = _captureCursor;
            bool baselineAdvanced = false;
            while (index < _hostSweepEntities.Length &&
                   snapshot.Properties.Count < ResidentialOccupancySnapshot.MaxProperties &&
                   (budget.Bytes < PageByteBudget || !baselineAdvanced))
            {
                OccupancyProperty property;
                if (TryCaptureProperty(_hostSweepEntities[index], out property))
                {
                    PageAddResult result = TryAddPageEntry(snapshot, budget, property);
                    if (result == PageAddResult.Added)
                        TraceSentRoster(_hostSweepEntities[index], property);
                    if (result == PageAddResult.Full)
                    {
                        // Priority entries may already have used most of the hard page cap. Close
                        // that page without consuming this baseline entity; the following page
                        // starts empty and can always carry a valid single property.
                        if (snapshot.Properties.Count > 0 || snapshot.Departures.Count > 0 ||
                            snapshot.CitizenDepartures.Count > 0)
                        {
                            // The following capture intentionally omits priority/lifecycle extras
                            // once, guaranteeing that any individually valid baseline property can
                            // make progress even when it nearly fills the hard transport cap.
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

            // Encode before committing traversal state. Future schema changes can then fail this
            // one channel safely without consuming a baseline suffix that was never sent.
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

        /// <summary>
        /// A city with no residential property still has to close its sweep, otherwise a client
        /// that bulldozed its last house would keep the previous roster cached forever.
        /// </summary>
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
                PropertyRentIdentity identity;
                if (!_priorityOrder.TryDequeue(out identity)) break;
                Entity entity;
                if (!_priority.TryGetValue(identity, out entity)) continue;
                OccupancyProperty property;
                // Recapture at send time. The queued signal says only "this property changed";
                // retaining the old payload could let a later baseline lose to a stale priority
                // copy of the same identity in this page.
                if (!TryCaptureProperty(entity, out property))
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
