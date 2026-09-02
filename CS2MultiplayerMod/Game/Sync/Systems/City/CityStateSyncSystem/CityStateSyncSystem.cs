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
    /// Replicates city state via <see cref="IStateChannel"/> snapshots: host periodically
    /// captures and broadcasts; clients apply snapshots and detect edits via <see cref="StateEditMessage"/>.
    /// Two channel types: authoritative (money, XP, etc., host to clients);
    /// editable (taxes, policies, etc., client edit -> host -> broadcast). Host is arbiter.
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

        // Client-side edit tracking: what the host last sent per editable channel, and
        // the edit we shipped and are waiting to see confirmed in a snapshot.
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
            // No population channel: the HUD population count is a cosmetic output of each
            // peer's own simulation. Overwriting it once a second made the client's number
            // flicker as the local sim and the snapshot fought over it. Channel id 2 is retired.
            Register(new XpStateChannel());
            Register(new MilestoneStateChannel());
            Register(new DevTreePointsStateChannel());
            Register(new TourismStateChannel());
            Register(new StatisticsStateChannel());
            // Taxation's displayed residential/commercial/industrial/office amounts come from
            // parameterized taxable-income statistics, independently of the editable rate table.
            Register(new TaxIncomeStateChannel());
            // Fee events from every service path and the collected building/net upkeep records
            // converge into one native accounting view. Keep that terminal view host-owned while
            // channel 8 remains the separately editable fee-slider table.
            Register(new ServiceAccountingStateChannel(
                World.GetOrCreateSystemManaged<ServiceAccountingCorrectionSystem>()));
            Register(new WeatherStateChannel());
            Register(new GameClockStateChannel());
            _treeStateChannel = new TreeStateChannel();
            Register(_treeStateChannel);
            // Full native demand state. On a client the channel holds the three redundant demand
            // writers after its first valid snapshot and feeds their host arrays to native readers.
            Register(new ZoneDemandChannel());
            // Numeric rent only. The channel queues rolling absolute pages; its runtime applies
            // them in GameSimulation after vanilla recalculates rent and before rent is charged.
            Register(new PropertyRentStateChannel(
                World.GetOrCreateSystemManaged<PropertyRentSyncSystem>()));
            // Who lives in each residential building, and the people in those households. Rolling
            // absolute pages; the runtime reconciles them in GameSimulation through the game's own
            // renter pipeline. See ResidentialOccupancySyncSystem.
            Register(new ResidentialOccupancyChannel(
                World.GetOrCreateSystemManaged<ResidentialOccupancySyncSystem>()));
            // The money-facing figures behind every shop, factory and office, and the goods they
            // hold. Rolling absolute pages; the runtime corrects them in GameSimulation in the
            // same frame the game recomputes them. See CompanyStatsSyncSystem.
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
            var pumped = channel as IPumpedStateChannel;
            if (pumped != null) _pumped.Add(pumped);
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
        /// A local edit shows up as the channel capturing something different from what
        /// the host last sent (and from anything we already shipped). Runs before
        /// <see cref="ApplyIncoming"/> so a fresh edit is sent before a stale snapshot
        /// could overwrite it.
        /// </summary>
        private void DetectLocalEdits(MultiplayerSession session)
        {
            long now = _clock.ElapsedMilliseconds;
            if (now - _lastEditScanMs < EditDetectIntervalMs) return;
            _lastEditScanMs = now;

            foreach (byte channelId in _editable)
            {
                // Until the host has told us its state once, "different" means nothing —
                // we may simply still hold pre-join defaults.
                byte[] hostPayload;
                if (!_lastHostPayload.TryGetValue(channelId, out hostPayload)) continue;

                var writer = new NetworkWriter(64);
                if (!_channels[channelId].Capture(EntityManager, writer)) continue;
                byte[] local = writer.ToArray();

                if (BytesEqual(local, hostPayload)) { _pendingEdits.Remove(channelId); continue; }

                PendingEdit pending;
                if (_pendingEdits.TryGetValue(channelId, out pending) &&
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
                // Reject channel probing before it can occupy the shared edit inbox. In particular,
                // channel 19 is host-only and must never be client-injectable through StateEdit.
                if (edit == null || !_isEditable(edit.ChannelId)) return;
                // Edits are absolute proposals, not an ordered dependency stream. Under a hostile
                // burst it is safer to drop excess proposals than trigger a multi-megabyte world
                // resync; the next host snapshot remains authoritative.
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

        private void RequestOrderedPoison()
        {
            Interlocked.Exchange(ref _orderedPoisonRequested, 1);
        }

        private void PoisonOrderedStream(string reason)
        {
            if (_orderedInvalidated) return;
            _orderedInvalidated = true;
            SyncInbox.Clear(_orderedDeferred);
            // The ordered city-state stream is revisioned: once a page is lost or refused, every
            // later page describes a state this machine never reached. Nothing local supplies it.
            SyncInbox.RequestResync(CS2MultiplayerMod.Game.Diagnostics.ResyncReport
                .Create(reason, "city-state", CS2MultiplayerMod.Game.Diagnostics.ResyncEvidence.StreamLoss)
                .About("ordered city-state stream")
                .Tried("nothing - the ordered stream was invalidated and its deferred pages dropped"));
        }
    }
}
