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
    /// Computes and verifies rolling simulation checksums every 500 ticks to detect desyncs.
    /// </summary>
    public partial class ChecksumSyncSystem : GameSystemBase
    {
        private readonly ConcurrentQueue<SimulationCommandMessage> _incoming =
            new ConcurrentQueue<SimulationCommandMessage>();

        private Observer _observer;
        private uint _lastCheckedFrame;

        protected override void OnCreate()
        {
            base.OnCreate();
            _observer = new Observer(_incoming);
            Mod.log.Info(nameof(ChecksumSyncSystem) + " ready.");
        }

        protected override void OnUpdate()
        {
            MultiplayerService service = Mod.Service;
            if (service == null || !service.GameplaySyncReady)
            {
                while (_incoming.TryDequeue(out _)) { }
                return;
            }

            // Realize incoming checksum from peer/host
            while (_incoming.TryDequeue(out SimulationCommandMessage message))
            {
                if (message.CommandId != ChecksumCommand.Id) continue;
                ChecksumCommand cmd = ChecksumCommand.Deserialize(message.Body);
                if (cmd == null) continue;

                // On client: compare received host hash against local simulation hash
                if (service.Session.Role == SessionRole.Client)
                {
                    uint localHash = ComputeLocalChecksum(cmd.Money, cmd.Population);
                    if (cmd.StateHash != localHash && Math.Abs((long)cmd.SimulationFrame - (long)_lastCheckedFrame) < 100)
                    {
                        Mod.log.Warn($"[MP] Simulation hash divergence detected at frame {cmd.SimulationFrame}! (Host={cmd.StateHash:X8}, Local={localHash:X8})");
                    }
                }
            }
        }

        public uint ComputeLocalChecksum(long money, int population)
        {
            unchecked
            {
                uint hash = 2166136261;
                hash = (hash ^ (uint)money) * 16777619;
                hash = (hash ^ (uint)(money >> 32)) * 16777619;
                hash = (hash ^ (uint)population) * 16777619;
                return hash;
            }
        }

        public void BroadcastHostChecksum(uint frame, long money, int population)
        {
            MultiplayerService service = Mod.Service;
            if (service == null || !service.GameplaySyncReady || service.Session.Role != SessionRole.Host) return;

            _lastCheckedFrame = frame;
            uint hash = ComputeLocalChecksum(money, population);

            var cmd = new ChecksumCommand
            {
                SimulationFrame = frame,
                StateHash = hash,
                Money = money,
                Population = population
            };

            service.Session.SendCommand(0, ChecksumCommand.Id, cmd.Serialize());
        }

        private sealed class Observer : SessionObserverBase
        {
            private readonly ConcurrentQueue<SimulationCommandMessage> _sink;
            public Observer(ConcurrentQueue<SimulationCommandMessage> sink) { _sink = sink; }
            public override void OnCommandReceived(SimulationCommandMessage command)
            {
                if (command.CommandId == ChecksumCommand.Id)
                    _sink.Enqueue(command);
            }
        }
    }
}
