using System;
using Unity.Mathematics;

namespace CS2MultiplayerMod.Core.Protocol
{
    /// <summary>
    /// Compaction utility for road bezier curves that strips redundant collinear control
    /// points from straight highway segments before network serialization.
    /// </summary>
    public static class CurveCompactor
    {
        public const float CollinearEpsilon = 0.01f;

        public static bool IsStraightSegment(float3 p0, float3 p1, float3 p2, float3 p3)
        {
            float3 lineDir = p3 - p0;
            float lineLenSq = math.lengthsq(lineDir);
            if (lineLenSq < 0.001f) return true;

            float3 d1 = math.cross(p1 - p0, lineDir);
            float3 d2 = math.cross(p2 - p0, lineDir);

            return (math.lengthsq(d1) / lineLenSq < CollinearEpsilon) &&
                   (math.lengthsq(d2) / lineLenSq < CollinearEpsilon);
        }
    }
}
