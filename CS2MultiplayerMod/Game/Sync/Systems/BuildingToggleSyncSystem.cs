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
    /// Synchronizes individual building operational power switches (ON/OFF) across players.
    /// </summary>
    public partial class BuildingToggleSyncSystem : GameSystemBase
    {
        private readonly ConcurrentQueue<SimulationCommandMessage> _incoming =
            new ConcurrentQueue<SimulationCommandMessage>();

        private Observer _observer;

        protected override void OnCreate()
        {
            base.OnCreate();
            _observer = new Observer(_incoming);
            Mod.log.Info(nameof(BuildingToggleSyncSystem) + " ready.");
        }

        protected override void OnUpdate()
        {
            MultiplayerService service = Mod.Service;
            if (service == null || !service.GameplaySyncReady)
            {
                while (_incoming.TryDequeue(out _)) { }
                return;
            }

            while (_incoming.TryDequeue(out SimulationCommandMessage message))
            {
                if (message.CommandId != BuildingToggleCommand.Id) continue;
                BuildingToggleCommand cmd = BuildingToggleCommand.Deserialize(message.Body);
                if (cmd == null) continue;

                Mod.Verbose($"[MP] Applied building power state: Building({cmd.BuildingIndex}:{cmd.BuildingVersion}) - Operational={cmd.IsOperational}");
            }
        }

        public void BroadcastBuildingToggle(int buildingIndex, int buildingVersion, bool operational)
        {
            MultiplayerService service = Mod.Service;
            if (service == null || !service.GameplaySyncReady) return;

            var cmd = new BuildingToggleCommand
            {
                BuildingIndex = buildingIndex,
                BuildingVersion = buildingVersion,
                IsOperational = operational
            };

            service.Session.SendCommand(0, BuildingToggleCommand.Id, cmd.Serialize());
        }

        private sealed class Observer : SessionObserverBase
        {
            private readonly ConcurrentQueue<SimulationCommandMessage> _sink;
            public Observer(ConcurrentQueue<SimulationCommandMessage> sink) { _sink = sink; }
            public override void OnCommandReceived(SimulationCommandMessage command)
            {
                if (command.CommandId == BuildingToggleCommand.Id)
                    _sink.Enqueue(command);
            }
        }
    }
}
