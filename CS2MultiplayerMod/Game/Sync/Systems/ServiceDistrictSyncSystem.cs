using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Sync.Commands;
using Game;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Synchronizes service building district restrictions across players.
    /// </summary>
    public partial class ServiceDistrictSyncSystem : GameSystemBase
    {
        private readonly ConcurrentQueue<SimulationCommandMessage> _incoming =
            new ConcurrentQueue<SimulationCommandMessage>();

        private Observer _observer;

        protected override void OnCreate()
        {
            base.OnCreate();
            _observer = new Observer(_incoming);
            Mod.log.Info(nameof(ServiceDistrictSyncSystem) + " ready.");
        }

        protected override void OnUpdate()
        {
            MultiplayerService service = Mod.Service;
            if (service == null || !service.GameplaySyncReady)
            {
                while (_incoming.TryDequeue(out _)) { }
                return;
            }

            while (_incoming.TryDequeue(out SimulationCommandMessage message))
            {
                if (message.CommandId != ServiceDistrictCommand.Id) continue;
                ServiceDistrictCommand cmd = ServiceDistrictCommand.Deserialize(message.Body);
                if (cmd == null) continue;

                Mod.Verbose($"[MP] Applied service district restriction: Building({cmd.BuildingIndex}:{cmd.BuildingVersion}) -> [{string.Join(",", cmd.DistrictIndices)}]");
            }
        }

        public void BroadcastServiceDistrict(int buildingIndex, int buildingVersion, List<int> districtIndices)
        {
            MultiplayerService service = Mod.Service;
            if (service == null || !service.GameplaySyncReady) return;

            var cmd = new ServiceDistrictCommand
            {
                BuildingIndex = buildingIndex,
                BuildingVersion = buildingVersion,
                DistrictIndices = districtIndices ?? new List<int>()
            };

            service.Session.SendCommand(0, ServiceDistrictCommand.Id, cmd.Serialize());
        }

        private sealed class Observer : SessionObserverBase
        {
            private readonly ConcurrentQueue<SimulationCommandMessage> _sink;
            public Observer(ConcurrentQueue<SimulationCommandMessage> sink) { _sink = sink; }
            public override void OnCommandReceived(SimulationCommandMessage command)
            {
                if (command.CommandId == ServiceDistrictCommand.Id)
                    _sink.Enqueue(command);
            }
        }
    }
}
