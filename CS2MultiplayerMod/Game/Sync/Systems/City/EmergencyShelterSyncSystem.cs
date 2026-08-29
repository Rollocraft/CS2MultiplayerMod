using System;
using System.Collections.Concurrent;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Sync.Commands;
using Game;
using Game.Buildings;
using Game.Common;
using Game.Events;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Synchronizes emergency shelter evacuation states and city-wide disaster siren alarms across players.
    /// </summary>
    public partial class EmergencyShelterSyncSystem : GameSystemBase
    {
        private readonly ConcurrentQueue<SimulationCommandMessage> _incoming =
            new ConcurrentQueue<SimulationCommandMessage>();

        private Observer _observer;
        private bool _registered;

        protected override void OnCreate()
        {
            base.OnCreate();
            _observer = new Observer(_incoming);
            Mod.log.Info(nameof(EmergencyShelterSyncSystem) + " ready.");
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
                if (message.CommandId != EmergencyShelterCommand.Id) continue;
                EmergencyShelterCommand cmd = EmergencyShelterCommand.Deserialize(message.Body);
                if (cmd == null) continue;

                var entity = new Entity { Index = cmd.BuildingIndex, Version = cmd.BuildingVersion };
                if (!EntityManager.Exists(entity) || !EntityManager.HasComponent<EmergencyShelter>(entity)) continue;

                if (cmd.IsEvacuating)
                {
                    if (EntityManager.HasComponent<InDanger>(entity))
                    {
                        InDanger danger = EntityManager.GetComponentData<InDanger>(entity);
                        danger.m_Flags |= DangerFlags.Evacuate;
                        EntityManager.SetComponentData(entity, danger);
                    }
                    else
                    {
                        EntityManager.AddComponentData(entity, new InDanger
                        {
                            m_Flags = DangerFlags.Evacuate
                        });
                    }
                }
                else
                {
                    if (EntityManager.HasComponent<InDanger>(entity))
                    {
                        InDanger danger = EntityManager.GetComponentData<InDanger>(entity);
                        danger.m_Flags &= ~DangerFlags.Evacuate;
                        if (danger.m_Flags == 0)
                        {
                            EntityManager.RemoveComponent<InDanger>(entity);
                        }
                        else
                        {
                            EntityManager.SetComponentData(entity, danger);
                        }
                    }
                }

                Mod.Verbose("[MP] Applied emergency shelter evacuation state: Entity=" + entity +
                            ", Evacuating=" + cmd.IsEvacuating);
            }
        }

        public void SetEvacuationState(Entity entity, bool isEvacuating)
        {
            MultiplayerService service = Mod.Service;
            if (service == null || !service.GameplaySyncReady) return;

            var cmd = new EmergencyShelterCommand
            {
                BuildingIndex = entity.Index,
                BuildingVersion = entity.Version,
                IsEvacuating = isEvacuating
            };

            service.Session.SendCommand(0, EmergencyShelterCommand.Id, cmd.Serialize());
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
                if (command.CommandId == EmergencyShelterCommand.Id)
                    _incoming.Enqueue(command);
            }
        }
    }
}
