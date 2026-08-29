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
    /// Synchronizes municipal budget sliders and zone taxation rates across co-op sessions.
    /// </summary>
    public partial class CityBudgetSyncSystem : GameSystemBase
    {
        private readonly ConcurrentQueue<SimulationCommandMessage> _incoming =
            new ConcurrentQueue<SimulationCommandMessage>();

        private Observer _observer;
        private bool _registered;

        protected override void OnCreate()
        {
            base.OnCreate();
            _observer = new Observer(_incoming);
            Mod.log.Info(nameof(CityBudgetSyncSystem) + " ready.");
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

            // Realize incoming budget changes
            while (_incoming.TryDequeue(out SimulationCommandMessage message))
            {
                if (message.CommandId != CityBudgetCommand.Id) continue;
                CityBudgetCommand cmd = CityBudgetCommand.Deserialize(message.Body);
                if (cmd == null) continue;

                EntityManager em = EntityManager;

                // 1. Apply Tax Rate if requested
                if (cmd.ZoneTaxType < 4)
                {
                    try
                    {
                        var taxSystem = World.GetOrCreateSystemManaged<global::Game.Simulation.TaxSystem>();
                        if (taxSystem != null)
                        {
                            global::Game.Simulation.TaxAreaType[] areas = new[]
                            {
                                global::Game.Simulation.TaxAreaType.Residential,
                                global::Game.Simulation.TaxAreaType.Commercial,
                                global::Game.Simulation.TaxAreaType.Industrial,
                                global::Game.Simulation.TaxAreaType.Office
                            };
                            taxSystem.SetTaxRate(areas[cmd.ZoneTaxType], cmd.TaxRatePercent);
                        }
                    }
                    catch (Exception ex)
                    {
                        Mod.log.Warn("[MP] Failed to apply tax sync: " + ex.Message);
                    }
                }

                // 2. Apply Service Budget if requested
                if (cmd.ServiceType != 255)
                {
                    try
                    {
                        var budgetSystem = World.GetOrCreateSystemManaged<global::Game.Simulation.CityServiceBudgetSystem>();
                        var budgetQuery = GetEntityQuery(ComponentType.ReadOnly<global::Game.Simulation.ServiceBudgetData>());
                        if (budgetSystem != null && budgetQuery.CalculateEntityCount() > 0)
                        {
                            Entity singleton = budgetQuery.GetSingletonEntity();
                            DynamicBuffer<global::Game.Simulation.ServiceBudgetData> budgets = em.GetBuffer<global::Game.Simulation.ServiceBudgetData>(singleton, true);
                            if (cmd.ServiceType < budgets.Length)
                            {
                                Entity serviceEntity = budgets[cmd.ServiceType].m_Service;
                                if (serviceEntity != Entity.Null)
                                {
                                    budgetSystem.SetServiceBudget(serviceEntity, cmd.BudgetPercent);
                                }
                            }

                            // Trigger UI refresh
                            if (!em.HasComponent<global::Game.Common.BatchesUpdated>(singleton))
                                em.AddComponent<global::Game.Common.BatchesUpdated>(singleton);
                            if (!em.HasComponent<global::Game.Common.Updated>(singleton))
                                em.AddComponent<global::Game.Common.Updated>(singleton);
                        }
                    }
                    catch (Exception ex)
                    {
                        Mod.log.Warn("[MP] Failed to apply service budget sync: " + ex.Message);
                    }
                }

                Mod.Verbose("[MP] Applied budget/tax sync: Service=" + cmd.ServiceType +
                            ", Budget=" + cmd.BudgetPercent + "%, Zone=" + cmd.ZoneTaxType +
                            ", Tax=" + cmd.TaxRatePercent + "%");
            }
        }

        public void BroadcastBudgetChange(byte serviceType, byte budgetPercent, byte zoneTaxType, byte taxRatePercent)
        {
            MultiplayerService service = Mod.Service;
            if (service == null || !service.GameplaySyncReady) return;

            var cmd = new CityBudgetCommand
            {
                ServiceType = serviceType,
                BudgetPercent = budgetPercent,
                ZoneTaxType = zoneTaxType,
                TaxRatePercent = taxRatePercent
            };

            service.Session.SendCommand(0, CityBudgetCommand.Id, cmd.Serialize());
        }

        private sealed class Observer : SessionObserverBase
        {
            private readonly ConcurrentQueue<SimulationCommandMessage> _sink;
            public Observer(ConcurrentQueue<SimulationCommandMessage> sink) { _sink = sink; }
            public override void OnCommandReceived(SimulationCommandMessage command)
            {
                if (command.CommandId == CityBudgetCommand.Id)
                    _sink.Enqueue(command);
            }
        }
    }
}
