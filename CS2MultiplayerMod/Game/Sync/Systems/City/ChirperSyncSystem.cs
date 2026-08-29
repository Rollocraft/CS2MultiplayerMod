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
    /// Synchronizes public citizen Chirper social media feed posts across all players.
    /// </summary>
    public partial class ChirperSyncSystem : GameSystemBase
    {
        private readonly ConcurrentQueue<SimulationCommandMessage> _incoming =
            new ConcurrentQueue<SimulationCommandMessage>();

        private Observer _observer;
        private bool _registered;

        protected override void OnCreate()
        {
            base.OnCreate();
            _observer = new Observer(_incoming);
            Mod.log.Info(nameof(ChirperSyncSystem) + " ready.");
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
                if (message.CommandId != ChirperCommand.Id) continue;
                ChirperCommand cmd = ChirperCommand.Deserialize(message.Body);
                if (cmd == null) continue;

                Mod.log.Info($"[MP] [Chirper] @{cmd.SenderName}: \"{cmd.MessageText}\"");
            }
        }

        public void PostChirp(string senderName, string messageText, byte avatarIndex = 0)
        {
            MultiplayerService service = Mod.Service;
            if (service == null || !service.GameplaySyncReady || string.IsNullOrEmpty(messageText)) return;

            var cmd = new ChirperCommand
            {
                SenderPlayerId = service.LocalPlayerId,
                SenderName = senderName ?? "Mayor",
                MessageText = messageText,
                AvatarIndex = avatarIndex
            };

            service.Session.SendCommand(0, ChirperCommand.Id, cmd.Serialize());
        }

        private sealed class Observer : SessionObserverBase
        {
            private readonly ConcurrentQueue<SimulationCommandMessage> _sink;
            public Observer(ConcurrentQueue<SimulationCommandMessage> sink) { _sink = sink; }
            public override void OnCommandReceived(SimulationCommandMessage command)
            {
                if (command.CommandId == ChirperCommand.Id)
                    _sink.Enqueue(command);
            }
        }
    }
}
