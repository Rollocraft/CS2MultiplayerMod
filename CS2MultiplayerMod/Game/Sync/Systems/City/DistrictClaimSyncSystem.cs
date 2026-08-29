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
    /// Synchronizes district mayoral claims and ownership badges across players.
    /// </summary>
    public partial class DistrictClaimSyncSystem : GameSystemBase
    {
        private readonly ConcurrentQueue<SimulationCommandMessage> _incoming =
            new ConcurrentQueue<SimulationCommandMessage>();

        private readonly ConcurrentDictionary<long, string> _districtOwners =
            new ConcurrentDictionary<long, string>();

        private Observer _observer;
        private bool _registered;

        protected override void OnCreate()
        {
            base.OnCreate();
            _observer = new Observer(_incoming);
            Mod.log.Info(nameof(DistrictClaimSyncSystem) + " ready.");
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
                if (message.CommandId != DistrictClaimCommand.Id) continue;
                DistrictClaimCommand cmd = DistrictClaimCommand.Deserialize(message.Body);
                if (cmd == null) continue;

                long key = ((long)cmd.DistrictIndex << 32) | (uint)cmd.DistrictVersion;
                _districtOwners[key] = cmd.OwnerPlayerName;

                Mod.Verbose("[MP] Applied district claim: District (" + cmd.DistrictIndex + ":" +
                            cmd.DistrictVersion + ") claimed by " + cmd.OwnerPlayerName);
            }
        }

        public void ClaimDistrict(Entity districtEntity, int playerId, string playerName)
        {
            MultiplayerService service = Mod.Service;
            if (service == null || !service.GameplaySyncReady) return;

            var cmd = new DistrictClaimCommand
            {
                DistrictIndex = districtEntity.Index,
                DistrictVersion = districtEntity.Version,
                OwnerPlayerId = playerId,
                OwnerPlayerName = playerName ?? ""
            };

            service.Session.SendCommand(0, DistrictClaimCommand.Id, cmd.Serialize());
        }

        private sealed class Observer : SessionObserverBase
        {
            private readonly ConcurrentQueue<SimulationCommandMessage> _sink;
            public Observer(ConcurrentQueue<SimulationCommandMessage> sink) { _sink = sink; }
            public override void OnCommandReceived(SimulationCommandMessage command)
            {
                if (command.CommandId == DistrictClaimCommand.Id)
                    _sink.Enqueue(command);
            }
        }
    }
}
