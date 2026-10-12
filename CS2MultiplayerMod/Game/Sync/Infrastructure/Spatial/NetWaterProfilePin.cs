using System;

namespace CS2MultiplayerMod.Game.Sync.Infrastructure
{
    /// <summary>
    /// Reproduces a course's vertical profile. Only endpoints travel; the receiver regenerates the deck
    /// from its own terrain and water. Elevations at the prefab limit collapse the clamp band onto the
    /// endpoint heights, pinning a straight deck; other decks are left to the generator.
    /// </summary>
    internal static class NetWaterProfilePin
    {
        /// <summary>Water shallower than this reads as terrain - the generator's own shore band.</summary>
        public const float ShoreDepth = 0.2f;

        /// <summary>Bridge clearance added above a bridged water surface, in elevation limits.</summary>
        public const float ClearanceLimits = 2f;

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

        /// <summary>The generator's own stand-in for "no bound on this side".</summary>
        private const float Unbounded = 1000000f;

        /// <summary>Terrain, or the water surface plus bridge clearance where deep enough.</summary>
        public static float SurfaceHeight(float terrain, float water, float depth, float elevationLimit)
        {
            float bridged = depth < ShoreDepth ? terrain : water + elevationLimit * ClearanceLimits;
            return Math.Max(terrain, bridged);
        }

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

        /// <summary>
        /// The locally generated deck, in the generator's order: elevation band, slope passes both ways,
        /// endpoint heights, slope cones, then straightening of raised runs.
        /// </summary>
        public static void PredictDeck(float[] surface, float[] terrain, float[] distance, int count,
            float startHeight, float endHeight, float startElevation, float endElevation,
            float elevationLimit, float maxSlope, float[] deck, bool requireElevated = false)
        {
            if (count <= 0) return;
            for (int i = 0; i < count; i++) deck[i] = surface[i];
            if (count == 1) { deck[0] = startHeight; return; }

            float slope = maxSlope > 0f ? maxSlope : 0f;

            float spannedTotal = 0f;
            for (int i = 1; i < count; i++) spannedTotal += distance[i];

            ElevationBand(startHeight, endHeight, startElevation, endElevation, elevationLimit,
                spannedTotal * slope * 0.5f,
                out float floorStart, out float floorEnd, out float ceilingStart, out float ceilingEnd);
            if (requireElevated)
            {
                floorStart = startHeight;
                floorEnd = endHeight;
            }

            // Band before slope limit, as the generator does.
            float run = float.NegativeInfinity;
            float walked = 0f;
            for (int i = 0; i < count; i++)
            {
                float low, high;
                if (i == 0) { low = floorStart; high = ceilingStart; }
                else if (i == count - 1) { low = floorEnd; high = ceilingEnd; }
                else
                {
                    walked += distance[i];
                    float t = spannedTotal > 0f ? walked / spannedTotal : 0f;
                    low = floorStart + (floorEnd - floorStart) * t;
                    high = ceilingStart + (ceilingEnd - ceilingStart) * t;
                }
                if (deck[i] < low) deck[i] = low;
                if (deck[i] > high) deck[i] = high;

                run -= distance[i] * slope;
                if (deck[i] < run) deck[i] = run;
                run = deck[i];
            }
            run = float.NegativeInfinity;
            for (int i = count - 1; i >= 0; i--)
            {
                if (deck[i] < run) deck[i] = run;
                run = deck[i] - distance[i] * slope;
            }

            deck[0] = startHeight;
            deck[count - 1] = endHeight;

            // Clamp into both endpoints' slope cones; where they do not overlap the probe is anchored.
            var anchored = new bool[count];
            anchored[0] = true;
            anchored[count - 1] = true;
            var fromStart = new float[count];
            float spanned = 0f;
            for (int i = 1; i < count; i++) { spanned += distance[i]; fromStart[i] = spanned; }

            spanned = 0f;
            for (int i = count - 2; i > 0; i--)
            {
                spanned += distance[i + 1];
                float low = Math.Max(startHeight - fromStart[i] * slope, endHeight - spanned * slope);
                float high = Math.Min(startHeight + fromStart[i] * slope, endHeight + spanned * slope);
                if (low > high)
                {
                    float t = fromStart[i] / Math.Max(1e-3f, fromStart[i] + spanned);
                    deck[i] = low + (high - low) * t;
                    anchored[i] = true;
                }
                else deck[i] = deck[i] < low ? low : (deck[i] > high ? high : deck[i]);
            }

            Straighten(terrain, distance, count, deck, anchored);
        }

        /// <summary>
        /// The clamp band between the endpoints. An elevation at the limit fixes the floor (or ceiling) at
        /// both heights; one past +/-1 fixes its own end. Level ends leave it open.
        /// </summary>
        public static void ElevationBand(float startHeight, float endHeight,
            float startElevation, float endElevation, float elevationLimit, float reach,
            out float floorStart, out float floorEnd, out float ceilingStart, out float ceilingEnd)
        {
            floorStart = floorEnd = -Unbounded;
            ceilingStart = ceilingEnd = Unbounded;

            if (startElevation >= elevationLimit || endElevation >= elevationLimit)
            {
                floorStart = startHeight;
                floorEnd = endHeight;
            }
            else
            {
                if (startElevation > 1f)
                {
                    floorStart = startHeight;
                    floorEnd = Math.Max(floorEnd, endHeight - reach);
                }
                if (endElevation > 1f)
                {
                    floorStart = Math.Max(floorStart, startHeight - reach);
                    floorEnd = endHeight;
                }
            }

            if (startElevation <= -elevationLimit || endElevation <= -elevationLimit)
            {
                ceilingStart = startHeight;
                ceilingEnd = endHeight;
            }
            else
            {
                if (startElevation < -1f)
                {
                    ceilingStart = startHeight;
                    ceilingEnd = Math.Min(ceilingEnd, endHeight + reach);
                }
                if (endElevation < -1f)
                {
                    ceilingStart = Math.Min(ceilingStart, startHeight + reach);
                    ceilingEnd = endHeight;
                }
            }
        }

        /// <summary>A floor alone can still be pushed up by the surface; only floor and ceiling pin.</summary>
        public static bool NeedsPin(float startElevation, float endElevation, float elevationLimit,
            bool requireElevated, bool startFreeHeight, bool endFreeHeight)
        {
            if (!(elevationLimit > 0f) || float.IsInfinity(elevationLimit)) return false;
            // Only two underground endpoints guarantee a terrain-only profile.
            if (Math.Max(startElevation, endElevation) < -1f) return false;
            if (startFreeHeight || endFreeHeight) return true;
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

        private static void Straighten(float[] terrain, float[] distance, int count, float[] deck,
            bool[] anchored)
        {
            int i = 1;
            while (i < count - 1)
            {
                if (deck[i] == terrain[i] || anchored[i]) { i++; continue; }

                int last = i;
                while (last + 1 < count - 1 && deck[last + 1] != terrain[last + 1] &&
                       !anchored[last + 1]) last++;

                float spanned = 0f;
                for (int j = i; j <= last + 1; j++) spanned += distance[j];
                if (spanned > 0f)
                {
                    float low = deck[i - 1];
                    float high = deck[last + 1];
                    float walked = 0f;
                    for (int j = i; j <= last; j++)
                    {
                        walked += distance[j];
                        deck[j] = low + (high - low) * (walked / spanned);
                    }
                }
                i = last + 2;
            }
        }
    }
}
