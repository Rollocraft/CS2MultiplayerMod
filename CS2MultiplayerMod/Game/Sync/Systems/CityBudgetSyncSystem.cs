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

        protected override void OnCreate()
        {
            base.OnCreate();
            _observer = new Observer(_incoming);
            Mod.log.Info(nameof(CityBudgetSyncSystem) + " ready.");
        }

        protected override void OnUpdate()
        {
            MultiplayerService service = Mod.Service;
            if (service == null || !service.GameplaySyncReady)
            {
                while (_incoming.TryDequeue(out _)) { }
                return;
            }

            // Realize incoming budget changes
            while (_incoming.TryDequeue(out SimulationCommandMessage message))
            {
                if (message.CommandId != CityBudgetCommand.Id) continue;
                CityBudgetCommand cmd = CityBudgetCommand.Deserialize(message.Body);
                if (cmd == null) continue;

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
