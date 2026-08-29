using System;
using Colossal.Mathematics;
using Game;
using Game.Rendering;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace CS2MultiplayerMod.Game.Sync.Players
{
    /// <summary>
    /// Renders remote player 3D presence in the world:
    /// - Ground look-at target ring
    /// - Vertical altitude drop line / laser connecting ground focus to camera eye
    /// - Orientation heading cone matching player camera yaw
    /// </summary>
    public partial class PlayerCursorRenderSystem : GameSystemBase
    {
        private static readonly Color[] Palette =
        {
            new Color(0.36f, 0.78f, 1.00f, 0.85f), // blue
            new Color(1.00f, 0.69f, 0.26f, 0.85f), // orange
            new Color(0.56f, 0.88f, 0.55f, 0.85f), // green
            new Color(1.00f, 0.45f, 0.45f, 0.85f), // red
            new Color(0.80f, 0.60f, 1.00f, 0.85f), // purple
            new Color(1.00f, 0.85f, 0.40f, 0.85f), // yellow
        };

        private readonly System.Collections.Generic.Dictionary<int, (float3 ground, float3 eye)> _interpolatedPos =
            new System.Collections.Generic.Dictionary<int, (float3, float3)>();

        private OverlayRenderSystem _overlay;
        private CameraUpdateSystem _cameraUpdateSystem;

        protected override void OnCreate()
        {
            base.OnCreate();
            _overlay = World.GetOrCreateSystemManaged<OverlayRenderSystem>();
            _cameraUpdateSystem = World.GetOrCreateSystemManaged<CameraUpdateSystem>();
            Mod.log.Info(nameof(PlayerCursorRenderSystem) + " ready.");
        }

        protected override void OnUpdate()
        {
            MultiplayerService service = Mod.Service;
            if (service == null || _overlay == null || !service.GameplaySyncReady) return;

            long now = service.NowMs;
            var remotePlayers = service.RemotePlayers;
            if (remotePlayers == null) return;

            bool anyActive = false;
            foreach (RemotePlayer p in remotePlayers)
            {
                if (now - p.LastUpdateMs <= 6000)
                {
                    anyActive = true;
                    break;
                }
            }
            if (!anyActive) return;

            OverlayRenderSystem.Buffer buffer = _overlay.GetBuffer(out JobHandle dependencies);
            dependencies.Complete();

            float3 camPos = _cameraUpdateSystem != null ? _cameraUpdateSystem.position : float3.zero;
            float dt = UnityEngine.Time.unscaledDeltaTime;
            float lerpFactor = math.clamp(dt * 15f, 0.05f, 1.0f);

            foreach (RemotePlayer player in remotePlayers)
            {
                // Only render active players (updated within 6 seconds)
                if (now - player.LastUpdateMs > 6000) continue;

                var targetGround = new float3(player.X, player.Y, player.Z);
                var targetEye = new float3(player.EyeX, player.EyeY, player.EyeZ);

                float3 currentGround = targetGround;
                float3 currentEye = targetEye;

                if (_interpolatedPos.TryGetValue(player.PlayerId, out var current))
                {
                    currentGround = math.lerp(current.ground, targetGround, lerpFactor);
                    currentEye = math.lerp(current.eye, targetEye, lerpFactor);
                }
                _interpolatedPos[player.PlayerId] = (currentGround, currentEye);

                // Distance culling: Skip rendering visual overlays for distant players far outside camera range
                if (_cameraUpdateSystem != null && !Infrastructure.SpatialGridCulling.IsWithinCullingDistance(camPos, currentGround))
                    continue;

                int colorIdx = Math.Abs(player.PlayerId) % Palette.Length;
                Color baseColor = Palette[colorIdx];

                // 1. Ground Look-At Ring
                buffer.DrawCircle(baseColor, Color.clear, 1.5f, default, new float2(0f, 1f), currentGround, 20f);

                // 2. Vertical Altitude Laser Drop Line (if camera is elevated)
                float altDiff = currentEye.y - currentGround.y;
                if (altDiff > 5f)
                {
                    var beamColor = new Color(baseColor.r, baseColor.g, baseColor.b, 0.35f);
                    buffer.DrawLine(beamColor, new Line3.Segment(currentGround, currentEye), 1.2f, true);
                }

                // 3. Eye Level Marker Ring
                if (altDiff > 5f)
                {
                    var eyeColor = new Color(baseColor.r, baseColor.g, baseColor.b, 0.6f);
                    buffer.DrawCircle(eyeColor, Color.clear, 1.2f, default, new float2(0f, 1f), currentEye, 12f);
                }
            }

            _overlay.AddBufferWriter(default);
        }
    }
}
