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
        private bool _registered;

        protected override void OnCreate()
        {
            base.OnCreate();
            _observer = new Observer(_incoming);
            Mod.log.Info(nameof(BuildingToggleSyncSystem) + " ready.");
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
                if (message.CommandId != BuildingToggleCommand.Id) continue;
                BuildingToggleCommand cmd = BuildingToggleCommand.Deserialize(message.Body);
                if (cmd == null) continue;

                Entity building = new Entity { Index = cmd.BuildingIndex, Version = cmd.BuildingVersion };
                if (EntityManager.Exists(building) && EntityManager.HasComponent<global::Game.Buildings.Building>(building))
                {
                    if (cmd.IsOperational)
                    {
                        if (EntityManager.HasComponent<Unity.Entities.Disabled>(building))
                            EntityManager.RemoveComponent<Unity.Entities.Disabled>(building);
                    }
                    else
                    {
                        if (!EntityManager.HasComponent<Unity.Entities.Disabled>(building))
                            EntityManager.AddComponent<Unity.Entities.Disabled>(building);
                    }
                }

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
