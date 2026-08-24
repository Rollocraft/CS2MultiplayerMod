using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Sync.Commands;
using Game;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Synchronizes shared camera navigation bookmarks (/mark, /goto) across players.
    /// </summary>
    public partial class CityBookmarkSyncSystem : GameSystemBase
    {
        private readonly ConcurrentQueue<SimulationCommandMessage> _incoming =
            new ConcurrentQueue<SimulationCommandMessage>();

        private readonly ConcurrentDictionary<string, float3> _bookmarks =
            new ConcurrentDictionary<string, float3>(StringComparer.OrdinalIgnoreCase);

        private Observer _observer;

        public IReadOnlyDictionary<string, float3> Bookmarks => _bookmarks;

        protected override void OnCreate()
        {
            base.OnCreate();
            _observer = new Observer(_incoming);
            Mod.log.Info(nameof(CityBookmarkSyncSystem) + " ready.");
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
                if (message.CommandId != BookmarkCommand.Id) continue;
                BookmarkCommand cmd = BookmarkCommand.Deserialize(message.Body);
                if (cmd == null || string.IsNullOrEmpty(cmd.BookmarkName)) continue;

                _bookmarks[cmd.BookmarkName] = new float3(cmd.X, cmd.Y, cmd.Z);
                Mod.Verbose("[MP] Applied bookmark sync: '" + cmd.BookmarkName + "' at (" +
                            cmd.X + ", " + cmd.Y + ", " + cmd.Z + ")");
            }
        }

        public bool TryGetBookmark(string name, out float3 position)
        {
            return _bookmarks.TryGetValue(name, out position);
        }

        public void SaveBookmark(string name, float3 position)
        {
            MultiplayerService service = Mod.Service;
            if (service == null || !service.GameplaySyncReady || string.IsNullOrEmpty(name)) return;

            _bookmarks[name] = position;

            var cmd = new BookmarkCommand
            {
                BookmarkName = name,
                X = position.x,
                Y = position.y,
                Z = position.z
            };

            service.Session.SendCommand(0, BookmarkCommand.Id, cmd.Serialize());
        }

        private sealed class Observer : SessionObserverBase
        {
            private readonly ConcurrentQueue<SimulationCommandMessage> _sink;
            public Observer(ConcurrentQueue<SimulationCommandMessage> sink) { _sink = sink; }
            public override void OnCommandReceived(SimulationCommandMessage command)
            {
                if (command.CommandId == BookmarkCommand.Id)
                    _sink.Enqueue(command);
            }
        }
    }
}
