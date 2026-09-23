using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Game;
using Unity.Entities;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Infrastructure;
using CS2MultiplayerMod.Game.Sync.Channels;
namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Replicates city state through <see cref="IStateChannel"/> snapshots. Authoritative channels flow
    /// host to clients; editable ones accept client edits that the host arbitrates and rebroadcasts.
    /// </summary>
    public partial class CityStateSyncSystem : GameSystemBase
    {
        /// <summary>How often the host publishes a fresh snapshot.</summary>
        private const long SnapshotIntervalMs = 1000;

        /// <summary>How often a client compares its local editable state against the host's.</summary>
        private const long EditDetectIntervalMs = 250;

        /// <summary>How long a client trusts its own in-flight edit over incoming snapshots.</summary>
        private const long EditPendingTimeoutMs = 5000;
        private const int OrderedApplyPerFrame = 16;
        private const int OrderedDeferredCap = 256;

        private readonly Dictionary<byte, IStateChannel> _channels = new Dictionary<byte, IStateChannel>();
        private readonly List<IPumpedStateChannel> _pumped = new List<IPumpedStateChannel>();
        private readonly HashSet<byte> _editable = new HashSet<byte>();
        private readonly HashSet<byte> _ordered = new HashSet<byte>();
        // Newest queued snapshot per channel, rebuilt per drain.
        private readonly Dictionary<byte, StateSnapshotMessage> _newestSnapshot =
            new Dictionary<byte, StateSnapshotMessage>();
        private readonly List<byte> _newestOrder = new List<byte>();
        private readonly ConcurrentQueue<StateSnapshotMessage> _incoming = new ConcurrentQueue<StateSnapshotMessage>();
        private readonly ConcurrentQueue<StateEditMessage> _incomingEdits = new ConcurrentQueue<StateEditMessage>();
        private readonly ConcurrentQueue<StateSnapshotMessage> _orderedDeferred =
            new ConcurrentQueue<StateSnapshotMessage>();
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        // Per editable channel: what the host last sent, and our edit awaiting confirmation.
        private readonly Dictionary<byte, byte[]> _lastHostPayload = new Dictionary<byte, byte[]>();
        private readonly Dictionary<byte, PendingEdit> _pendingEdits = new Dictionary<byte, PendingEdit>();

        private Observer _observer;
        private TreeStateChannel _treeStateChannel;
        private long _lastSnapshotMs;
        private long _lastEditScanMs;
        private long _lastLogMs;
        private int _applied;
        private int _superseded;
        private bool _orderedInvalidated;
        private int _orderedPoisonRequested;

        private struct PendingEdit
        {
            public byte[] Payload;
            public long SentMs;
        }

        protected override void OnCreate()
        {
            base.OnCreate();

            // Simulation-owned values: one source of truth, host → clients.
            Register(new MoneyStateChannel());
            // No population channel (id 2 retired): the HUD count is each peer's own simulation output.
            Register(new XpStateChannel());
            Register(new MilestoneStateChannel());
            Register(new DevTreePointsStateChannel());
            Register(new TourismStateChannel());
            Register(new StatisticsStateChannel());
            // Displayed tax amounts come from taxable-income statistics, apart from the editable rates.
            Register(new TaxIncomeStateChannel());
            // The terminal accounting view is host-owned; channel 8 stays the editable fee table.
            Register(new ServiceAccountingStateChannel(
                World.GetOrCreateSystemManaged<ServiceAccountingCorrectionSystem>()));
            Register(new WeatherStateChannel());
            Register(new GameClockStateChannel());
            _treeStateChannel = new TreeStateChannel();
            Register(_treeStateChannel);
            // On a client this holds the three demand writers and feeds the host's arrays to readers.
            Register(new ZoneDemandChannel());
            // Rolling rent pages, applied between RentAdjust and rent payment.
            Register(new PropertyRentStateChannel(
                World.GetOrCreateSystemManaged<PropertyRentSyncSystem>()));
            // Residential rosters, reconciled through the game's renter pipeline.
            Register(new ResidentialOccupancyChannel(
                World.GetOrCreateSystemManaged<ResidentialOccupancySyncSystem>()));
            // Workplace tenancy, figures and goods, corrected in the frame the game recomputes them.
            Register(new CompanyStatsStateChannel(
                World.GetOrCreateSystemManaged<CompanyStatsSyncSystem>()));

            // Player-editable settings: every player may change them; the host arbitrates.
            RegisterEditable(new TaxStateChannel());
            RegisterEditable(new CityPolicyStateChannel());
            RegisterEditable(new ServiceFeeStateChannel());
            RegisterEditable(new ServiceBudgetStateChannel());
            RegisterEditable(new SimulationSpeedStateChannel());
            RegisterEditable(new LoanStateChannel());
            RegisterEditable(new CityNameStateChannel());

            SyncLog.Detail(LogTopic.City, nameof(CityStateSyncSystem) + " ready with " +
                _channels.Count + " state channel(s), " + _editable.Count + " player-editable.");

            _observer = SyncObserverBinding.Bind(
                () => new Observer(_incoming, _incomingEdits, RequestOrderedPoison,
                    channelId => _editable.Contains(channelId)),
                DrainQueues);
        }

        protected override void OnDestroy()
        {
            SyncObserverBinding.Unbind(_observer, DrainQueues);
            if (_treeStateChannel != null) _treeStateChannel.Dispose();
            base.OnDestroy();
        }

        /// <summary>Ensure a newly placed host tree is included in the next bounded snapshot.</summary>
        internal void PrioritizeTree(Entity entity)
        {
            if (_treeStateChannel != null) _treeStateChannel.Prioritize(entity);
        }

        private void Register(IStateChannel channel)
        {
            _channels[channel.ChannelId] = channel;
            if (channel is IPumpedStateChannel pumped) _pumped.Add(pumped);
            if (channel is IOrderedStateChannel) _ordered.Add(channel.ChannelId);
        }

        private void RegisterEditable(IStateChannel channel)
        {
            Register(channel);
            _editable.Add(channel.ChannelId);
        }

        protected override void OnUpdate()
        {
            using (Diagnostics.SyncProfiler.Measure("CityState"))
            {
                if (Interlocked.Exchange(ref _orderedPoisonRequested, 0) != 0)
                    PoisonOrderedStream("invalid or overflowed ordered-state ingress");
                MultiplayerService service = Mod.Service;
                if (service == null) return;

                MultiplayerSession session = service.Session;
                if (!service.GameplaySyncReady)
                {
                    // Leaving a session invalidates everything we knew about the host's state.
                    if (_lastHostPayload.Count > 0) { _lastHostPayload.Clear(); _pendingEdits.Clear(); }
                    for (int i = 0; i < _pumped.Count; i++) _pumped[i].ResetPending();
                    SyncInbox.Clear(_incoming);
                    SyncInbox.Clear(_incomingEdits);
                    SyncInbox.Clear(_orderedDeferred);
                    _newestSnapshot.Clear();
                    _newestOrder.Clear();
                    _orderedInvalidated = false;
                    Interlocked.Exchange(ref _orderedPoisonRequested, 0);
                    return;
                }

                if (session.Role == SessionRole.Host)
                {
                    ApplyIncomingEdits();
                    CaptureAndBroadcast(session);
                }
                else
                {
                    DetectLocalEdits(session);
                    ApplyIncoming();
                    PumpChannels();
                }
            }
        }

        // ---- Host ------------------------------------------------------------

        private void CaptureAndBroadcast(MultiplayerSession session)
        {
            long now = _clock.ElapsedMilliseconds;
            if (_lastSnapshotMs != 0 && now - _lastSnapshotMs < SnapshotIntervalMs) return;
            _lastSnapshotMs = now;

            int sent = 0;
            foreach (var pair in _channels)
            {
                var writer = new NetworkWriter(64);
                if (pair.Value.Capture(EntityManager, writer)) { session.SendState(pair.Key, writer.ToArray()); sent++; }
            }

            // Heartbeat every ~30 s so the log shows state replication is alive without spam.
            if (now - _lastLogMs >= 30000)
            {
                _lastLogMs = now;
                SyncLog.Detail(LogTopic.City, "CityState: broadcasting " + sent +
                    " channel(s)/snapshot to clients.");
            }
        }

        // ---- Client ------------------------------------------------------------

        /// <summary>
        /// A local edit is a capture that differs from the host's last payload and anything already sent.
        /// Runs before <see cref="ApplyIncoming"/> so a stale snapshot cannot overwrite it.
        /// </summary>
        private void DetectLocalEdits(MultiplayerSession session)
        {
            long now = _clock.ElapsedMilliseconds;
            if (now - _lastEditScanMs < EditDetectIntervalMs) return;
            _lastEditScanMs = now;

            foreach (byte channelId in _editable)
            {
                // Before the host's first snapshot we may still hold pre-join defaults.
                if (!_lastHostPayload.TryGetValue(channelId, out byte[] hostPayload)) continue;

                var writer = new NetworkWriter(64);
                if (!_channels[channelId].Capture(EntityManager, writer)) continue;
                byte[] local = writer.ToArray();

                if (BytesEqual(local, hostPayload)) { _pendingEdits.Remove(channelId); continue; }

                if (_pendingEdits.TryGetValue(channelId, out PendingEdit pending) &&
                    BytesEqual(local, pending.Payload) &&
                    now - pending.SentMs < EditPendingTimeoutMs)
                    continue; // already in flight

                _pendingEdits[channelId] = new PendingEdit { Payload = local, SentMs = now };
                session.SendStateEdit(channelId, local);
                SyncLog.Detail(LogTopic.City, "CityState: local edit on channel " + channelId +
                    " sent to host.");
            }
        }

        /// <summary>Bridges session callbacks (sim thread) into this system's queues.</summary>
        private sealed class Observer : SessionObserver
        {
            private const int EditIngressCap = 256;
            private readonly ConcurrentQueue<StateSnapshotMessage> _snapshots;
            private readonly ConcurrentQueue<StateEditMessage> _edits;
            private readonly System.Action _poisonOrdered;
            private readonly System.Func<byte, bool> _isEditable;

            public Observer(ConcurrentQueue<StateSnapshotMessage> snapshots,
                ConcurrentQueue<StateEditMessage> edits, System.Action poisonOrdered,
                System.Func<byte, bool> isEditable)
            {
                _snapshots = snapshots;
                _edits = edits;
                _poisonOrdered = poisonOrdered;
                _isEditable = isEditable;
            }

            public override void OnStateReceived(StateSnapshotMessage snapshot)
            {
                if (!SyncInbox.Push(_snapshots, snapshot)) _poisonOrdered();
            }
            public override void OnStateEditReceived(StateEditMessage edit)
            {
                // Non-editable channels (e.g. 19, host-only) are rejected before queuing.
                if (edit == null || !_isEditable(edit.ChannelId)) return;
                // Edits are absolute proposals: under a burst drop the excess; the next snapshot is authoritative.
                lock (_edits)
                {
                    if (_edits.Count >= EditIngressCap) return;
                    _edits.Enqueue(edit);
                }
            }
        }

        private void DrainQueues()
        {
            SyncInbox.Clear(_incoming);
            SyncInbox.Clear(_incomingEdits);
            _newestSnapshot.Clear();
            _newestOrder.Clear();
            SyncInbox.Clear(_orderedDeferred);
            _orderedInvalidated = false;
            Interlocked.Exchange(ref _orderedPoisonRequested, 0);
            _lastHostPayload.Clear();
            _pendingEdits.Clear();
            for (int i = 0; i < _pumped.Count; i++) _pumped[i].ResetPending();
        }

        private void RequestOrderedPoison() => Interlocked.Exchange(ref _orderedPoisonRequested, 1);

        private void PoisonOrderedStream(string reason)
        {
            if (_orderedInvalidated) return;
            _orderedInvalidated = true;
            SyncInbox.Clear(_orderedDeferred);
            // The ordered stream is revisioned: after a lost page nothing local can catch up.
            SyncInbox.RequestResync(CS2MultiplayerMod.Game.Diagnostics.ResyncReport
                .Create(reason, "city-state", CS2MultiplayerMod.Game.Diagnostics.ResyncEvidence.StreamLoss)
                .About("ordered city-state stream")
                .Tried("nothing - the ordered stream was invalidated and its deferred pages dropped"));
        }
    }
}
