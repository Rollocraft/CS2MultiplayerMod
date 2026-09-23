using System.Collections.Generic;
using Colossal.Mathematics;
using Game;
using Game.Rendering;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace CS2MultiplayerMod.Game.Sync.Players
{
    /// <summary>
    /// Draws a ground ring at every other player's camera focus, interpolated between received
    /// positions with a short motion smear.
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

        /// <summary>Beam length cap: the camera-facing quad grows with altitude.</summary>
        private const float MaxBeamLength = 400f;

        /// <summary>Keep-out radius around the local camera, or a quad ending there fills the screen.</summary>
        private const float BeamCameraClearance = 80f;

        /// <summary>
        /// Render delay behind the newest position: drawn between the two bracketing positions, since
        /// they arrive about ten times a second and unevenly.
        /// </summary>
        private const long PlayoutDelayMs = 140;

        /// <summary>Rounds off the corner where one pair of positions hands over to the next.</summary>
        private const float SmoothingSeconds = 0.035f;

        /// <summary>Positions held per partner while they wait out the playout delay.</summary>
        private const int SampleCapacity = 8;

        /// <summary>Lag between smear copies, short enough that they overlap into one smear.</summary>
        private const float SmearLagSeconds = 0.05f;

        /// <summary>Strength of each smear copy, nearest first; one overlay entry each.</summary>
        private static readonly float[] SmearStrength = { 0.45f, 0.28f, 0.16f };

        /// <summary>Distance at which the whole smear reaches full strength, so a still marker shows none.</summary>
        private const float SmearFullLagDistance = 12f;

        /// <summary>Weaker than this is invisible anyway, so it does not get an overlay entry.</summary>
        private const float SmearMinStrength = 0.02f;

        /// <summary>A larger step is a jump (minimap, notification), not a pan.</summary>
        private const float SnapDistance = 350f;

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

        /// <summary>A received position, stamped with the moment it arrived.</summary>
        private struct Sample
        {
            public float3 Focus;
            public float3 Eye;
            public long AtMs;
        }

        /// <summary>Where a partner's marker is drawn, as opposed to where they last reported being.</summary>
        private sealed class Trail
        {
            public float3 Focus;
            public float3 Eye;
            /// <summary>The smear, nearest first; each copy chases the one ahead of it.</summary>
            public readonly float3[] Smear = new float3[SmearStrength.Length];
            public long TouchedMs;
            /// <summary>Received positions, oldest first, waiting out the playout delay.</summary>
            public readonly Sample[] Samples = new Sample[SampleCapacity];
            public int SampleCount;
            public long LastSampleMs;
            public readonly Core.Protocol.Messages.PlayerHoverShape[] Hover =
                new Core.Protocol.Messages.PlayerHoverShape[Core.Protocol.Messages.PlayerHoverShape.MaxShapes];
            public int HoverCount;
        }

        private readonly Dictionary<int, Trail> _trails = new Dictionary<int, Trail>();
        private readonly List<int> _departed = new List<int>();
        private long _lastFrameMs;
        private long _hoverResetMs;
        /// <summary>This frame's own camera, which sets how thick an outline has to be drawn.</summary>
        private float3 _localEye;

        protected override void OnCreate()
        {
            base.OnCreate();
            _overlay = World.GetOrCreateSystemManaged<OverlayRenderSystem>();
            _camera = World.GetExistingSystemManaged<CameraUpdateSystem>();
            _hoverTerrain = World.GetOrCreateSystemManaged<global::Game.Simulation.TerrainSystem>();
        }

        protected override void OnUpdate()
        {
            MultiplayerService service = Mod.Service;
            if (service == null || _overlay == null || !service.GameplaySyncReady)
            {
                _trails.Clear();
                _hoverResetMs = service != null ? service.NowMs : 0;
                return;
            }
            if (Mod.Setting != null && !Mod.Setting.ShowPartnerMarkers)
            {
                if (_trails.Count != 0) _trails.Clear();
                return;
            }

            long now = service.NowMs;
            float frameSeconds = (now - _lastFrameMs) * 0.001f;
            _lastFrameMs = now;

            // Don't touch the overlay buffer at all unless there's a fresh position to draw.
            int fresh = 0;
            foreach (RemotePlayer p in service.RemotePlayers)
                if (now - p.LastUpdateMs <= StaleAfterMs) fresh++;
            if (fresh == 0)
            {
                if (_trails.Count != 0) _trails.Clear();
                return;
            }

            if (_camera == null) _camera = World.GetExistingSystemManaged<CameraUpdateSystem>();
            Camera view = _camera != null ? _camera.activeCamera : null;
            bool culling = view != null;
            if (culling) GeometryUtility.CalculateFrustumPlanes(view, _frustum);
            float3 localEye = _camera != null ? _camera.position : default(float3);
            _localEye = localEye;
            _hoverHeights = _hoverTerrain.GetHeightData();

            // Taking the buffer forces the overlay pass on; only do it when something is visible.
            bool haveBuffer = false;
            OverlayRenderSystem.Buffer buffer = default(OverlayRenderSystem.Buffer);

            foreach (RemotePlayer p in service.RemotePlayers)
            {
                if (now - p.LastUpdateMs > StaleAfterMs)
                {
                    _trails.Remove(p.PlayerId);
                    continue;
                }

                // Eased before culling so it keeps moving off screen.
                Trail trail = Advance(p, frameSeconds, now);
                if (p.LastUpdateMs > _hoverResetMs) AdvanceHover(trail, p, frameSeconds, now);
                else trail.HoverCount = 0;
                float3 focus = trail.Focus;
                float smearLength = math.distance(trail.Smear[trail.Smear.Length - 1], focus);

                bool ringVisible = !culling || SphereVisible(focus, RingDiameter + smearLength);
                bool beamVisible = TryBuildBeam(focus, trail.Eye, localEye, out Line3.Segment beam) &&
                                   (!culling || SegmentVisible(beam));
                bool hoverVisible = HoverVisible(trail, culling);
                if (!ringVisible && !beamVisible && !hoverVisible) continue;

                if (!haveBuffer)
                {
                    // Measured separately: taking the buffer blocks on the overlay's dependencies.
                    using (Diagnostics.SyncProfiler.Measure("PartnerMarkers.Overlay"))
                    {
                        buffer = _overlay.GetBuffer(out JobHandle dependencies);
                        dependencies.Complete();
                    }
                    haveBuffer = true;
                }

                Color color = Palette[((p.PlayerId % Palette.Length) + Palette.Length) % Palette.Length];
                Color fill = new Color(color.r, color.g, color.b, 0.12f);
                color.a = 0.9f;

                // Smear first, so the ring stays on top.
                if (ringVisible)
                {
                    float moving = math.saturate(smearLength / SmearFullLagDistance);
                    // Outline only; the fill keeps the colour so it tints.
                    var noFill = new Color(color.r, color.g, color.b, 0f);
                    for (int i = 0; i < trail.Smear.Length; i++)
                    {
                        float strength = moving * SmearStrength[i];
                        if (strength < SmearMinStrength) continue;

                        var echo = new Color(color.r, color.g, color.b, color.a * strength);
                        buffer.DrawCircle(echo, noFill, RingOutlineWidth, default,
                            new float2(0f, 1f), trail.Smear[i], RingDiameter);
                    }

                    buffer.DrawCircle(color, fill, RingOutlineWidth, default,
                        new float2(0f, 1f), focus, RingDiameter);
                }

                // A line up towards their camera shows height.
                if (beamVisible) buffer.DrawLine(color, beam, BeamWidth, true);
                if (hoverVisible) DrawHover(buffer, trail, color, culling);
            }

            // Departed players no longer appear in the loop above.
            if (_trails.Count > fresh) DropDepartedTrails(now);
        }

        /// <summary>
        /// Moves the marker to where the partner was <see cref="PlayoutDelayMs"/> ago; the easing is frame-rate
        /// independent.
        /// </summary>
        private Trail Advance(RemotePlayer player, float frameSeconds, long now)
        {
            if (!_trails.TryGetValue(player.PlayerId, out Trail trail))
            {
                trail = new Trail();
                _trails.Add(player.PlayerId, trail);
            }

            if (player.LastUpdateMs != trail.LastSampleMs)
            {
                trail.LastSampleMs = player.LastUpdateMs;
                var focus = new float3(player.X, player.Y, player.Z);
                var eye = new float3(player.EyeX, player.EyeY, player.EyeZ);
                if (Push(trail, focus, eye, player.LastUpdateMs)) Land(trail, focus, eye);
            }

            if (Playout(trail, now - PlayoutDelayMs, out float3 playoutFocus, out float3 playoutEye))
            {
                float toMarker = 1f - math.exp(-frameSeconds / SmoothingSeconds);
                trail.Focus += (playoutFocus - trail.Focus) * toMarker;
                trail.Eye += (playoutEye - trail.Eye) * toMarker;
            }

            float toSmear = 1f - math.exp(-frameSeconds / SmearLagSeconds);
            float3 ahead = trail.Focus;
            for (int i = 0; i < trail.Smear.Length; i++)
            {
                trail.Smear[i] += (ahead - trail.Smear[i]) * toSmear;
                ahead = trail.Smear[i];
            }

            trail.TouchedMs = now;
            return trail;
        }

        /// <summary>Files a position; true when the track restarts (first position, or a jump).</summary>
        private static bool Push(Trail trail, float3 focus, float3 eye, long atMs)
        {
            bool restart = trail.SampleCount == 0 ||
                math.distancesq(trail.Samples[trail.SampleCount - 1].Focus, focus) >
                    SnapDistance * SnapDistance;
            if (restart) trail.SampleCount = 0;
            else if (trail.SampleCount == trail.Samples.Length)
            {
                // Nothing is draining the buffer; keep the newest positions.
                for (int i = 1; i < trail.SampleCount; i++) trail.Samples[i - 1] = trail.Samples[i];
                trail.SampleCount--;
            }

            trail.Samples[trail.SampleCount++] = new Sample { Focus = focus, Eye = eye, AtMs = atMs };
            return restart;
        }

        /// <summary>
        /// The position at <paramref name="renderMs"/>; holds the last one rather than extrapolate past a stop.
        /// </summary>
        private static bool Playout(Trail trail, long renderMs, out float3 focus, out float3 eye)
        {
            focus = default(float3);
            eye = default(float3);
            if (trail.SampleCount == 0) return false;

            // Drop what the playout point has already passed, less the one it is coming from.
            int passed = 0;
            while (passed + 1 < trail.SampleCount &&
                   trail.Samples[passed + 1].AtMs <= renderMs) passed++;
            if (passed != 0)
            {
                for (int i = passed; i < trail.SampleCount; i++)
                    trail.Samples[i - passed] = trail.Samples[i];
                trail.SampleCount -= passed;
            }

            Sample from = trail.Samples[0];
            focus = from.Focus;
            eye = from.Eye;
            if (trail.SampleCount == 1) return true;

            Sample to = trail.Samples[1];
            long span = to.AtMs - from.AtMs;
            if (span <= 0)
            {
                focus = to.Focus;
                eye = to.Eye;
                return true;
            }

            float t = math.saturate((renderMs - from.AtMs) / (float)span);
            focus = math.lerp(from.Focus, to.Focus, t);
            eye = math.lerp(from.Eye, to.Eye, t);
            return true;
        }

        /// <summary>Puts the marker and its whole smear on one position, with nothing in between.</summary>
        private static void Land(Trail trail, float3 focus, float3 eye)
        {
            trail.Focus = focus;
            trail.Eye = eye;
            for (int i = 0; i < trail.Smear.Length; i++) trail.Smear[i] = focus;
        }

        private void DropDepartedTrails(long now)
        {
            foreach (KeyValuePair<int, Trail> entry in _trails)
                if (entry.Value.TouchedMs != now) _departed.Add(entry.Key);

            for (int i = 0; i < _departed.Count; i++) _trails.Remove(_departed[i]);
            _departed.Clear();
        }

        /// <summary>The drawable beam: length-capped and cut short of the camera keep-out.</summary>
        private static bool TryBuildBeam(float3 focus, float3 eye, float3 localEye,
            out Line3.Segment beam)
        {
            beam = default(Line3.Segment);
            float3 delta = eye - focus;
            float length = math.length(delta);
            if (length <= 1f) return false;

            float3 direction = delta / length;
            length = math.min(length, MaxBeamLength);

            // The partner is at the local camera: nothing to draw.
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
