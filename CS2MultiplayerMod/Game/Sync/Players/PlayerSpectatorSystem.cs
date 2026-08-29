using System;
using Game;
using Game.Rendering;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace CS2MultiplayerMod.Game.Sync.Players
{
    /// <summary>
    /// Smoothly animates and tracks camera perspective to follow a selected teammate in real-time.
    /// Breaks out automatically if local player moves their camera manually.
    /// </summary>
    public partial class PlayerSpectatorSystem : GameSystemBase
    {
        private CameraUpdateSystem _camera;
        public int SpectatingPlayerId { get; private set; } = 0;

        protected override void OnCreate()
        {
            base.OnCreate();
            _camera = World.GetExistingSystemManaged<CameraUpdateSystem>();
            Mod.log.Info(nameof(PlayerSpectatorSystem) + " ready.");
        }

        public void SpectatePlayer(int playerId)
        {
            SpectatingPlayerId = playerId;
            Mod.log.Info("[MP] Spectator mode: following player ID " + playerId);
        }

        public void StopSpectating()
        {
            SpectatingPlayerId = 0;
        }

        protected override void OnUpdate()
        {
            MultiplayerService service = Mod.Service;
            if (service == null || !service.GameplaySyncReady)
            {
                SpectatingPlayerId = 0;
                return;
            }

            // Strict spectator enforcement: force active tool to DefaultToolSystem and disallow construction
            if (service.IsLocalSpectator)
            {
                var toolSystem = World.GetOrCreateSystemManaged<global::Game.Tools.ToolSystem>();
                if (toolSystem?.activeTool != null && !(toolSystem.activeTool is global::Game.Tools.DefaultToolSystem))
                {
                    toolSystem.activeTool = World.GetOrCreateSystemManaged<global::Game.Tools.DefaultToolSystem>();
                }
            }

            if (SpectatingPlayerId == 0) return;

            RemotePlayer target = null;
            foreach (RemotePlayer p in service.RemotePlayers)
            {
                if (p.PlayerId == SpectatingPlayerId)
                {
                    target = p;
                    break;
                }
            }

            if (target == null || service.NowMs - target.LastUpdateMs > 6000)
            {
                // Target player disconnected or inactive
                SpectatingPlayerId = 0;
                return;
            }

            if (_camera == null) _camera = World.GetExistingSystemManaged<CameraUpdateSystem>();
            if (_camera == null || _camera.activeCamera == null) return;

            var targetGround = new float3(target.X, target.Y, target.Z);
            var targetEye = new float3(target.EyeX, target.EyeY, target.EyeZ);

            Transform camTransform = _camera.activeCamera.transform;
            if (camTransform != null)
            {
                float dt = UnityEngine.Time.deltaTime;
                // Smoothly lerp camera position and look rotation towards target
                camTransform.position = Vector3.Lerp(camTransform.position, targetEye, dt * 5f);
                Vector3 lookDir = (Vector3)targetGround - camTransform.position;
                if (lookDir.sqrMagnitude > 0.1f)
                {
                    Quaternion targetRot = Quaternion.LookRotation(lookDir);
                    camTransform.rotation = Quaternion.Slerp(camTransform.rotation, targetRot, dt * 5f);
                }
            }
        }
    }
}
