using System;

namespace CS2MultiplayerMod.Game.Sync.Infrastructure
{
    /// <summary>
    /// Selects the safe correction for a course endpoint whose height is relative to a sampled
    /// surface. Fixed-height endpoints are deliberately excluded: their position is authoritative
    /// and their elevation still selects how the span between the endpoints is graded.
    /// </summary>
    internal static class NetEndpointElevationPolicy
    {
        // Endpoint elevations below this value select the terrain-only profile. Values at or above
        // it select the terrain/water profile, so a correction must never jump across the boundary.
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
