using System.Collections.Concurrent;
using System.Collections.Generic;
using Game;
using Game.Common;
using Game.Prefabs;
using Game.Routes;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.Infrastructure;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Replicates each transport line's ticket price.
    ///
    /// The price is a field on the line's runtime <see cref="TransportLine"/> component rather
    /// than a policy, so <see cref="PolicySyncSystem"/> does not see it and no state channel
    /// carries it. Everything else about a line already replicates - geometry, stops, colour,
    /// name - which is what makes the gap easy to miss: the two cities look identical and then
    /// disagree about fare revenue, and that disagreement compounds every transport tick.
    ///
    /// Shape follows <see cref="PolicySyncSystem"/>: a 1 Hz scan diffs the current prices against
    /// what this machine last saw and sends only what changed. Prices change when a player drags
    /// a slider, so there is nothing to gain from watching every frame, and a scan is far cheaper
    /// than trying to hook the panel.
    ///
    /// The first ready tick seeds the baseline instead of sending it: the prices in a freshly
    /// loaded save are already agreed, and broadcasting them would have every peer re-announce
    /// the whole network on join.
    /// </summary>
    public partial class TransitFareSyncSystem : GameSystemBase
    {
        private const long ScanIntervalMs = 1000;

        private readonly ConcurrentQueue<SimulationCommandMessage> _incoming =
            new ConcurrentQueue<SimulationCommandMessage>();
        private readonly ReplicationGuard _guard = new ReplicationGuard();

        /// <summary>Last price this machine observed per route number.</summary>
        private readonly Dictionary<int, int> _known = new Dictionary<int, int>();

        private PrefabSystem _prefabSystem;
        private EntityQuery _lines;
        private CommandObserver _observer;
        private long _lastScanMs;
        private bool _primed;

        protected override void OnCreate()
        {
            base.OnCreate();
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();

            _lines = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<Route, RouteNumber, TransportLine, PrefabRef>(),
                None = SyncQuery.ReadOnly<Temp, Deleted>(),
            });

            _observer = SyncObserverBinding.Bind(
                () => new CommandObserver(_incoming, TransitFareCommand.Id), DrainQueue);
            Mod.log.Info(nameof(TransitFareSyncSystem) + " ready.");
        }

        protected override void OnDestroy()
        {
            SyncObserverBinding.Unbind(_observer, DrainQueue);
            base.OnDestroy();
        }

        private void DrainQueue()
        {
            SyncInbox.Clear(_incoming);
            // The baseline describes a world that is no longer loaded. Keeping it would have the
            // first scan after a reload read every price as a change and rebroadcast the network.
            _known.Clear();
            _primed = false;
            _guard.Clear();
        }

        protected override void OnUpdate()
        {
            using (Diagnostics.SyncProfiler.Measure("TransitFare"))
            {
                MultiplayerService service = Mod.Service;
                if (service == null) return;

                MultiplayerSession session = service.Session;
                if (!service.GameplaySyncReady)
                {
                    DrainQueue();
                    return;
                }

                long now = service.NowMs;
                _guard.Prune(now);

                // Incoming first, so a price this machine is about to adopt is in the baseline
                // before the scan diffs against it and reports it back as a local edit.
                ApplyIncoming(session, now);

                if (now - _lastScanMs < ScanIntervalMs) return;
                _lastScanMs = now;

                Scan(session, now);
            }
        }

        private void Scan(MultiplayerSession session, long now)
        {
            NativeArray<Entity> lines = _lines.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < lines.Length; i++)
                {
                    Entity line = lines[i];
                    if (!EntityManager.Exists(line) ||
                        !EntityManager.HasComponent<TransportLine>(line) ||
                        !EntityManager.HasComponent<RouteNumber>(line)) continue;

                    int number = EntityManager.GetComponentData<RouteNumber>(line).m_Number;
                    int price = EntityManager.GetComponentData<TransportLine>(line).m_TicketPrice;

                    int previous;
                    bool seen = _known.TryGetValue(number, out previous);
                    _known[number] = price;

                    if (!_primed || !seen || previous == price) continue;
                    if (_guard.Consume(FareKey(number, price), now)) continue; // we applied it

                    string prefabName = PrefabIndex.SafeName(
                        _prefabSystem, EntityManager.GetComponentData<PrefabRef>(line).m_Prefab);
                    if (string.IsNullOrEmpty(prefabName)) continue;

                    var command = new TransitFareCommand
                    {
                        RoutePrefabName = prefabName,
                        RouteNumber = number,
                        TicketPrice = price,
                    };
                    session.SendCommand(0, TransitFareCommand.Id, command.Encode());
                    Mod.Verbose("[MP] TransitFare: broadcast line " + number + " at " + price + ".");
                }

                // A line deleted while we were not looking would otherwise keep its last price in
                // the baseline forever, and a later line reusing that number would be read as a
                // change the moment it appeared.
                PruneMissing(lines);
                _primed = true;
            }
            finally { lines.Dispose(); }
        }

        private void PruneMissing(NativeArray<Entity> lines)
        {
            if (_known.Count == 0) return;

            var live = new HashSet<int>();
            for (int i = 0; i < lines.Length; i++)
                if (EntityManager.HasComponent<RouteNumber>(lines[i]))
                    live.Add(EntityManager.GetComponentData<RouteNumber>(lines[i]).m_Number);

            List<int> gone = null;
            foreach (var pair in _known)
                if (!live.Contains(pair.Key)) (gone ?? (gone = new List<int>())).Add(pair.Key);
            if (gone == null) return;
            for (int i = 0; i < gone.Count; i++) _known.Remove(gone[i]);
        }

        private void ApplyIncoming(MultiplayerSession session, long now)
        {
            SimulationCommandMessage message;
            while (_incoming.TryDequeue(out message))
            {
                if (message.OriginPlayerId == session.LocalPlayerId) continue;

                TransitFareCommand command;
                try { command = TransitFareCommand.Decode(message.Body); }
                catch (System.Exception ex)
                {
                    Mod.log.Warn("[MP] TransitFare: dropping malformed command: " + ex.Message);
                    continue;
                }

                if (!TryApply(command))
                {
                    // Not a reason to resync: the line is on its way through the route pipeline,
                    // and its price will be picked up by the sender's next scan once it lands.
                    Mod.Verbose("[MP] TransitFare: line " + command.RouteNumber +
                                " not here yet; ignoring its fare.");
                    continue;
                }

                _guard.Mark(FareKey(command.RouteNumber, command.TicketPrice), now);
                _known[command.RouteNumber] = command.TicketPrice;
                Mod.Verbose("[MP] TransitFare: line " + command.RouteNumber +
                            " set to " + command.TicketPrice + " by player " +
                            message.OriginPlayerId + ".");
            }
        }

        private bool TryApply(TransitFareCommand command)
        {
            NativeArray<Entity> lines = _lines.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < lines.Length; i++)
                {
                    Entity line = lines[i];
                    if (!EntityManager.Exists(line) ||
                        !EntityManager.HasComponent<RouteNumber>(line) ||
                        !EntityManager.HasComponent<TransportLine>(line) ||
                        !EntityManager.HasComponent<PrefabRef>(line)) continue;

                    if (EntityManager.GetComponentData<RouteNumber>(line).m_Number !=
                        command.RouteNumber) continue;

                    // A route number can be reused by a different kind of line. Writing a bus
                    // fare onto a freight line would be silent and wrong, so the prefab has to
                    // agree before anything is written.
                    string prefabName = PrefabIndex.SafeName(
                        _prefabSystem, EntityManager.GetComponentData<PrefabRef>(line).m_Prefab);
                    if (!string.Equals(prefabName, command.RoutePrefabName)) continue;

                    TransportLine transport = EntityManager.GetComponentData<TransportLine>(line);
                    if (transport.m_TicketPrice == (ushort)command.TicketPrice) return true;
                    transport.m_TicketPrice = (ushort)command.TicketPrice;
                    EntityManager.SetComponentData(line, transport);
                    return true;
                }
            }
            finally { lines.Dispose(); }
            return false;
        }

        private static string FareKey(int routeNumber, int price) =>
            "fare|" + routeNumber + "|" + price;
    }
}
