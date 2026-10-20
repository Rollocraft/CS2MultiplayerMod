using System;

namespace CS2MultiplayerMod.Game.Sync.Infrastructure
{
    /// <summary>
    /// Pins a committed edge's straight deck. Only endpoints travel; the receiver regenerates the deck
    /// from its own terrain and water. Elevations at the prefab limit collapse the clamp band onto the
    /// endpoint heights; other decks are left to the generator.
    /// </summary>
    internal static class NetWaterProfilePin
    {
        /// <summary>Water shallower than this reads as terrain - the generator's own shore band.</summary>
        public const float ShoreDepth = 0.2f;

        /// <summary>Deck deviation (m) a water span may absorb and still count as one line.</summary>
        public const float ChordTolerance = 1f;

        /// <summary>Dry grades must agree closely with the line that a pin will commit.</summary>
        public const float DryChordTolerance = 0.1f;

        /// <summary>Metres between profile probes; the generator samples at the same spacing.</summary>
        public const float ProbeSpacing = 4f;

        /// <summary>Initial allocation for profile probes.</summary>
        public const int ProbeCount = 65;

        /// <summary>Reject exceptionally long spans instead of skipping interior samples.</summary>
        public const int MaxProbeCount = 4097;

        /// <summary>Ceiling on the piece count <see cref="Simplify"/> reports for a bent deck.</summary>
        public const int MaxPieces = 8;

        /// <summary>Probe count for a span of <paramref name="lengthXZ"/> metres, or zero if too long.</summary>
        public static int ProbesFor(float lengthXZ)
        {
            if (!(lengthXZ > 0f)) return 2;
            double segments = Math.Ceiling((double)lengthXZ / ProbeSpacing);
            if (segments >= MaxProbeCount) return 0;
            return Math.Max(2, 1 + (int)segments);
        }

        public static bool CrossesWater(float[] depth, int count)
        {
            for (int i = 0; i < count; i++)
                if (depth[i] >= ShoreDepth) return true;
            return false;
        }

        /// <summary>A floor alone can still be pushed up by the surface; only floor and ceiling pin.</summary>
        public static bool NeedsPin(float startElevation, float endElevation, float elevationLimit,
            bool requireElevated)
        {
            if (!(elevationLimit > 0f) || float.IsInfinity(elevationLimit)) return false;
            // Only two underground endpoints guarantee a terrain-only profile.
            if (Math.Max(startElevation, endElevation) < -1f) return false;
            bool floor = requireElevated || Math.Max(startElevation, endElevation) >= elevationLimit;
            bool ceiling = Math.Min(startElevation, endElevation) <= -elevationLimit;
            return !(floor && ceiling);
        }

        /// <summary>
        /// Fewest straight pieces reproducing <paramref name="deck"/> within tolerance; <paramref name="breaks"/>
        /// gets pieces + 1 indices from 0 to count - 1. One piece is pinnable.
        /// </summary>
        public static int Simplify(float[] deck, float[] distance, int count, float tolerance,
            int maxPieces, int[] breaks)
        {
            breaks[0] = 0;
            if (count <= 2 || maxPieces <= 1)
            {
                breaks[1] = count > 1 ? count - 1 : 0;
                return 1;
            }

            int pieces = 0;
            int from = 0;
            while (from < count - 1 && pieces < maxPieces)
            {
                int to = count - 1;
                if (pieces < maxPieces - 1)
                    while (to > from + 1 && MaxDeviation(deck, distance, from, to) > tolerance) to--;
                breaks[++pieces] = to;
                from = to;
            }
            breaks[pieces] = count - 1;
            return pieces;
        }

        private static float MaxDeviation(float[] deck, float[] distance, int from, int to)
        {
            float spanned = 0f;
            for (int i = from + 1; i <= to; i++) spanned += distance[i];
            if (!(spanned > 0f)) return 0f;

            float worst = 0f;
            float walked = 0f;
            for (int i = from + 1; i < to; i++)
            {
                walked += distance[i];
                float chord = deck[from] + (deck[to] - deck[from]) * (walked / spanned);
                float error = Math.Abs(deck[i] - chord);
                if (error > worst) worst = error;
            }
            return worst;
        }

        /// <summary>Both endpoints must match the source; the tool's elevation range does not limit this.</summary>
        public static bool IsEligible(bool startPinnable, bool endPinnable, float elevationLimit)
        {
            return startPinnable && endPinnable &&
                elevationLimit > 0f && !float.IsInfinity(elevationLimit);
        }

        /// <summary>With <see cref="PinnedEndElevation"/>: +limit sets the floor, -limit the ceiling.</summary>
        public static float PinnedStartElevation(float elevationLimit) => elevationLimit;

        public static float PinnedEndElevation(float elevationLimit) => -elevationLimit;
    }
}
