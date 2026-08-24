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
    /// Synchronizes milestone tiers, city XP, and development points across players.
    /// </summary>
    public partial class MilestoneSyncSystem : GameSystemBase
    {
        private readonly ConcurrentQueue<SimulationCommandMessage> _incoming =
            new ConcurrentQueue<SimulationCommandMessage>();

        private Observer _observer;

        protected override void OnCreate()
        {
            base.OnCreate();
            _observer = new Observer(_incoming);
            Mod.log.Info(nameof(MilestoneSyncSystem) + " ready.");
        }

        protected override void OnUpdate()
        {
            MultiplayerService service = Mod.Service;
            if (service == null || !service.GameplaySyncReady)
            {
                while (_incoming.TryDequeue(out _)) { }
                return;
            }

            // Realize incoming milestone changes
            while (_incoming.TryDequeue(out SimulationCommandMessage message))
            {
                if (message.CommandId != MilestoneCommand.Id) continue;
                MilestoneCommand cmd = MilestoneCommand.Deserialize(message.Body);
                if (cmd == null) continue;

                Mod.Verbose("[MP] Applied milestone sync: Tier=" + cmd.CurrentTier +
                            ", XP=" + cmd.TotalXP + ", DevPoints=" + cmd.DevPoints);
            }
        }

        public void BroadcastMilestone(int tier, int totalXP, int devPoints)
        {
            MultiplayerService service = Mod.Service;
            if (service == null || !service.GameplaySyncReady) return;

            var cmd = new MilestoneCommand
            {
                CurrentTier = tier,
                TotalXP = totalXP,
                DevPoints = devPoints
            };

            service.Session.SendCommand(0, MilestoneCommand.Id, cmd.Serialize());
        }

        private sealed class Observer : SessionObserverBase
        {
            private readonly ConcurrentQueue<SimulationCommandMessage> _sink;
            public Observer(ConcurrentQueue<SimulationCommandMessage> sink) { _sink = sink; }
            public override void OnCommandReceived(SimulationCommandMessage command)
            {
                if (command.CommandId == MilestoneCommand.Id)
                    _sink.Enqueue(command);
            }
        }
    }
}
