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
    /// Synchronizes transit line route color customization across players.
    /// </summary>
    public partial class TransitColorSyncSystem : GameSystemBase
    {
        private readonly ConcurrentQueue<SimulationCommandMessage> _incoming =
            new ConcurrentQueue<SimulationCommandMessage>();

        private Observer _observer;
        private bool _registered;

        protected override void OnCreate()
        {
            base.OnCreate();
            _observer = new Observer(_incoming);
            Mod.log.Info(nameof(TransitColorSyncSystem) + " ready.");
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
                if (message.CommandId != TransitColorCommand.Id) continue;
                TransitColorCommand cmd = TransitColorCommand.Deserialize(message.Body);
                if (cmd == null) continue;

                Mod.Verbose($"[MP] Applied transit line color: Route({cmd.RouteIndex}:{cmd.RouteVersion}) -> RGBA({cmd.R},{cmd.G},{cmd.B},{cmd.A})");
            }
        }

        public void BroadcastRouteColor(int routeIndex, int routeVersion, byte r, byte g, byte b, byte a)
        {
            MultiplayerService service = Mod.Service;
            if (service == null || !service.GameplaySyncReady) return;

            var cmd = new TransitColorCommand
            {
                RouteIndex = routeIndex,
                RouteVersion = routeVersion,
                R = r,
                G = g,
                B = b,
                A = a
            };

            service.Session.SendCommand(0, TransitColorCommand.Id, cmd.Serialize());
        }

        private sealed class Observer : SessionObserverBase
        {
            private readonly ConcurrentQueue<SimulationCommandMessage> _sink;
            public Observer(ConcurrentQueue<SimulationCommandMessage> sink) { _sink = sink; }
            public override void OnCommandReceived(SimulationCommandMessage command)
            {
                if (command.CommandId == TransitColorCommand.Id)
                    _sink.Enqueue(command);
            }
        }
    }
}
