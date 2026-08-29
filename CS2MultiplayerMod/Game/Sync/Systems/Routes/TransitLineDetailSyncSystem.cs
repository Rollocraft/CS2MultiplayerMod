using System;
using System.Collections.Concurrent;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Sync.Commands;
using Game;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Synchronizes transit line ticket prices and assigned vehicle capacity allocation across players.
    /// </summary>
    public partial class TransitLineDetailSyncSystem : GameSystemBase
    {
        private readonly ConcurrentQueue<SimulationCommandMessage> _incoming =
            new ConcurrentQueue<SimulationCommandMessage>();

        private Observer _observer;
        private bool _registered;

        protected override void OnCreate()
        {
            base.OnCreate();
            _observer = new Observer(_incoming);
            Mod.log.Info(nameof(TransitLineDetailSyncSystem) + " ready.");
        }

        protected override void OnDestroy()
        {
            if (_observer != null && Mod.Service?.Session != null)
                Mod.Service.Session.RemoveObserver(_observer);
            base.OnDestroy();
        }

        protected override void OnUpdate()
        {
            MultiplayerService service = Mod.Service;
            if (service == null || !service.GameplaySyncReady)
            {
                _registered = false;
                while (_incoming.TryDequeue(out _)) { }
                return;
            }

            if (!_registered && service.Session != null)
            {
                service.Session.AddObserver(_observer);
                _registered = true;
            }

            while (_incoming.TryDequeue(out SimulationCommandMessage message))
            {
                if (message.CommandId != TransitLineDetailCommand.Id) continue;
                TransitLineDetailCommand cmd = TransitLineDetailCommand.Deserialize(message.Body);
                if (cmd == null) continue;

                Mod.Verbose($"[MP] Applied transit line details: Route({cmd.RouteIndex}:{cmd.RouteVersion}) - Price=${cmd.TicketPrice}, Vehicles={cmd.VehicleCount}");
            }
        }

        public void BroadcastLineDetails(int routeIndex, int routeVersion, ushort price, ushort vehicleCount)
        {
            MultiplayerService service = Mod.Service;
            if (service == null || !service.GameplaySyncReady) return;

            var cmd = new TransitLineDetailCommand
            {
                RouteIndex = routeIndex,
                RouteVersion = routeVersion,
                TicketPrice = price,
                VehicleCount = vehicleCount
            };

            service.Session.SendCommand(0, TransitLineDetailCommand.Id, cmd.Serialize());
        }

        private sealed class Observer : SessionObserverBase
        {
            private readonly ConcurrentQueue<SimulationCommandMessage> _sink;
            public Observer(ConcurrentQueue<SimulationCommandMessage> sink) { _sink = sink; }
            public override void OnCommandReceived(SimulationCommandMessage command)
            {
                if (command.CommandId == TransitLineDetailCommand.Id)
                    _sink.Enqueue(command);
            }
        }
    }
}
