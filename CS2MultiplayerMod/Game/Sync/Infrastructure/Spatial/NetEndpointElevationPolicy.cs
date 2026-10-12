using System;

namespace CS2MultiplayerMod.Game.Sync.Infrastructure
{
    /// <summary>
    /// Corrects surface-relative endpoints only; fixed-height endpoints are authoritative and still
    /// select the span's grading.
    /// </summary>
    internal static class NetEndpointElevationPolicy
    {
        // Below this the terrain-only profile applies; a correction must not cross it.
        private const float TerrainOnlyThreshold = -1f;

        public static float Correction(float sourceElevation, float projectedElevation,
            bool freeHeight, float agreementTolerance)
        {
            if (!freeHeight || float.IsNaN(projectedElevation) ||
                float.IsInfinity(projectedElevation) ||
                Math.Abs(projectedElevation - sourceElevation) <= agreementTolerance)
                return 0f;

            bool sourceUsesTerrainOnly = sourceElevation < TerrainOnlyThreshold;
            bool projectionUsesTerrainOnly = projectedElevation < TerrainOnlyThreshold;
            if (sourceUsesTerrainOnly != projectionUsesTerrainOnly)
                return 0f;

            return projectedElevation - sourceElevation;
        }
    }
}
