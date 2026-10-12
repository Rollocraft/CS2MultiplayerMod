using Colossal.Mathematics;
using CS2MultiplayerMod.Core.Protocol.Messages;
using Game.Rendering;
using Unity.Mathematics;
using UnityEngine;

namespace CS2MultiplayerMod.Game.Sync.Players
{
    public partial class RemotePlayerMarkerSystem
    {
        private const long HoverStaleAfterMs = 1500;

        /// <summary>Outline width at the closest camera; grows with viewing distance.</summary>
        private const float HoverLineWidth = 2f;
        private const float HoverWidthPerMetre = 0.005f;
        private const float HoverMaxLineWidth = 14f;

        /// <summary>Steps the curve outline is drawn in; a road edge is two lines of these.</summary>
        private const int CurveSteps = 16;

        /// <summary>Eases an outline onto a newly received shape rather than snapping it there.</summary>
        private const float HoverBlendSeconds = 0.07f;

        private static void AdvanceHover(Trail trail, RemotePlayer player, float seconds, long now)
        {
            var target = player.Hover;
            int count = now - player.LastUpdateMs <= HoverStaleAfterMs && target != null
                ? math.min(target.Length, PlayerHoverShape.MaxShapes) : 0;
            float blend = 1f - math.exp(-math.max(0f, seconds) / HoverBlendSeconds);
            for (int i = 0; i < count; i++)
            {
                PlayerHoverShape next = target[i], previous = trail.Hover[i];
                if (i < trail.HoverCount && previous.Placement && next.Placement &&
                    previous.Kind == next.Kind && previous.Key == next.Key &&
                    // A new road segment must not morph out of the previously committed course.
                    (next.Kind != PlayerHoverKind.Curve ||
                     math.distancesq(Vector(previous.A), Vector(next.A)) < 0.0001f) &&
                    math.distancesq(Vector(previous.A), Vector(next.A)) < SnapDistance * SnapDistance)
                {
                    next.A = Blend(previous.A, next.A, blend);
                    next.B = Blend(previous.B, next.B, blend);
                    next.C = Blend(previous.C, next.C, blend);
                    next.D = Blend(previous.D, next.D, blend);
                    next.Width = math.lerp(previous.Width, next.Width, blend);
                    next.Height = math.lerp(previous.Height, next.Height, blend);
                }
                trail.Hover[i] = next;
            }
            trail.HoverCount = count;
        }

        // The game's hover outline is one global colour, so partners are drawn here instead.
        private bool HoverVisible(Trail trail, bool culling)
        {
            for (int i = 0; i < trail.HoverCount; i++)
                if (!culling || ShapeVisible(trail.Hover[i])) return true;
            return false;
        }

        /// <summary>How thick a shape at <paramref name="point"/> has to be drawn to read on screen.</summary>
        private float HoverWidth(float3 point) => math.clamp(
            math.distance(_localEye, point) * HoverWidthPerMetre, HoverLineWidth, HoverMaxLineWidth);

        /// <summary>Screen-readable width, capped to a fraction of the outlined object.</summary>
        private float HoverWidth(float3 point, float extent) => math.max(HoverLineWidth,
            math.min(HoverWidth(point), extent * 0.25f));

        private bool ShapeVisible(PlayerHoverShape shape)
        {
            float3 a = Vector(shape.A);
            if (shape.Kind == PlayerHoverKind.Circle) return SphereVisible(a, shape.Width * 0.5f + HoverLineWidth);
            float3 min = math.min(math.min(a, Vector(shape.B)), math.min(Vector(shape.C), Vector(shape.D)));
            float3 max = math.max(math.max(a, Vector(shape.B)), math.max(Vector(shape.C), Vector(shape.D)));
            if (shape.Kind == PlayerHoverKind.Curve)
            {
                // Include the surface projection in culling, even when the course is deep below it.
                var curve = new Bezier4x3(a, Vector(shape.B), Vector(shape.C), Vector(shape.D));
                for (int step = 0; step <= CurveSteps; step++)
                {
                    float3 surface = SurfacePoint(MathUtils.Position(curve, (float)step / CurveSteps));
                    min = math.min(min, surface);
                    max = math.max(max, surface);
                }
            }
            max.y += shape.Height;
            return SphereVisible((min + max) * 0.5f, math.length(max - min) * 0.5f + shape.Width * 0.5f + HoverLineWidth);
        }

