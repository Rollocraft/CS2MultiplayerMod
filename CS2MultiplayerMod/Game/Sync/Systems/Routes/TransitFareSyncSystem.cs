using System;
using System.Collections.Concurrent;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Sync.Commands;
using Game;
using Game.Common;
using Game.Routes;
using Unity.Collections;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Synchronizes public transit line passenger ticket pricing across players.
    /// </summary>
    public partial class TransitFareSyncSystem : GameSystemBase
    {
        private readonly ConcurrentQueue<SimulationCommandMessage> _incoming =
            new ConcurrentQueue<SimulationCommandMessage>();

        private EntityQuery _routeQuery;
        private Observer _observer;

        protected override void OnCreate()
        {
            base.OnCreate();
            _routeQuery = GetEntityQuery(
                ComponentType.ReadOnly<Route>(),
                ComponentType.ReadWrite<TransportLine>(),
                ComponentType.ReadOnly<RouteNumber>()
            );
            _observer = new Observer(_incoming);
            Mod.log.Info(nameof(TransitFareSyncSystem) + " ready.");
        }

        protected override void OnDestroy()
        {
            if (_observer != null && Mod.Service?.Session != null)
                Mod.Service.Session.RemoveObserver(_observer);
            base.OnDestroy();
        }

        private bool _registered;

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
                if (message.CommandId != TransitFareCommand.Id) continue;
                TransitFareCommand cmd = TransitFareCommand.Deserialize(message.Body);
                if (cmd == null) continue;

                if (_routeQuery.IsEmptyIgnoreFilter) continue;
                NativeArray<Entity> entities = _routeQuery.ToEntityArray(Allocator.Temp);
                try
                {
                    for (int i = 0; i < entities.Length; i++)
                    {
                        Entity entity = entities[i];
                        int number = EntityManager.GetComponentData<RouteNumber>(entity).m_Number;
                        if (number != cmd.RouteNumber) continue;

                        TransportLine line = EntityManager.GetComponentData<TransportLine>(entity);
                        line.m_TicketPrice = (ushort)Math.Max(0, Math.Min(ushort.MaxValue, cmd.TicketPrice));
                        EntityManager.SetComponentData(entity, line);

                        Mod.Verbose("[MP] Applied transit fare: Route=" + cmd.RouteNumber +
                                    ", TicketPrice=" + cmd.TicketPrice);
                        break;
                    }
                }
                finally
                {
                    entities.Dispose();
                }
            }
        }

        public void SetTransitFare(int routeNumber, int ticketPrice)
        {
            MultiplayerService service = Mod.Service;
            if (service == null || !service.GameplaySyncReady) return;

            var cmd = new TransitFareCommand
            {
                RouteNumber = routeNumber,
                TicketPrice = ticketPrice
            };

            service.Session.SendCommand(0, TransitFareCommand.Id, cmd.Serialize());
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
                if (command.CommandId == TransitFareCommand.Id)
                    _incoming.Enqueue(command);
            }
        }
    }
}
