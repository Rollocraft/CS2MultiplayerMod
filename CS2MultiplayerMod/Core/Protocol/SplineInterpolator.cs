using System;
using Unity.Mathematics;

namespace CS2MultiplayerMod.Core.Protocol
{
    /// <summary>
    /// High-order cubic Catmull-Rom and Hermite spline interpolation formulas for
    /// buttery-smooth 144 FPS camera spectating and cursor trajectory smoothing.
    /// </summary>
    public static class SplineInterpolator
    {
        public static float3 CatmullRom(float3 p0, float3 p1, float3 p2, float3 p3, float t)
        {
            t = math.clamp(t, 0f, 1f);
            float t2 = t * t;
            float t3 = t2 * t;

            float3 a = 2f * p1;
            float3 b = p2 - p0;
            float3 c = 2f * p0 - 5f * p1 + 4f * p2 - p3;
            float3 d = -p0 + 3f * p1 - 3f * p2 + p3;

            return 0.5f * (a + (b * t) + (c * t2) + (d * t3));
        }

        public static float3 Hermite(float3 start, float3 startTangent, float3 end, float3 endTangent, float t)
        {
            t = math.clamp(t, 0f, 1f);
            float t2 = t * t;
            float t3 = t2 * t;

            float h00 = 2f * t3 - 3f * t2 + 1f;
            float h10 = t3 - 2f * t2 + t;
            float h01 = -2f * t3 + 3f * t2;
            float h11 = t3 - t2;

            return h00 * start + h10 * startTangent + h01 * end + h11 * endTangent;
        }
    }
}