        private void DrawHover(OverlayRenderSystem.Buffer buffer, Trail trail, Color color, bool culling)
        {
            using (Diagnostics.SyncProfiler.Measure("PartnerHover.Draw"))
            {
                for (int i = 0; i < trail.HoverCount; i++)
                {
                    PlayerHoverShape shape = trail.Hover[i];
                    if (culling && !ShapeVisible(shape)) continue;
                    float3 a = Vector(shape.A), b = Vector(shape.B), c = Vector(shape.C), d = Vector(shape.D);
                    float line = HoverWidth(a);
                    switch (shape.Kind)
                    {
                        case PlayerHoverKind.Circle:
                            line = HoverWidth(a, math.max(1f, shape.Width));
                            buffer.DrawCircle(color, new Color(color.r, color.g, color.b, 0f), line,
                                OverlayRenderSystem.StyleFlags.Projected,
                                new float2(0f, 1f), a, math.max(1f, shape.Width));
                            break;
                        case PlayerHoverKind.Box:
                            line = HoverWidth(a, math.min(math.distance(a, b), math.distance(b, c)));
                            DrawQuad(buffer, color, line, a, b, c, d);
                            break;
                        case PlayerHoverKind.Curve:
                            bool narrow = shape.Width <= line * 2f;
                            // Two thin edges show the road width without a large translucent fill.
                            float3 lastLeft = default, lastRight = default, lastPoint = default;
                            for (int step = 0; step <= CurveSteps; step++)
                            {
                                float t = (float)step / CurveSteps, u = 1f - t;
                                float3 point = u * u * u * a + 3f * u * u * t * b +
                                    3f * u * t * t * c + t * t * t * d;
                                float3 tangent = u * u * (b - a) + 2f * u * t * (c - b) + t * t * (d - c);
                                float3 side = math.normalizesafe(new float3(-tangent.z, 0f, tangent.x),
                                    new float3(1f, 0f, 0f)) * (shape.Width * 0.5f);
                                float3 left = point + side, right = point - side;
                                if (!narrow && (step == 0 || step == CurveSteps))
                                    DrawNetworkLine(buffer, color, line, left, right);
                                if (step != 0)
                                {
                                    if (narrow)
                                        DrawNetworkLine(buffer, color, math.max(shape.Width, line), lastPoint, point);
                                    else
                                    {
                                        DrawNetworkLine(buffer, color, line, lastLeft, left);
                                        DrawNetworkLine(buffer, color, line, lastRight, right);
                                    }
                                }
                                lastLeft = left; lastRight = right; lastPoint = point;
                            }
                            break;
                    }
                }
            }
        }

        private global::Game.Simulation.TerrainSystem _hoverTerrain;
        private global::Game.Simulation.TerrainHeightData _hoverHeights;

        private float3 SurfacePoint(float3 point)
        {
            point.y = math.max(point.y,
                global::Game.Simulation.TerrainUtils.SampleHeight(ref _hoverHeights, point));
            return point;
        }

        private void DrawNetworkLine(OverlayRenderSystem.Buffer buffer, Color color, float width,
            float3 a, float3 b)
        {
            if (math.distancesq(a, b) <= 0.0001f) return;
            float3 middle = (a + b) * 0.5f;
            if (SurfacePoint(a).y > a.y + 0.1f || SurfacePoint(b).y > b.y + 0.1f ||
                SurfacePoint(middle).y > middle.y + 0.1f)
            {
                // Projected lines follow terrain between samples; elevated sections keep their height.
                buffer.DrawLine(color, color, 0f, OverlayRenderSystem.StyleFlags.Projected,
                    new Line3.Segment(a, b), width, default(float2));
            }
            else DrawHoverLine(buffer, color, width, a, b);
        }

        private static void DrawQuad(OverlayRenderSystem.Buffer buffer, Color color, float width,
            float3 a, float3 b, float3 c, float3 d)
        {
            DrawHoverLine(buffer, color, width, a, b); DrawHoverLine(buffer, color, width, b, c);
            DrawHoverLine(buffer, color, width, c, d); DrawHoverLine(buffer, color, width, d, a);
        }

        private static void DrawHoverLine(OverlayRenderSystem.Buffer buffer, Color color, float width,
            float3 a, float3 b)
        {
            if (math.distancesq(a, b) > 0.0001f)
                buffer.DrawLine(color, new Line3.Segment(a, b), width, true);
        }

        private static float3 Vector(HoverPoint p) => new float3(p.X, p.Y, p.Z);
        private static HoverPoint Blend(HoverPoint a, HoverPoint b, float blend)
        {
            float3 value = math.lerp(Vector(a), Vector(b), blend);
            return new HoverPoint(value.x, value.y, value.z);
        }
    }
}
