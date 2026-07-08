using Colossal.Mathematics;
using Game;
using Game.Rendering;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace CS2MultiplayerMod.Game.Sync.Players
{
    /// <summary>
    /// Draws a coloured ground ring at every other player's camera focus - the point on
    /// the map they are looking at - so partners can see where each other is working.
    /// The positions themselves are published by <see cref="PlayerCursorSyncSystem"/>
    /// (the gameplay camera pivot) and kept fresh in
    /// <see cref="MultiplayerService.RemotePlayers"/>; this system only renders them.
    /// </summary>
    public partial class RemotePlayerMarkerSystem : GameSystemBase
    {
        /// <summary>A position older than this (no fresh update) stops being drawn.</summary>
        private const long StaleAfterMs = 5000;

        /// <summary>Ring size on the ground, in metres.</summary>
        private const float RingDiameter = 30f;
        private const float RingOutlineWidth = 4f;
        /// <summary>Width of the line drawn from the ground focus up to the camera.</summary>
        private const float BeamWidth = 3f;

        // Distinct, readable colours cycled by player id so each partner is recognisable.
        private static readonly Color[] Palette =
        {
            new Color(0.36f, 0.78f, 1.00f), // blue
            new Color(1.00f, 0.69f, 0.26f), // orange
            new Color(0.56f, 0.88f, 0.55f), // green
            new Color(1.00f, 0.45f, 0.45f), // red
            new Color(0.80f, 0.60f, 1.00f), // purple
            new Color(1.00f, 0.85f, 0.40f), // yellow
        };

        private OverlayRenderSystem _overlay;

        protected override void OnCreate()
        {
            base.OnCreate();
            _overlay = World.GetOrCreateSystemManaged<OverlayRenderSystem>();
            Mod.log.Info(nameof(RemotePlayerMarkerSystem) + " ready.");
        }

        protected override void OnUpdate()
        {
            MultiplayerService service = Mod.Service;
            if (service == null || _overlay == null || !service.GameplaySyncReady) return;

            long now = service.NowMs;

            // Don't touch the overlay buffer at all unless there's a fresh position to draw.
            bool anyFresh = false;
            foreach (RemotePlayer p in service.RemotePlayers)
                if (now - p.LastUpdateMs <= StaleAfterMs) { anyFresh = true; break; }
            if (!anyFresh) return;

            OverlayRenderSystem.Buffer buffer = _overlay.GetBuffer(out JobHandle dependencies);
            dependencies.Complete();

            foreach (RemotePlayer p in service.RemotePlayers)
            {
                if (now - p.LastUpdateMs > StaleAfterMs) continue;

                Color color = Palette[((p.PlayerId % Palette.Length) + Palette.Length) % Palette.Length];
                Color fill = new Color(color.r, color.g, color.b, 0.12f);
                color.a = 0.9f;

                var focus = new float3(p.X, p.Y, p.Z);
                var eye = new float3(p.EyeX, p.EyeY, p.EyeZ);

                // Ground ring where the partner is looking.
                buffer.DrawCircle(color, fill, RingOutlineWidth, default,
                    new float2(0f, 1f), focus, RingDiameter);

                // A line from that point up to their camera, so you can see how high they
                // are "flying" (and roughly where they are when zoomed out).
                if (math.distancesq(focus, eye) > 1f)
                    buffer.DrawLine(color, new Line3.Segment(focus, eye), BeamWidth, true);
            }
        }
    }
}
