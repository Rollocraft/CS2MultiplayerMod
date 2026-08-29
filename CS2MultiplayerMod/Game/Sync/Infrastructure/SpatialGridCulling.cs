using System;
using Unity.Mathematics;

namespace CS2MultiplayerMod.Game.Sync.Infrastructure
{
    /// <summary>
    /// Lightweight 2D spatial hash grid utility for culling distant remote player
    /// visual lasers, blueprint holograms, and sound calculations.
    /// </summary>
    public static class SpatialGridCulling
    {
        public const float DefaultCellSize = 512f;
        public const float MaxVisibleDistanceMeters = 2500f;

        public static int2 GetCell(float3 worldPos, float cellSize = DefaultCellSize)
        {
            return new int2((int)math.floor(worldPos.x / cellSize), (int)math.floor(worldPos.z / cellSize));
        }

        public static bool IsWithinCullingDistance(float3 observerPos, float3 targetPos, float maxDistance = MaxVisibleDistanceMeters)
        {
            float dx = targetPos.x - observerPos.x;
            float dz = targetPos.z - observerPos.z;
            return (dx * dx + dz * dz) <= (maxDistance * maxDistance);
        }
    }
}
