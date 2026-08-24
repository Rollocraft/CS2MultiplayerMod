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

        private OverlayRenderSystem _overlay;

        protected override void OnCreate()
        {
            base.OnCreate();
            _overlay = World.GetOrCreateSystemManaged<OverlayRenderSystem>();
            Mod.log.Info(nameof(PlayerCursorRenderSystem) + " ready.");
        }

        protected override void OnUpdate()
        {
            MultiplayerService service = Mod.Service;
            if (service == null || _overlay == null || !service.GameplaySyncReady) return;

            long now = service.NowMs;
            var remotePlayers = service.RemotePlayers;

            OverlayRenderSystem.Buffer buffer = _overlay.GetBuffer(out JobHandle dependencies);
            dependencies.Complete();

            foreach (RemotePlayer player in remotePlayers)
            {
                // Only render active players (updated within 6 seconds)
                if (now - player.LastUpdateMs > 6000) continue;

                int colorIdx = Math.Abs(player.PlayerId) % Palette.Length;
                Color baseColor = Palette[colorIdx];

                var groundPos = new float3(player.X, player.Y, player.Z);
                var eyePos = new float3(player.EyeX, player.EyeY, player.EyeZ);

                // 1. Ground Look-At Ring
                var groundCircle = new Circle2(10f, groundPos.xz);
                var groundBounds = new Bounds1(groundPos.y - 2f, groundPos.y + 2f);
                buffer.DrawCircle(baseColor, Color.clear, 1.5f, 0, groundBounds, groundCircle);

                // 2. Vertical Altitude Laser Drop Line (if camera is elevated)
                float altDiff = eyePos.y - groundPos.y;
                if (altDiff > 5f)
                {
                    var beamBounds = new Bounds1(groundPos.y, eyePos.y);
                    var beamCircle = new Circle2(1.2f, groundPos.xz);
                    var beamColor = new Color(baseColor.r, baseColor.g, baseColor.b, 0.35f);
                    buffer.DrawCircle(beamColor, Color.clear, 0.6f, 0, beamBounds, beamCircle);
                }

                // 3. Eye Level Marker Ring
                if (altDiff > 5f)
                {
                    var eyeCircle = new Circle2(6f, eyePos.xz);
                    var eyeBounds = new Bounds1(eyePos.y - 1f, eyePos.y + 1f);
                    var eyeColor = new Color(baseColor.r, baseColor.g, baseColor.b, 0.6f);
                    buffer.DrawCircle(eyeColor, Color.clear, 1.2f, 0, eyeBounds, eyeCircle);
                }
            }
        }
    }
}
