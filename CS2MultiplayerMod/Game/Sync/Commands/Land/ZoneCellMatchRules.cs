using System;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>World position identifies zoning cells; local block ownership is only a tie-break.</summary>
    public static class ZoneCellMatchRules
    {
        public const float MaxDistanceSquared = 40f;
        public const float LookupPadding = 6.325f;
        private const float HeightPenaltyScale = 0.25f;
        private const float MaxHeightPenalty = 256f;

        /// <summary>Height separates overlapping grids but is not identity: peers settle grids at different heights.</summary>
        public static float HeightPenalty(float difference)
        {
            if (float.IsNaN(difference) || float.IsInfinity(difference)) return float.MaxValue;
            return Math.Min(difference * difference * HeightPenaltyScale, MaxHeightPenalty);
        }

        public static bool TryScore(float distanceSquared, float signedAlignment, float stripOffset,
            byte sourceState, byte localState, bool sameSize, out float score)
        {
            score = float.MaxValue;
            if (float.IsNaN(distanceSquared) || distanceSquared < 0 ||
                distanceSquared > MaxDistanceSquared) return false;
            if ((localState & ZonePaintCommand.StateVisible) == 0) return false;

            // A perpendicular block can own the visible cell at an intersection; prefer the exact position.
            score = distanceSquared * 1024f + (1f - Math.Abs(signedAlignment)) * 32f +
                    Math.Min(stripOffset * stripOffset * 0.25f, 64f);
            if (signedAlignment < 0f) score += 32f;
            if (((sourceState ^ localState) & ZonePaintCommand.StateRoadside) != 0) score += 16f;
            if (((sourceState & ZonePaintCommand.StateRoadMask) != 0) !=
                ((localState & ZonePaintCommand.StateRoadMask) != 0)) score += 64f;
            if (((sourceState ^ localState) & ZonePaintCommand.StateShared) != 0) score += 4f;
            if (((sourceState ^ localState) & ZonePaintCommand.StateOccupied) != 0) score += 2f;
            if (sameSize) score -= 0.25f;
            return true;
        }

        public static bool TryScore(float distanceSquared, float heightDifference,
            float signedAlignment, float stripOffset, byte sourceState, byte localState,
            bool sameSize, out float score)
        {
            if (!TryScore(distanceSquared, signedAlignment, stripOffset, sourceState, localState,
                    sameSize, out score)) return false;
            float heightPenalty = HeightPenalty(heightDifference);
            if (heightPenalty == float.MaxValue) return false;
            score += heightPenalty;
            return true;
        }
    }
}
