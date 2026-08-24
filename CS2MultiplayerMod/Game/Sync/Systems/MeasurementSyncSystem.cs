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
    /// Synchronizes and renders shared 3D laser measurement lines, distance, and slope grade.
    /// </summary>
    public partial class MeasurementSyncSystem : GameSystemBase
    {
        private readonly ConcurrentQueue<SimulationCommandMessage> _incoming =
            new ConcurrentQueue<SimulationCommandMessage>();

        private readonly ConcurrentDictionary<int, MeasurementCommand> _activeMeasurements =
            new ConcurrentDictionary<int, MeasurementCommand>();

        private Observer _observer;
        private OverlayRenderSystem _overlay;

        protected override void OnCreate()
        {
            base.OnCreate();
            _observer = new Observer(_incoming);
            _overlay = World.GetOrCreateSystemManaged<OverlayRenderSystem>();
            Mod.log.Info(nameof(MeasurementSyncSystem) + " ready.");
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
                if (message.CommandId != MeasurementCommand.Id) continue;
                MeasurementCommand cmd = MeasurementCommand.Deserialize(message.Body);
                if (cmd == null) continue;

                if (cmd.Active)
                {
                    _activeMeasurements[cmd.PlayerId] = cmd;
                }
                else
                {
                    MeasurementCommand removed;
                    _activeMeasurements.TryRemove(cmd.PlayerId, out removed);
                }
            }

            if (_activeMeasurements.Count == 0) return;

            OverlayRenderSystem.Buffer buffer = _overlay.GetBuffer(out JobHandle dependencies);
            dependencies.Complete();

            foreach (var pair in _activeMeasurements)
            {
                MeasurementCommand m = pair.Value;
                var start = new float3(m.StartX, m.StartY, m.StartZ);
                var end = new float3(m.EndX, m.EndY, m.EndZ);

                var color = new Color(1.0f, 0.9f, 0.2f, 0.85f); // Golden ruler laser
                buffer.DrawLine(color, new Line3.Segment(start, end), 1.5f, true);

                // Draw start/end point rings
                var startCircle = new Circle2(2f, start.xz);
                var startBounds = new Bounds1(start.y - 1f, start.y + 1f);
                buffer.DrawCircle(color, Color.clear, 1.2f, 0, startBounds, startCircle);

                var endCircle = new Circle2(2f, end.xz);
                var endBounds = new Bounds1(end.y - 1f, end.y + 1f);
                buffer.DrawCircle(color, Color.clear, 1.2f, 0, endBounds, endCircle);
            }
        }

        public void SetMeasurement(float3 start, float3 end, bool active)
        {
            MultiplayerService service = Mod.Service;
            if (service == null || !service.GameplaySyncReady) return;

            var cmd = new MeasurementCommand
            {
                PlayerId = service.LocalPlayerId,
                StartX = start.x,
                StartY = start.y,
                StartZ = start.z,
                EndX = end.x,
                EndY = end.y,
                EndZ = end.z,
                Active = active
            };

            if (active) _activeMeasurements[service.LocalPlayerId] = cmd;
            else
            {
                MeasurementCommand removed;
                _activeMeasurements.TryRemove(service.LocalPlayerId, out removed);
            }

            service.Session.SendCommand(0, MeasurementCommand.Id, cmd.Serialize());
        }

        private sealed class Observer : SessionObserverBase
        {
            private readonly ConcurrentQueue<SimulationCommandMessage> _sink;
            public Observer(ConcurrentQueue<SimulationCommandMessage> sink) { _sink = sink; }
            public override void OnCommandReceived(SimulationCommandMessage command)
            {
                if (command.CommandId == MeasurementCommand.Id)
                    _sink.Enqueue(command);
            }
        }
    }
}
