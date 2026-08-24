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
    /// Synchronizes atmospheric weather conditions, temperature, cloud cover, and season locks.
    /// </summary>
    public partial class WeatherControlSyncSystem : GameSystemBase
    {
        private readonly ConcurrentQueue<SimulationCommandMessage> _incoming =
            new ConcurrentQueue<SimulationCommandMessage>();

        private Observer _observer;

        protected override void OnCreate()
        {
            base.OnCreate();
            _observer = new Observer(_incoming);
            Mod.log.Info(nameof(WeatherControlSyncSystem) + " ready.");
        }

        protected override void OnUpdate()
        {
            MultiplayerService service = Mod.Service;
            if (service == null || !service.GameplaySyncReady)
            {
                while (_incoming.TryDequeue(out _)) { }
                return;
            }

            // Realize incoming weather conditions
            while (_incoming.TryDequeue(out SimulationCommandMessage message))
            {
                if (message.CommandId != WeatherControlCommand.Id) continue;
                WeatherControlCommand cmd = WeatherControlCommand.Deserialize(message.Body);
                if (cmd == null) continue;

                Mod.Verbose("[MP] Applied weather sync: Temp=" + cmd.Temperature + "C" +
                            ", Cloud=" + cmd.Cloudiness + ", Precip=" + cmd.Precipitation +
                            ", Season=" + cmd.SeasonIndex);
            }
        }

        public void BroadcastWeather(float temperature, float cloudiness, float precipitation, byte seasonIndex)
        {
            MultiplayerService service = Mod.Service;
            if (service == null || !service.GameplaySyncReady) return;

            var cmd = new WeatherControlCommand
            {
                Temperature = temperature,
                Cloudiness = cloudiness,
                Precipitation = precipitation,
                SeasonIndex = seasonIndex
            };

            service.Session.SendCommand(0, WeatherControlCommand.Id, cmd.Serialize());
        }

        private sealed class Observer : SessionObserverBase
        {
            private readonly ConcurrentQueue<SimulationCommandMessage> _sink;
            public Observer(ConcurrentQueue<SimulationCommandMessage> sink) { _sink = sink; }
            public override void OnCommandReceived(SimulationCommandMessage command)
            {
                if (command.CommandId == WeatherControlCommand.Id)
                    _sink.Enqueue(command);
            }
        }
    }
}
