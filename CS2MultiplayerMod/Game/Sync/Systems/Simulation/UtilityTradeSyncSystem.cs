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
    /// Synchronizes regional outside connection electricity and water import/export trading switches across players.
    /// </summary>
    public partial class UtilityTradeSyncSystem : GameSystemBase
    {
        private readonly ConcurrentQueue<SimulationCommandMessage> _incoming =
            new ConcurrentQueue<SimulationCommandMessage>();

        private Observer _observer;

        public bool ElectricityImport { get; private set; } = true;
        public bool ElectricityExport { get; private set; } = true;
        public bool WaterImport { get; private set; } = true;
        public bool WaterExport { get; private set; } = true;

        private bool _registered;

        protected override void OnCreate()
        {
            base.OnCreate();
            _observer = new Observer(_incoming);
            Mod.log.Info(nameof(UtilityTradeSyncSystem) + " ready.");
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
                if (message.CommandId != UtilityTradeCommand.Id) continue;
                UtilityTradeCommand cmd = UtilityTradeCommand.Deserialize(message.Body);
                if (cmd == null) continue;

                ElectricityImport = cmd.ElectricityImport;
                ElectricityExport = cmd.ElectricityExport;
                WaterImport = cmd.WaterImport;
                WaterExport = cmd.WaterExport;

                Mod.Verbose("[MP] Applied utility trade sync: ElecImport=" + ElectricityImport +
                            ", ElecExport=" + ElectricityExport +
                            ", WaterImport=" + WaterImport +
                            ", WaterExport=" + WaterExport);
            }
        }

        public void SetTradeSettings(bool elecImport, bool elecExport, bool waterImport, bool waterExport)
        {
            ElectricityImport = elecImport;
            ElectricityExport = elecExport;
            WaterImport = waterImport;
            WaterExport = waterExport;

            MultiplayerService service = Mod.Service;
            if (service == null || !service.GameplaySyncReady) return;

            var cmd = new UtilityTradeCommand
            {
                ElectricityImport = elecImport,
                ElectricityExport = elecExport,
                WaterImport = waterImport,
                WaterExport = waterExport
            };

            service.Session.SendCommand(0, UtilityTradeCommand.Id, cmd.Serialize());
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
                if (command.CommandId == UtilityTradeCommand.Id)
                    _incoming.Enqueue(command);
            }
        }
    }
}
