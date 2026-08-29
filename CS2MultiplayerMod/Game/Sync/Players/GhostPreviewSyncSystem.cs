using System;
using System.Collections.Concurrent;
using Colossal.Mathematics;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Sync.Commands;
using Game;
using Game.Prefabs;
using Game.Rendering;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace CS2MultiplayerMod.Game.Sync.Players
{
    /// <summary>
    /// Synchronizes and renders real-time co-op tool ghost blueprints and 3D placement holograms
    /// with player-attributed colors, rotation vectors, and dynamic footprint bounds.
    /// </summary>
    public partial class GhostPreviewSyncSystem : GameSystemBase
    {
        private const long GhostStaleTimeoutMs = 4000;

        private readonly ConcurrentQueue<SimulationCommandMessage> _incoming =
            new ConcurrentQueue<SimulationCommandMessage>();

        private readonly ConcurrentDictionary<int, GhostPlacementState> _activeGhosts =
            new ConcurrentDictionary<int, GhostPlacementState>();

        private Observer _observer;
        private bool _observerRegistered;

        private OverlayRenderSystem _overlay;
        private CameraUpdateSystem _cameraUpdateSystem;
        private global::Game.Tools.ToolSystem _toolSystem;
        private PrefabSystem _prefabSystem;

        private long _lastBroadcastMs;
        private float3 _lastBroadcastPos;
        private float _lastBroadcastYaw;
        private string _lastBroadcastPrefab = "";
        private bool _hasActiveLocalGhost;

        private sealed class GhostPlacementState
        {
            public GhostPlacementCommand Command;
            public long LastSeenMs;
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            _observer = new Observer(_incoming);
            _overlay = World.GetOrCreateSystemManaged<OverlayRenderSystem>();
            _cameraUpdateSystem = World.GetOrCreateSystemManaged<CameraUpdateSystem>();
            _toolSystem = World.GetOrCreateSystemManaged<global::Game.Tools.ToolSystem>();
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            Mod.log.Info(nameof(GhostPreviewSyncSystem) + " ready.");
        }

        protected override void OnDestroy()
        {
            if (_observer != null && _observerRegistered && Mod.Service?.Session != null)
            {
                Mod.Service.Session.RemoveObserver(_observer);
                _observerRegistered = false;
            }
            _activeGhosts.Clear();
            base.OnDestroy();
        }

        protected override void OnUpdate()
        {
            MultiplayerService service = Mod.Service;
            if (service == null || _overlay == null || !service.GameplaySyncReady)
            {
                if (_observerRegistered && _observer != null && service?.Session != null)
                {
                    service.Session.RemoveObserver(_observer);
                    _observerRegistered = false;
                }
                while (_incoming.TryDequeue(out _)) { }
                return;
            }

            // Ensure observer is subscribed to the active session
            if (!_observerRegistered && service.Session != null)
            {
                service.Session.AddObserver(_observer);
                _observerRegistered = true;
            }

            long now = service.NowMs;

            // Sample local tool state to broadcast hover blueprints to teammates
            TrackLocalToolHover(service, now);

            // Process incoming ghost placement packets from network
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
                    _activeGhosts[cmd.PlayerId] = new GhostPlacementState
                    {
                        Command = cmd,
                        LastSeenMs = now
                    };
                }
            }

            if (_activeGhosts.Count == 0) return;

            // Purge expired ghost states
            foreach (var pair in _activeGhosts)
            {
                if (now - pair.Value.LastSeenMs > GhostStaleTimeoutMs)
                {
                    _activeGhosts.TryRemove(pair.Key, out _);
                }
            }

            if (_activeGhosts.Count == 0) return;

            // Render 3D holographic wireframe bounding boxes and blueprint footprints
            OverlayRenderSystem.Buffer buffer = _overlay.GetBuffer(out JobHandle dependencies);
            dependencies.Complete();

            float3 camPos = _cameraUpdateSystem != null ? _cameraUpdateSystem.position : float3.zero;

            foreach (var pair in _activeGhosts)
            {
                GhostPlacementCommand ghost = pair.Value.Command;
                if (ghost == null || ghost.PlayerId == service.LocalPlayerId) continue;

                var pos = new float3(ghost.X, ghost.Y, ghost.Z);

                // Distance culling: Skip rendering overlays far outside camera range
                if (_cameraUpdateSystem != null && !Infrastructure.SpatialGridCulling.IsWithinCullingDistance(camPos, pos))
                    continue;

                // Attributed player color
                Color playerColor = RemotePlayerMarkerSystem.Palette[((ghost.PlayerId % RemotePlayerMarkerSystem.Palette.Length) + RemotePlayerMarkerSystem.Palette.Length) % RemotePlayerMarkerSystem.Palette.Length];
                playerColor.a = 0.85f;

                RenderGhostHologram(buffer, pos, ghost.RotationYaw, playerColor);
            }

            _overlay.AddBufferWriter(default);
        }

        private void RenderGhostHologram(OverlayRenderSystem.Buffer buffer, float3 pos, float yaw, Color color)
        {
            float cosY = math.cos(yaw);
            float sinY = math.sin(yaw);

            float halfW = 8f;
            float halfD = 8f;
            float height = 10f;

            // 4 Ground Footprint Corners rotated by yaw
            float3 p0 = pos + new float3(-halfW * cosY - -halfD * sinY, 0f, -halfW * sinY + -halfD * cosY);
            float3 p1 = pos + new float3( halfW * cosY - -halfD * sinY, 0f,  halfW * sinY + -halfD * cosY);
            float3 p2 = pos + new float3( halfW * cosY -  halfD * sinY, 0f,  halfW * sinY +  halfD * cosY);
            float3 p3 = pos + new float3(-halfW * cosY -  halfD * sinY, 0f, -halfW * sinY +  halfD * cosY);

            // 4 Top Box Corners
            float3 top0 = p0 + new float3(0f, height, 0f);
            float3 top1 = p1 + new float3(0f, height, 0f);
            float3 top2 = p2 + new float3(0f, height, 0f);
            float3 top3 = p3 + new float3(0f, height, 0f);

            // Ground perimeter rectangle
            buffer.DrawLine(color, new Line3.Segment(p0, p1), 2.2f, true);
            buffer.DrawLine(color, new Line3.Segment(p1, p2), 2.2f, true);
            buffer.DrawLine(color, new Line3.Segment(p2, p3), 2.2f, true);
            buffer.DrawLine(color, new Line3.Segment(p3, p0), 2.2f, true);

            // 4 Vertical Holographic Pillars
            Color pillarColor = new Color(color.r, color.g, color.b, 0.65f);
            buffer.DrawLine(pillarColor, new Line3.Segment(p0, top0), 1.6f, true);
            buffer.DrawLine(pillarColor, new Line3.Segment(p1, top1), 1.6f, true);
            buffer.DrawLine(pillarColor, new Line3.Segment(p2, top2), 1.6f, true);
            buffer.DrawLine(pillarColor, new Line3.Segment(p3, top3), 1.6f, true);

            // Top boundary rectangle
            Color topColor = new Color(color.r, color.g, color.b, 0.45f);
            buffer.DrawLine(topColor, new Line3.Segment(top0, top1), 1.4f, true);
            buffer.DrawLine(topColor, new Line3.Segment(top1, top2), 1.4f, true);
            buffer.DrawLine(topColor, new Line3.Segment(top2, top3), 1.4f, true);
            buffer.DrawLine(topColor, new Line3.Segment(top3, top0), 1.4f, true);

            // Direction arrow pointing in the forward facing direction
            float3 forwardDir = new float3(-sinY, 0f, cosY);
            float3 frontMid = (p1 + p2) * 0.5f;
            buffer.DrawLine(color, new Line3.Segment(pos, frontMid + forwardDir * 3.5f), 2.8f, true);

            // Concentric blueprint focus rings
            Color fill = new Color(color.r, color.g, color.b, 0.12f);
            buffer.DrawCircle(color, fill, 2.0f, default, new float2(0f, 1f), pos, 16f);
            buffer.DrawCircle(color, Color.clear, 1.2f, default, new float2(0f, 1f), pos, 28f);
        }

        private void TrackLocalToolHover(MultiplayerService service, long now)
        {
            if (_toolSystem == null) _toolSystem = World.GetOrCreateSystemManaged<global::Game.Tools.ToolSystem>();
            if (_toolSystem == null) return;

            global::Game.Tools.ToolBaseSystem active = _toolSystem.activeTool;
            bool isPlacing = active != null && !(active is global::Game.Tools.DefaultToolSystem);

            if (isPlacing)
            {
                float3 hoverPos = float3.zero;
                float yaw = 0f;
                bool foundRaycast = false;

                // Extract active prefab identity
                string prefabName = "";
                PrefabBase activePrefab = _toolSystem.activePrefab;
                if (activePrefab != null)
                {
                    prefabName = activePrefab.name;
                }
                if (string.IsNullOrEmpty(prefabName))
                {
                    prefabName = active.GetType().Name;
                }

                if (active is global::Game.Tools.ObjectToolSystem objectTool)
                {
                    try
                    {
                        NativeList<global::Game.Tools.ControlPoint> points = objectTool.GetControlPoints(out var deps);
                        deps.Complete();
                        if (points.IsCreated && points.Length > 0)
                        {
                            hoverPos = points[0].m_Position;
                            quaternion rot = points[0].m_Rotation;
                            float3 euler = math.Euler(rot);
                            yaw = euler.y;
                            foundRaycast = true;
                        }
                    }
                    catch { }
                }
                else if (active is global::Game.Tools.NetToolSystem netTool)
                {
                    try
                    {
                        NativeList<global::Game.Tools.ControlPoint> points = netTool.GetControlPoints(out var deps);
                        deps.Complete();
                        if (points.IsCreated && points.Length > 0)
                        {
                            hoverPos = points[0].m_Position;
                            quaternion rot = points[0].m_Rotation;
                            float3 euler = math.Euler(rot);
                            yaw = euler.y;
                            foundRaycast = true;
                        }
                    }
                    catch { }
                }

                if (!foundRaycast && _cameraUpdateSystem?.gamePlayController != null)
                {
                    hoverPos = _cameraUpdateSystem.gamePlayController.pivot;
                    yaw = _cameraUpdateSystem.gamePlayController.rotation.y;
                }

                bool moved = math.distancesq(hoverPos, _lastBroadcastPos) > 0.15f ||
                             math.abs(yaw - _lastBroadcastYaw) > 0.05f ||
                             prefabName != _lastBroadcastPrefab;

                if (moved || (now - _lastBroadcastMs > 400 && _hasActiveLocalGhost))
                {
                    _lastBroadcastMs = now;
                    _lastBroadcastPos = hoverPos;
                    _lastBroadcastYaw = yaw;
                    _lastBroadcastPrefab = prefabName;
                    _hasActiveLocalGhost = true;
                    BroadcastGhost(hoverPos.x, hoverPos.y, hoverPos.z, yaw, prefabName);
                }
            }
            else if (_hasActiveLocalGhost)
            {
                _hasActiveLocalGhost = false;
                _lastBroadcastPos = float3.zero;
                _lastBroadcastYaw = 0f;
                _lastBroadcastPrefab = "";
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
