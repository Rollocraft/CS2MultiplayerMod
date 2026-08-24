using System;
using System.Collections.Concurrent;
using Colossal.Mathematics;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Sync.Commands;
using Game;
using Game.Rendering;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Synchronizes and renders active co-op tool ghost blueprints and placement holograms.
    /// </summary>
    public partial class GhostPreviewSyncSystem : GameSystemBase
    {
        private readonly ConcurrentQueue<SimulationCommandMessage> _incoming =
            new ConcurrentQueue<SimulationCommandMessage>();

        private readonly ConcurrentDictionary<int, GhostPlacementCommand> _activeGhosts =
            new ConcurrentDictionary<int, GhostPlacementCommand>();

        private Observer _observer;
        private OverlayRenderSystem _overlay;

        protected override void OnCreate()
        {
            base.OnCreate();
            _observer = new Observer(_incoming);
            _overlay = World.GetOrCreateSystemManaged<OverlayRenderSystem>();
            Mod.log.Info(nameof(GhostPreviewSyncSystem) + " ready.");
        }

        protected override void OnUpdate()
        {
            MultiplayerService service = Mod.Service;
            if (service == null || _overlay == null || !service.GameplaySyncReady)
            {
                while (_incoming.TryDequeue(out _)) { }
                return;
            }

            while (_incoming.TryDequeue(out SimulationCommandMessage message))
            {
                if (message.CommandId != GhostPlacementCommand.Id) continue;
                GhostPlacementCommand cmd = GhostPlacementCommand.Deserialize(message.Body);
                if (cmd == null) continue;

                _activeGhosts[cmd.PlayerId] = cmd;
            }

            if (_activeGhosts.Count == 0) return;

            OverlayRenderSystem.Buffer buffer = _overlay.GetBuffer(out JobHandle dependencies);
            dependencies.Complete();

            foreach (var pair in _activeGhosts)
            {
                GhostPlacementCommand ghost = pair.Value;
                var pos = new float3(ghost.X, ghost.Y, ghost.Z);

                // Render holographic cyan outline for the planned object footprint
                var circle = new Circle2(8f, pos.xz);
                var bounds = new Bounds1(pos.y - 1f, pos.y + 1f);
                var color = new Color(0.2f, 0.85f, 1.0f, 0.5f);
                buffer.DrawCircle(color, Color.clear, 1.5f, 0, bounds, circle);
            }
        }

        public void BroadcastGhost(float x, float y, float z, float rotationYaw, string prefabName)
        {
            MultiplayerService service = Mod.Service;
            if (service == null || !service.GameplaySyncReady) return;

            var cmd = new GhostPlacementCommand
            {
                PlayerId = service.LocalPlayerId,
                X = x,
                Y = y,
                Z = z,
                RotationYaw = rotationYaw,
                PrefabName = prefabName ?? ""
            };

            service.Session.SendCommand(0, GhostPlacementCommand.Id, cmd.Serialize());
        }

        private sealed class Observer : SessionObserverBase
        {
            private readonly ConcurrentQueue<SimulationCommandMessage> _sink;
            public Observer(ConcurrentQueue<SimulationCommandMessage> sink) { _sink = sink; }
            public override void OnCommandReceived(SimulationCommandMessage command)
            {
                if (command.CommandId == GhostPlacementCommand.Id)
                    _sink.Enqueue(command);
            }
        }
    }
}
