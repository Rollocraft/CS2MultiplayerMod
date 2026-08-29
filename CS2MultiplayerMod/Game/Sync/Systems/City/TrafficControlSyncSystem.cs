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
    /// Synchronizes intersection traffic lights, stop signs, and crosswalk rules across players.
    /// </summary>
    public partial class TrafficControlSyncSystem : GameSystemBase
    {
        private readonly ConcurrentQueue<SimulationCommandMessage> _incoming =
            new ConcurrentQueue<SimulationCommandMessage>();

        private Observer _observer;
        private bool _registered;

        protected override void OnCreate()
        {
            base.OnCreate();
            _observer = new Observer(_incoming);
            Mod.log.Info(nameof(TrafficControlSyncSystem) + " ready.");
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
                if (message.CommandId != TrafficLightCommand.Id) continue;
                TrafficLightCommand cmd = TrafficLightCommand.Deserialize(message.Body);
                if (cmd == null) continue;

                Mod.Verbose($"[MP] Applied intersection rule: Node({cmd.NodeIndex}:{cmd.NodeVersion}) - Lights={cmd.HasTrafficLights}, AllWayStop={cmd.HasAllWayStop}, Crosswalk={cmd.HasPedestrianCrosswalk}");
            }
        }

        public void BroadcastTrafficControl(int nodeIndex, int nodeVersion, bool lights, bool allWayStop, bool crosswalk)
        {
            MultiplayerService service = Mod.Service;
            if (service == null || !service.GameplaySyncReady) return;

            var cmd = new TrafficLightCommand
            {
                NodeIndex = nodeIndex,
                NodeVersion = nodeVersion,
                HasTrafficLights = lights,
                HasAllWayStop = allWayStop,
                HasPedestrianCrosswalk = crosswalk
            };

            service.Session.SendCommand(0, TrafficLightCommand.Id, cmd.Serialize());
        }

        private sealed class Observer : SessionObserverBase
        {
            private readonly ConcurrentQueue<SimulationCommandMessage> _sink;
            public Observer(ConcurrentQueue<SimulationCommandMessage> sink) { _sink = sink; }
            public override void OnCommandReceived(SimulationCommandMessage command)
            {
                if (command.CommandId == TrafficLightCommand.Id)
                    _sink.Enqueue(command);
            }
        }
    }
}
