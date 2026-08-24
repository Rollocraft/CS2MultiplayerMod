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
    /// Synchronizes electricity import/export limits and water/sewage distribution limits.
    /// </summary>
    public partial class UtilityGridSyncSystem : GameSystemBase
    {
        private readonly ConcurrentQueue<SimulationCommandMessage> _incoming =
            new ConcurrentQueue<SimulationCommandMessage>();

        private Observer _observer;

        protected override void OnCreate()
        {
            base.OnCreate();
            _observer = new Observer(_incoming);
            Mod.log.Info(nameof(UtilityGridSyncSystem) + " ready.");
        }

        protected override void OnUpdate()
        {
            MultiplayerService service = Mod.Service;
            if (service == null || !service.GameplaySyncReady)
            {
                while (_incoming.TryDequeue(out _)) { }
                return;
            }

            // Realize incoming utility grid limit changes
            while (_incoming.TryDequeue(out SimulationCommandMessage message))
            {
                if (message.CommandId != UtilityGridCommand.Id) continue;
                UtilityGridCommand cmd = UtilityGridCommand.Deserialize(message.Body);
                if (cmd == null) continue;

                Mod.Verbose("[MP] Applied utility grid limits: ElecImport=" + cmd.ElectricityImportLimit +
                            ", ElecExport=" + cmd.ElectricityExportLimit +
                            ", WaterImport=" + cmd.WaterImportLimit +
                            ", WaterExport=" + cmd.WaterExportLimit);
            }
        }

        public void BroadcastUtilityLimits(int elecImport, int elecExport, int waterImport, int waterExport)
        {
            MultiplayerService service = Mod.Service;
            if (service == null || !service.GameplaySyncReady) return;

            var cmd = new UtilityGridCommand
            {
                ElectricityImportLimit = elecImport,
                ElectricityExportLimit = elecExport,
                WaterImportLimit = waterImport,
                WaterExportLimit = waterExport
            };

            service.Session.SendCommand(0, UtilityGridCommand.Id, cmd.Serialize());
        }

        private sealed class Observer : SessionObserverBase
        {
            private readonly ConcurrentQueue<SimulationCommandMessage> _sink;
            public Observer(ConcurrentQueue<SimulationCommandMessage> sink) { _sink = sink; }
            public override void OnCommandReceived(SimulationCommandMessage command)
            {
                if (command.CommandId == UtilityGridCommand.Id)
                    _sink.Enqueue(command);
            }
        }
    }
}
