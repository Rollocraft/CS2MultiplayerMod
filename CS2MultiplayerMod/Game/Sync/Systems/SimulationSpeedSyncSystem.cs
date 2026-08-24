using System;
using System.Collections.Concurrent;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Sync.Commands;
using Game;
using Game.Simulation;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Synchronizes simulation play/pause state and speed multiplier step (1x, 2x, 3x)
    /// across co-op sessions.
    /// </summary>
    public partial class SimulationSpeedSyncSystem : GameSystemBase
    {
        private readonly ConcurrentQueue<SimulationCommandMessage> _incoming =
            new ConcurrentQueue<SimulationCommandMessage>();

        private Observer _observer;
        private SimulationSystem _simulationSystem;
        private int _lastBroadcastSpeed = -1;
        private bool _lastBroadcastPaused;

        protected override void OnCreate()
        {
            base.OnCreate();
            _observer = new Observer(_incoming);
            _simulationSystem = World.GetExistingSystemManaged<SimulationSystem>();
            Mod.log.Info(nameof(SimulationSpeedSyncSystem) + " ready.");
        }

        protected override void OnUpdate()
        {
            MultiplayerService service = Mod.Service;
            if (service == null || !service.GameplaySyncReady)
            {
                while (_incoming.TryDequeue(out _)) { }
                return;
            }

            if (_simulationSystem == null)
            {
                _simulationSystem = World.GetExistingSystemManaged<SimulationSystem>();
                if (_simulationSystem == null) return;
            }

            // Realize incoming speed/pause commands
            while (_incoming.TryDequeue(out SimulationCommandMessage message))
            {
                if (message.CommandId != SimulationSpeedCommand.Id) continue;
                SimulationSpeedCommand cmd = SimulationSpeedCommand.Deserialize(message.Body);
                if (cmd == null) continue;

                if (_simulationSystem != null)
                {
                    _simulationSystem.selectedSpeed = cmd.SpeedIndex;
                    _lastBroadcastSpeed = cmd.SpeedIndex;
                    _lastBroadcastPaused = cmd.Paused;
                    Mod.Verbose("[MP] Applied simulation speed: " + cmd.SpeedIndex + "x, Paused=" + cmd.Paused);
                }
            }

            // Host broadcasts speed/pause state changes
            if (service.Session.Role == SessionRole.Host && _simulationSystem != null)
            {
                int currentSpeed = (int)_simulationSystem.selectedSpeed;
                bool isPaused = currentSpeed == 0;
                if (currentSpeed != _lastBroadcastSpeed || isPaused != _lastBroadcastPaused)
                {
                    _lastBroadcastSpeed = currentSpeed;
                    _lastBroadcastPaused = isPaused;
                    BroadcastSpeedChange(isPaused, (byte)currentSpeed);
                }
            }
        }

        public void BroadcastSpeedChange(bool paused, byte speedIndex)
        {
            MultiplayerService service = Mod.Service;
            if (service == null || !service.GameplaySyncReady) return;

            var cmd = new SimulationSpeedCommand
            {
                Paused = paused,
                SpeedIndex = speedIndex
            };

            service.Session.SendCommand(0, SimulationSpeedCommand.Id, cmd.Serialize());
        }

        private sealed class Observer : SessionObserverBase
        {
            private readonly ConcurrentQueue<SimulationCommandMessage> _sink;
            public Observer(ConcurrentQueue<SimulationCommandMessage> sink) { _sink = sink; }
            public override void OnCommandReceived(SimulationCommandMessage command)
            {
                if (command.CommandId == SimulationSpeedCommand.Id)
                    _sink.Enqueue(command);
            }
        }
    }
}
