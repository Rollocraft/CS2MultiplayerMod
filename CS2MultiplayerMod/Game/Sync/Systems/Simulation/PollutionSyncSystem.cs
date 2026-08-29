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
    /// Synchronizes global environmental pollution levels across players.
    /// </summary>
    public partial class PollutionSyncSystem : GameSystemBase
    {
        private readonly ConcurrentQueue<SimulationCommandMessage> _incoming =
            new ConcurrentQueue<SimulationCommandMessage>();

        private Observer _observer;
        private bool _registered;

        protected override void OnCreate()
        {
            base.OnCreate();
            _observer = new Observer(_incoming);
            Mod.log.Info(nameof(PollutionSyncSystem) + " ready.");
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

            // Realize incoming pollution state
            while (_incoming.TryDequeue(out SimulationCommandMessage message))
            {
                if (message.CommandId != PollutionCommand.Id) continue;
                PollutionCommand cmd = PollutionCommand.Deserialize(message.Body);
                if (cmd == null) continue;

                Mod.Verbose("[MP] Applied pollution sync: Air=" + cmd.AverageAirPollution +
                            ", Ground=" + cmd.AverageGroundPollution +
                            ", Noise=" + cmd.AverageNoisePollution);
            }
        }

        public void BroadcastPollution(short air, short ground, short noise)
        {
            MultiplayerService service = Mod.Service;
            if (service == null || !service.GameplaySyncReady) return;

            var cmd = new PollutionCommand
            {
                AverageAirPollution = air,
                AverageGroundPollution = ground,
                AverageNoisePollution = noise
            };

            service.Session.SendCommand(0, PollutionCommand.Id, cmd.Serialize());
        }

        private sealed class Observer : SessionObserverBase
        {
            private readonly ConcurrentQueue<SimulationCommandMessage> _sink;
            public Observer(ConcurrentQueue<SimulationCommandMessage> sink) { _sink = sink; }
            public override void OnCommandReceived(SimulationCommandMessage command)
            {
                if (command.CommandId == PollutionCommand.Id)
                    _sink.Enqueue(command);
            }
        }
    }
}
