using System.Collections.Concurrent;
using System.Collections.Generic;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Core.Sync.ModSync;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.Infrastructure;
using CS2MultiplayerMod.Game.Sync.ModSync;
using Game;
using Game.Prefabs;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Systems.Mods
{
    /// <summary>
    /// Replicates other mods' world state without knowing which mods exist: a type travels because the
    /// runtime saves it, an entity is found because it is a place in the city, and a change is noticed
    /// because its chunk was written. Joiners get the state from the savegame; this covers later edits.
    /// </summary>
    public partial class ModStateSyncSystem : GameSystemBase, IRealizeStage
    {
        private readonly ConcurrentQueue<SimulationCommandMessage> _incomingState =
            new ConcurrentQueue<SimulationCommandMessage>();
        private readonly ConcurrentQueue<SimulationCommandMessage> _incomingTable =
            new ConcurrentQueue<SimulationCommandMessage>();

        private ModSyncObserver _observer;

        private ModComponentCatalog _catalog;
        private ModTypeBinding _binding;
        private ModCarrierIdentity _identity;
        private PrefabSystem _prefabs;
        private PrefabIndex _prefabIndex;
        private global::Game.Net.SearchSystem _netSearch;
        private ObjectSearch _objectSearch;

        /// <summary>The closure hash last seen for a carrier, keyed the portable way.</summary>
        private readonly Dictionary<string, ulong> _shadow =
            new Dictionary<string, ulong>(System.StringComparer.Ordinal);
        private readonly HashSet<Entity> _roadSpeedActive = new HashSet<Entity>();

        /// <summary>Carriers we published state for: a removal can only be seen by remembering it.</summary>
        private readonly Dictionary<string, ModEntityRef> _knownCarriers =
            new Dictionary<string, ModEntityRef>(System.StringComparer.Ordinal);

        private readonly List<string> _sweepOrder = new List<string>();
        private int _sweepCursor;

        private ModTypeTable _lastReceivedTable;
        private bool _tablePublished;
        private bool _announcedCatalog;
        private long _lastReportMs;

        // What the 30-second line reports.
        private int _capturedTransactions;
        private int _sentBytes;
        private int _appliedTransactions;
        private int _unresolvedGaveUp;
        private int _rejectedClosures;
        private int _noCarrier;
        private bool _saidWaitingForTable;

        protected override void OnCreate()
        {
            base.OnCreate();

            _prefabs = World.GetOrCreateSystemManaged<PrefabSystem>();
            _netSearch = World.GetOrCreateSystemManaged<global::Game.Net.SearchSystem>();
            _objectSearch = new ObjectSearch(
                World.GetOrCreateSystemManaged<global::Game.Objects.SearchSystem>());
            _prefabIndex = new PrefabIndex(_prefabs,
                GetEntityQuery(ComponentType.ReadOnly<PrefabData>()));

            _observer = SyncObserverBinding.Bind(
                () => new ModSyncObserver(this, _incomingState, _incomingTable), DrainQueues);
        }

        protected override void OnDestroy()
        {
            SyncObserverBinding.Unbind(_observer, DrainQueues);
            base.OnDestroy();
        }

        private void DrainQueues()
        {
            SimulationCommandMessage ignored;
            while (_incomingState.TryDequeue(out ignored)) { }
            while (_incomingTable.TryDequeue(out ignored)) { }

            _shadow.Clear();
            _roadSpeedActive.Clear();
            _knownCarriers.Clear();
            _sweepOrder.Clear();
            _sweepCursor = 0;
            _held.Clear();
            _awaitingSettle.Clear();
            while (_pendingCandidates.TryDequeue(out Entity pending)) { }
            _queuedCandidates.Clear();
            _candidates.Clear();
            _lastOrderVersion = 0;
            _sweepPending = true;
            _lastComplaintTick.Clear();
            _tablePublished = false;
            _binding = null;

            _reportedOnce.Clear();
            _saidWaitingForTable = false;
            _lastReceivedTable = null;
        }

        protected override void OnUpdate()
        {
            using (SyncProfiler.Measure("ModSync"))
            {
                MultiplayerService service = Mod.Service;
                if (service == null || !service.GameplaySyncReady) return;

                MultiplayerSession session = service.Session;
                long now = service.NowMs;

                EnsureCatalog();
                NegotiateTable(session);
                if (_binding == null)
                {
                    if (!_saidWaitingForTable)
                    {
                        _saidWaitingForTable = true;
                        SyncLog.Event(LogTopic.ModSync,
                            "Mod state is idle: waiting for the host to publish its type table.");
                    }
                    return;
                }

                CaptureChanges(session, now);
                Report(now);
            }
        }

        /// <summary>ToolUpdate, so structural changes reach the systems that must see them this frame.</summary>
        public void RealizePending()
        {
            MultiplayerService service = Mod.Service;
            if (service == null || !service.GameplaySyncReady) return;
            if (_binding == null) return;

            using (SyncProfiler.Measure("ModSync.Realize"))
            {
                RealizeIncoming(service.Session, service.NowMs);
            }
        }

        /// <summary>Once per session, not in OnCreate: mods register their types as they load.</summary>
        private void EnsureCatalog()
        {
            // A mod enabled at runtime registers its types when first used; the registry size is the signal.
            if (_catalog != null && TypeManager.GetTypeCount() == _catalog.TypeCountAtBuild) return;

            ModComponentCatalog previous = _catalog;
            int before = previous != null ? previous.Entries.Count : 0;

            _catalog = ModComponentCatalog.Build();

            // Nothing replicated changed: keep the table and hashes.
            if (previous != null && _catalog.ReplicatesSameAs(previous))
            {
                _catalog = previous;
                _catalog.TypeCountAtBuild = TypeManager.GetTypeCount();
                return;
            }

            _identity = new ModCarrierIdentity(EntityManager, _prefabs, _netSearch, _objectSearch,
                _prefabIndex);

            if (previous != null)
            {
                // Indices are table positions: rebuild the query, binding and hashes.
                _queryBuilt = false;
                _typeHandles = null;
                _capture = null;
                _apply = null;
                _binding = null;
                _tablePublished = false;
                _shadow.Clear();
                _roadSpeedActive.Clear();
                _knownCarriers.Clear();
                _sweepOrder.Clear();
                _sweepCursor = 0;
                _reportedOnce.Clear();
                _announcedCatalog = false;

                SyncLog.Event(LogTopic.ModSync, "Mod state rescanned: " + before + " -> " +
                    _catalog.Entries.Count + " replicable type(s); a mod was loaded or unloaded " +
                    "after the session started.");
            }

            if (_announcedCatalog) return;
            _announcedCatalog = true;

            SyncLog.Event(LogTopic.ModSync, "Mod state: " + _catalog.Entries.Count +
                " replicable of " + _catalog.ThirdPartyTypeCount + " third-party type(s).");
            for (int i = 0; i < _catalog.Entries.Count; i++)
            {
                ModTypeDescriptor descriptor = _catalog.Entries[i].Descriptor;
                SyncLog.Detail(LogTopic.ModSync, "  " + descriptor.DisplayName + " (" +
                    descriptor.Kind + ", " + descriptor.LeafCount + " field(s))");
            }
            for (int i = 0; i < _catalog.Exclusions.Count; i++)
                SyncLog.Detail(LogTopic.ModSync, "  excluded: " + _catalog.Exclusions[i]);
        }

        /// <summary>The host owns the type order and everyone binds to it.</summary>
        private void NegotiateTable(MultiplayerSession session)
        {
            if (session.Role == SessionRole.Host)
            {
                if (_binding == null) _binding = ModTypeBinding.ForHost(_catalog);

                // Republished on every join: a joiner has not seen it.
                if (_tablePublished && !_observer.TakePeerJoined()) return;
                _tablePublished = true;

                var command = new ModTypeTableCommand { Table = _binding.Table };
                session.SendCommand(0, ModTypeTableCommand.Id, command.Encode());
                SyncLog.Event(LogTopic.ModSync, "Mod type table published: " +
                    _binding.Table.Count + " type(s).");
                return;
            }

            // A client that enabled a mod after the table arrived rebinds against it.
            if (_binding == null && _lastReceivedTable != null)
            {
                _binding = ModTypeBinding.ForClient(_catalog, _lastReceivedTable);
                SyncLog.Event(LogTopic.ModSync, "Mod type table re-bound after a local rescan: " +
                    _lastReceivedTable.Count + " type(s), " + _binding.Missing.Count +
                    " not available here.");
                _shadow.Clear();
            }

            while (_incomingTable.TryDequeue(out SimulationCommandMessage message))
            {
                if (message.OriginPlayerId == session.LocalPlayerId) continue;

                ModTypeTableCommand command;
                try
                {
                    command = ModTypeTableCommand.Decode(message.Body);
                }
                catch (System.Exception ex)
                {
                    SyncLog.Warn(LogTopic.ModSync,
                        "Dropping a malformed mod type table: " + ex.Message);
                    continue;
                }

                _lastReceivedTable = command.Table;
                _binding = ModTypeBinding.ForClient(_catalog, command.Table);
                SyncLog.Event(LogTopic.ModSync, "Mod type table bound: " + command.Table.Count +
                    " type(s), " + _binding.Missing.Count + " not available here.");
                for (int i = 0; i < _binding.Missing.Count; i++)
                    SyncLog.Warn(LogTopic.ModSync, "  missing: " + _binding.Missing[i]);

                _shadow.Clear();
            }
        }

        /// <summary>Reports a session-long condition once.</summary>
        private void ReportOnce(string key, string message)
        {
            if (!_reportedOnce.Add(key)) return;
            SyncLog.Warn(LogTopic.ModSync, message);
        }

        private void Report(long now)
        {
            if (now - _lastReportMs < 30000) return;
            _lastReportMs = now;

            // Silent while idle.
            if (_capturedTransactions == 0 && _appliedTransactions == 0 && _held.Count == 0 &&
                _unresolvedGaveUp == 0 && _rejectedClosures == 0 && _noCarrier == 0) return;

            SyncLog.Trace(LogTopic.ModSync, "ModSync/30s captured=" + _capturedTransactions +
                " sentKB=" + (_sentBytes >> 10) + " applied=" + _appliedTransactions +
                " waiting=" + _held.Count + " gaveUp=" + _unresolvedGaveUp +
                " refused=" + _rejectedClosures + " noCarrier=" + _noCarrier +
                " tracking=" + _shadow.Count + " known=" + _knownCarriers.Count +
                " role=" + (Mod.Service != null ? Mod.Service.Session.Role.ToString() : "?"));

            _capturedTransactions = 0;
            _sentBytes = 0;
            _appliedTransactions = 0;
            _unresolvedGaveUp = 0;
            _rejectedClosures = 0;
            _noCarrier = 0;
        }

        /// <summary>Both mod-sync commands, plus a join signal: the host must resend its table.</summary>
        private sealed class ModSyncObserver : SessionObserver
        {
            private readonly ModStateSyncSystem _owner;
            private readonly ConcurrentQueue<SimulationCommandMessage> _state;
            private readonly ConcurrentQueue<SimulationCommandMessage> _table;
            private int _peerJoined;

            public ModSyncObserver(ModStateSyncSystem owner,
                ConcurrentQueue<SimulationCommandMessage> state,
                ConcurrentQueue<SimulationCommandMessage> table)
            {
                _owner = owner;
                _state = state;
                _table = table;
            }

            public override void OnPeerJoined(Peer peer) => System.Threading.Interlocked.Exchange(ref _peerJoined, 1);

            /// <summary>Reads and clears the flag - the network thread sets it, this reads it.</summary>
            public bool TakePeerJoined() => System.Threading.Interlocked.Exchange(ref _peerJoined, 0) != 0;

            public override void OnCommandReceived(SimulationCommandMessage command)
            {
                if (command.CommandId == ModTypeTableCommand.Id)
                {
                    SyncInbox.Push(_table, command, SyncInbox.DefaultCap, "mod type table");
                    return;
                }

                if (command.CommandId != ModStateCommand.Id) return;
                if (command.Body != null && command.Body.Length > ModStateCommand.MaxBodyBytes)
                {
                    SyncLog.Warn(LogTopic.ModSync, "Dropping an oversized mod state command (" +
                        command.Body.Length + " bytes).");
                    return;
                }
                SyncInbox.Push(_state, command, SyncInbox.DefaultCap, "mod state");
            }
        }
    }
}
