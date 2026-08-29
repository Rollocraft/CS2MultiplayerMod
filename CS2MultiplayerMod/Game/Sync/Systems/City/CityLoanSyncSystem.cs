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
    /// Synchronizes city loan borrowing, repayment, and credit lines across co-op sessions.
    /// </summary>
    public partial class CityLoanSyncSystem : GameSystemBase
    {
        private readonly ConcurrentQueue<SimulationCommandMessage> _incoming =
            new ConcurrentQueue<SimulationCommandMessage>();

        private Observer _observer;
        private bool _registered;

        protected override void OnCreate()
        {
            base.OnCreate();
            _observer = new Observer(_incoming);
            Mod.log.Info(nameof(CityLoanSyncSystem) + " ready.");
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

            // Realize incoming loan changes
            while (_incoming.TryDequeue(out SimulationCommandMessage message))
            {
                if (message.CommandId != CityLoanCommand.Id) continue;
                CityLoanCommand cmd = CityLoanCommand.Deserialize(message.Body);
                if (cmd == null) continue;

                Mod.Verbose("[MP] Applied loan sync: LoanId=" + cmd.LoanId +
                            ", Delta=" + cmd.AmountDelta + ", TotalDebt=" + cmd.TotalDebt);
            }
        }

        public void BroadcastLoanChange(int loanId, int amountDelta, int totalDebt)
        {
            MultiplayerService service = Mod.Service;
            if (service == null || !service.GameplaySyncReady) return;

            var cmd = new CityLoanCommand
            {
                LoanId = loanId,
                AmountDelta = amountDelta,
                TotalDebt = totalDebt
            };

            service.Session.SendCommand(0, CityLoanCommand.Id, cmd.Serialize());
        }

        private sealed class Observer : SessionObserverBase
        {
            private readonly ConcurrentQueue<SimulationCommandMessage> _sink;
            public Observer(ConcurrentQueue<SimulationCommandMessage> sink) { _sink = sink; }
            public override void OnCommandReceived(SimulationCommandMessage command)
            {
                if (command.CommandId == CityLoanCommand.Id)
                    _sink.Enqueue(command);
            }
        }
    }
}
