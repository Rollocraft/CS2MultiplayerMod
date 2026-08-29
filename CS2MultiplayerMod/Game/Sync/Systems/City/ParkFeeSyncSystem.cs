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
    /// Synchronizes park and tourist attraction entrance admission fees across players.
    /// </summary>
    public partial class ParkFeeSyncSystem : GameSystemBase
    {
        private readonly ConcurrentQueue<SimulationCommandMessage> _incoming =
            new ConcurrentQueue<SimulationCommandMessage>();

        private Observer _observer;
        private bool _registered;

        protected override void OnCreate()
        {
            base.OnCreate();
            _observer = new Observer(_incoming);
            Mod.log.Info(nameof(ParkFeeSyncSystem) + " ready.");
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
                if (message.CommandId != ParkFeeCommand.Id) continue;
                ParkFeeCommand cmd = ParkFeeCommand.Deserialize(message.Body);
                if (cmd == null) continue;

                Mod.Verbose($"[MP] Applied park entrance fee: Park({cmd.ParkIndex}:{cmd.ParkVersion}) - Fee=${cmd.FeeAmount}");
            }
        }

        public void BroadcastParkFee(int parkIndex, int parkVersion, ushort fee)
        {
            MultiplayerService service = Mod.Service;
            if (service == null || !service.GameplaySyncReady) return;

            var cmd = new ParkFeeCommand
            {
                ParkIndex = parkIndex,
                ParkVersion = parkVersion,
                FeeAmount = fee
            };

            service.Session.SendCommand(0, ParkFeeCommand.Id, cmd.Serialize());
        }

        private sealed class Observer : SessionObserverBase
        {
            private readonly ConcurrentQueue<SimulationCommandMessage> _sink;
            public Observer(ConcurrentQueue<SimulationCommandMessage> sink) { _sink = sink; }
            public override void OnCommandReceived(SimulationCommandMessage command)
            {
                if (command.CommandId == ParkFeeCommand.Id)
                    _sink.Enqueue(command);
            }
        }
    }
}
