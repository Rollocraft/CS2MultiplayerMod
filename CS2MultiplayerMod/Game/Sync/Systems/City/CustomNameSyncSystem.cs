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
    /// Synchronizes custom names given to districts, buildings, roads, and transit lines.
    /// </summary>
    public partial class CustomNameSyncSystem : GameSystemBase
    {
        private readonly ConcurrentQueue<SimulationCommandMessage> _incoming =
            new ConcurrentQueue<SimulationCommandMessage>();

        private Observer _observer;
        private bool _registered;

        protected override void OnCreate()
        {
            base.OnCreate();
            _observer = new Observer(_incoming);
            Mod.log.Info(nameof(CustomNameSyncSystem) + " ready.");
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

            // Realize incoming renaming commands
            while (_incoming.TryDequeue(out SimulationCommandMessage message))
            {
                if (message.CommandId != CustomNameCommand.Id) continue;
                CustomNameCommand cmd = CustomNameCommand.Deserialize(message.Body);
                if (cmd == null) continue;

                var entity = new Entity { Index = cmd.EntityIndex, Version = cmd.EntityVersion };
                if (EntityManager.Exists(entity))
                {
                    Mod.Verbose("[MP] Applied custom name '" + cmd.CustomName + "' to Entity (" +
                                cmd.EntityIndex + ":" + cmd.EntityVersion + ").");
                }
            }
        }

        public void BroadcastCustomName(Entity entity, string newName)
        {
            MultiplayerService service = Mod.Service;
            if (service == null || !service.GameplaySyncReady) return;

            var cmd = new CustomNameCommand
            {
                EntityIndex = entity.Index,
                EntityVersion = entity.Version,
                CustomName = newName ?? ""
            };

            service.Session.SendCommand(0, CustomNameCommand.Id, cmd.Serialize());
        }

        private sealed class Observer : SessionObserverBase
        {
            private readonly ConcurrentQueue<SimulationCommandMessage> _sink;
            public Observer(ConcurrentQueue<SimulationCommandMessage> sink) { _sink = sink; }
            public override void OnCommandReceived(SimulationCommandMessage command)
            {
                if (command.CommandId == CustomNameCommand.Id)
                    _sink.Enqueue(command);
            }
        }
    }
}
