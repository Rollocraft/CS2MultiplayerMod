using System;
using System.Collections.Concurrent;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Sync.Commands;
using Game;
using Game.Buildings;
using Game.Common;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Synchronizes service building vehicle fleet limits (police, fire, medical, transport depots).
    /// </summary>
    public partial class ServiceFleetSyncSystem : GameSystemBase
    {
        private readonly ConcurrentQueue<SimulationCommandMessage> _incoming =
            new ConcurrentQueue<SimulationCommandMessage>();

        private Observer _observer;
        private bool _registered;

        protected override void OnCreate()
        {
            base.OnCreate();
            _observer = new Observer(_incoming);
            Mod.log.Info(nameof(ServiceFleetSyncSystem) + " ready.");
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
                if (message.CommandId != ServiceFleetCommand.Id) continue;
                ServiceFleetCommand cmd = ServiceFleetCommand.Deserialize(message.Body);
                if (cmd == null) continue;

                var entity = new Entity { Index = cmd.BuildingIndex, Version = cmd.BuildingVersion };
                if (!EntityManager.Exists(entity)) continue;

                if (EntityManager.HasComponent<ServiceUsage>(entity))
                {
                    ServiceUsage usage = EntityManager.GetComponentData<ServiceUsage>(entity);
                    usage.m_Usage = cmd.VehicleLimit;
                    EntityManager.SetComponentData(entity, usage);
                }

                Mod.Verbose("[MP] Applied service fleet limit: Entity=" + entity +
                            ", VehicleLimit=" + cmd.VehicleLimit);
            }
        }

        public void SetVehicleLimit(Entity entity, int vehicleLimit)
        {
            MultiplayerService service = Mod.Service;
            if (service == null || !service.GameplaySyncReady) return;

            var cmd = new ServiceFleetCommand
            {
                BuildingIndex = entity.Index,
                BuildingVersion = entity.Version,
                VehicleLimit = vehicleLimit
            };

            service.Session.SendCommand(0, ServiceFleetCommand.Id, cmd.Serialize());
        }

        private sealed class Observer : SessionObserverBase
        {
            private readonly ConcurrentQueue<SimulationCommandMessage> _incoming;

            public Observer(ConcurrentQueue<SimulationCommandMessage> incoming)
            {
                _incoming = incoming;
            }

            public override void OnCommandReceived(SimulationCommandMessage command)
            {
                if (command.CommandId == ServiceFleetCommand.Id)
                    _incoming.Enqueue(command);
            }
        }
    }
}
