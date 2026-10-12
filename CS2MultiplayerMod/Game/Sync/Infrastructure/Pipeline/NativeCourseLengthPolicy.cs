using CS2MultiplayerMod.Game.Sync.Commands;

namespace CS2MultiplayerMod.Game.Sync.Infrastructure
{
    internal static class NativeCourseLengthPolicy
    {
        // Generator state, not arc length: a broad bound that rejects only unusable inputs.
        public static bool IsPlausible(float length, float measuredLength, bool point)
        {
            if (float.IsNaN(length) || float.IsInfinity(length) ||
                float.IsNaN(measuredLength) || float.IsInfinity(measuredLength) ||
                measuredLength < 0f || length < 0f) return false;
            if (!point && (length < NetPlacementCommand.MinCourseLength ||
                           measuredLength < NetPlacementCommand.MinCourseLength)) return false;
            return length <= (double)measuredLength * 4d + 100d;
        }
    }
}
