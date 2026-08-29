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
        private CameraUpdateSystem _cameraUpdateSystem;
        private global::Game.Tools.ToolSystem _toolSystem;

        private long _lastBroadcastMs;
        private float3 _lastBroadcastPos;
        private bool _hasActiveLocalGhost;

        protected override void OnCreate()
        {
            base.OnCreate();
            _observer = new Observer(_incoming);
            _overlay = World.GetOrCreateSystemManaged<OverlayRenderSystem>();
            _cameraUpdateSystem = World.GetOrCreateSystemManaged<CameraUpdateSystem>();
            _toolSystem = World.GetOrCreateSystemManaged<global::Game.Tools.ToolSystem>();
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

            // Sample local tool state to broadcast hover ghosts to teammates
            TrackLocalToolHover(service);

            while (_incoming.TryDequeue(out SimulationCommandMessage message))
            {
                if (message.CommandId != GhostPlacementCommand.Id) continue;
                GhostPlacementCommand cmd = GhostPlacementCommand.Deserialize(message.Body);
                if (cmd == null) continue;

                if (float.IsNaN(cmd.X) || string.IsNullOrEmpty(cmd.PrefabName))
                {
                    _activeGhosts.TryRemove(cmd.PlayerId, out _);
                }
                else
                {
                    _activeGhosts[cmd.PlayerId] = cmd;
                }
            }

            if (_activeGhosts.Count == 0) return;

            OverlayRenderSystem.Buffer buffer = _overlay.GetBuffer(out JobHandle dependencies);
            dependencies.Complete();

            float3 camPos = _cameraUpdateSystem != null ? _cameraUpdateSystem.position : float3.zero;

            foreach (var pair in _activeGhosts)
            {
                GhostPlacementCommand ghost = pair.Value;
                if (ghost.PlayerId == service.LocalPlayerId) continue; // Don't draw over local tool's own preview
                var pos = new float3(ghost.X, ghost.Y, ghost.Z);

                // Distance culling: Skip rendering visual overlays for distant preview ghosts far outside camera range
                if (_cameraUpdateSystem != null && !Infrastructure.SpatialGridCulling.IsWithinCullingDistance(camPos, pos))
                    continue;

                // Render holographic blueprint outline for the planned object footprint
                var color = new Color(0.2f, 0.85f, 1.0f, 0.6f);
                var innerColor = new Color(0.2f, 0.85f, 1.0f, 0.15f);
                buffer.DrawCircle(color, innerColor, 1.5f, default, new float2(0f, 1f), pos, 18f);
                buffer.DrawCircle(color, Color.clear, 1.0f, default, new float2(0f, 1f), pos, 8f);
            }

            _overlay.AddBufferWriter(default);
        }

        private void TrackLocalToolHover(MultiplayerService service)
        {
            if (_toolSystem == null) _toolSystem = World.GetOrCreateSystemManaged<global::Game.Tools.ToolSystem>();
            if (_toolSystem == null) return;

            global::Game.Tools.ToolBaseSystem active = _toolSystem.activeTool;
            bool isPlacing = active != null && !(active is global::Game.Tools.DefaultToolSystem);

            long now = service.NowMs;
            if (isPlacing)
            {
                float3 hoverPos = float3.zero;
                bool foundRaycast = false;

                if (active is global::Game.Tools.ObjectToolSystem objectTool)
                {
                    try
                    {
                        Unity.Collections.NativeList<global::Game.Tools.ControlPoint> points = objectTool.GetControlPoints(out var deps);
                        deps.Complete();
                        if (points.IsCreated && points.Length > 0)
                        {
                            hoverPos = points[0].m_Position;
                            foundRaycast = true;
                        }
                    }
                    catch { }
                }
                else if (active is global::Game.Tools.NetToolSystem netTool)
                {
                    try
                    {
                        Unity.Collections.NativeList<global::Game.Tools.ControlPoint> points = netTool.GetControlPoints(out var deps);
                        deps.Complete();
                        if (points.IsCreated && points.Length > 0)
                        {
                            hoverPos = points[0].m_Position;
                            foundRaycast = true;
                        }
                    }
                    catch { }
                }

                if (!foundRaycast && _cameraUpdateSystem?.gamePlayController != null)
                {
                    hoverPos = _cameraUpdateSystem.gamePlayController.pivot;
                }

                bool moved = math.distancesq(hoverPos, _lastBroadcastPos) > 0.25f;
                if (moved || (now - _lastBroadcastMs > 1000 && _hasActiveLocalGhost))
                {
                    _lastBroadcastMs = now;
                    _lastBroadcastPos = hoverPos;
                    _hasActiveLocalGhost = true;
                    string toolName = active.GetType().Name;
                    BroadcastGhost(hoverPos.x, hoverPos.y, hoverPos.z, 0f, toolName);
                }
            }
            else if (_hasActiveLocalGhost)
            {
                _hasActiveLocalGhost = false;
                _lastBroadcastPos = float3.zero;
                BroadcastGhost(float.NaN, 0f, 0f, 0f, "");
            }
        }

        public void RemoveGhost(int playerId)
        {
            _activeGhosts.TryRemove(playerId, out _);
        }

        public void PruneInactiveGhosts(System.Collections.Generic.HashSet<int> activePlayerIds)
        {
            if (activePlayerIds == null) return;
            foreach (var key in _activeGhosts.Keys)
            {
                if (!activePlayerIds.Contains(key))
                {
                    _activeGhosts.TryRemove(key, out _);
                }
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
