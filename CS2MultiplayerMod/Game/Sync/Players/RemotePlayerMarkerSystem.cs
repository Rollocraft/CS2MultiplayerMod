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

        /// <summary>
        /// Beam length cap. The overlay quad is camera-facing, so its screen area grows with the
        /// partner's altitude even though the line itself stays 3 m wide - and a zoomed-out camera
        /// sits kilometres up. The first stretch above the ground already reads as height.
        /// </summary>
        private const float MaxBeamLength = 400f;

        /// <summary>
        /// Keep-out radius around the local camera. The beam's far end is the partner's eye, which
        /// in a shared view lands on top of the local camera; a camera-facing quad ending there is
        /// clipped against the near plane and covers most of the screen in translucent fill.
        /// </summary>
        private const float BeamCameraClearance = 80f;

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
        private CameraUpdateSystem _camera;
        private readonly Plane[] _frustum = new Plane[6];

        protected override void OnCreate()
        {
            base.OnCreate();
            _overlay = World.GetOrCreateSystemManaged<OverlayRenderSystem>();
            _camera = World.GetExistingSystemManaged<CameraUpdateSystem>();
            Mod.log.Info(nameof(RemotePlayerMarkerSystem) + " ready.");
        }

        protected override void OnUpdate()
        {
            MultiplayerService service = Mod.Service;
            if (service == null || _overlay == null || !service.GameplaySyncReady) return;
            if (Mod.Setting != null && !Mod.Setting.ShowPartnerMarkers) return;

            long now = service.NowMs;

            // Don't touch the overlay buffer at all unless there's a fresh position to draw.
            bool anyFresh = false;
            foreach (RemotePlayer p in service.RemotePlayers)
                if (now - p.LastUpdateMs <= StaleAfterMs) { anyFresh = true; break; }
            if (!anyFresh) return;

            if (_camera == null) _camera = World.GetExistingSystemManaged<CameraUpdateSystem>();
            Camera view = _camera != null ? _camera.activeCamera : null;
            bool culling = view != null;
            if (culling) GeometryUtility.CalculateFrustumPlanes(view, _frustum);
            float3 localEye = _camera != null ? _camera.position : default(float3);

            // Writing the buffer forces the game's overlay pass on for the frame and completes its
            // writers on this thread, so decide there is something visible to draw before taking it.
            bool haveBuffer = false;
            OverlayRenderSystem.Buffer buffer = default(OverlayRenderSystem.Buffer);

            foreach (RemotePlayer p in service.RemotePlayers)
            {
                if (now - p.LastUpdateMs > StaleAfterMs) continue;

                var focus = new float3(p.X, p.Y, p.Z);
                var eye = new float3(p.EyeX, p.EyeY, p.EyeZ);

                bool ringVisible = !culling || SphereVisible(focus, RingDiameter);
                Line3.Segment beam;
                bool beamVisible = TryBuildBeam(focus, eye, localEye, out beam) &&
                                   (!culling || SegmentVisible(beam));
                if (!ringVisible && !beamVisible) continue;

                if (!haveBuffer)
                {
                    // Taking the buffer turns the game's overlay pass on for this frame and blocks
                    // here until everything the overlay system depends on has finished. Both halves
                    // are paid per frame and both get more expensive the more the frame is already
                    // doing, so this is measured separately from the cheap culling above.
                    using (Diagnostics.SyncProfiler.Measure("PartnerMarkers.Overlay"))
                    {
                        JobHandle dependencies;
                        buffer = _overlay.GetBuffer(out dependencies);
                        dependencies.Complete();
                    }
                    haveBuffer = true;
                }

                Color color = Palette[((p.PlayerId % Palette.Length) + Palette.Length) % Palette.Length];
                Color fill = new Color(color.r, color.g, color.b, 0.12f);
                color.a = 0.9f;

                // Ground ring where the partner is looking.
                if (ringVisible)
                    buffer.DrawCircle(color, fill, RingOutlineWidth, default,
                        new float2(0f, 1f), focus, RingDiameter);

                // A line from that point up towards their camera, so you can see how high they
                // are "flying" (and roughly where they are when zoomed out).
                if (beamVisible) buffer.DrawLine(color, beam, BeamWidth, true);
            }
        }

        /// <summary>
        /// The drawable part of the focus-to-eye line: capped in length and cut short of the local
        /// camera's keep-out sphere. False when nothing worth drawing is left.
        /// </summary>
        private static bool TryBuildBeam(float3 focus, float3 eye, float3 localEye,
            out Line3.Segment beam)
        {
            beam = default(Line3.Segment);
            float3 delta = eye - focus;
            float length = math.length(delta);
            if (length <= 1f) return false;

            float3 direction = delta / length;
            length = math.min(length, MaxBeamLength);

            // Both ends inside the keep-out sphere: the partner is where the local camera is, so
            // the beam has nothing to say and everything to fill.
            float3 fromCamera = focus - localEye;
            float distanceToStart = math.length(fromCamera);
            if (distanceToStart < BeamCameraClearance) return false;

            // Otherwise clip at the sphere's first intersection along the line.
            float b = 2f * math.dot(fromCamera, direction);
            float c = distanceToStart * distanceToStart - BeamCameraClearance * BeamCameraClearance;
            float discriminant = b * b - 4f * c;
            if (discriminant > 0f)
            {
                float entry = 0.5f * (-b - math.sqrt(discriminant));
                if (entry > 0f) length = math.min(length, entry);
            }
            if (length <= 1f) return false;

            beam = new Line3.Segment(focus, focus + direction * length);
            return true;
        }

        private bool SphereVisible(float3 center, float radius)
        {
            var point = new Vector3(center.x, center.y, center.z);
            for (int i = 0; i < _frustum.Length; i++)
                if (_frustum[i].GetDistanceToPoint(point) < -radius) return false;
            return true;
        }

        private bool SegmentVisible(Line3.Segment segment)
        {
            float3 center = (segment.a + segment.b) * 0.5f;
            float radius = math.length(segment.b - segment.a) * 0.5f + BeamWidth;
            return SphereVisible(center, radius);
        }
    }
}
