using System.Collections.Generic;
using Colossal.Mathematics;
using Unity.Mathematics;

namespace CS2MultiplayerMod.Game.Sync.Infrastructure
{
    /// <summary>
    /// Tells a mid-span split (exact 3D sub-curves) from a span rebuilt at another height (XZ only) or
    /// partly consumed (sub-curves that no longer cover it, see <see cref="CoverWholeSpan"/>).
    /// </summary>
    internal static class SplitMatch
    {
        // Split halves are exact; this only absorbs float noise.
        public const float TolXZ = 1.0f;

        // Above split-node smoothing, below the smallest elevation step (1.25 m).
        public const float TolY = 1.0f;

        /// <summary>Endpoints and midpoint within <see cref="TolXZ"/> of the curve; the midpoint rules out chords.</summary>
        public static bool FollowsXZ(Bezier4x3 piece, Bezier4x3 whole)
        {
            return MathUtils.Distance(whole.xz, piece.a.xz, out float t) <= TolXZ
                && MathUtils.Distance(whole.xz, MathUtils.Position(piece, 0.5f).xz, out t) <= TolXZ
                && MathUtils.Distance(whole.xz, piece.d.xz, out t) <= TolXZ;
        }

        /// <summary>Also matches the height at those samples: a split half, not a rebuild.</summary>
        public static bool HeightMatches(Bezier4x3 piece, Bezier4x3 whole)
        {
            return HeightAt(whole, piece.a)
                && HeightAt(whole, MathUtils.Position(piece, 0.5f))
                && HeightAt(whole, piece.d);
        }

        /// <summary>A true 3D sub-curve: <see cref="FollowsXZ"/> and <see cref="HeightMatches"/>.</summary>
        public static bool IsSubCurve3D(Bezier4x3 piece, Bezier4x3 whole) =>
            FollowsXZ(piece, whole) && HeightMatches(piece, whole);

        // Split halves meet with no gap; a consumed stretch is well past 4 m.
        private const float CoverageStep = 2f;
        private const float CoverageGapTol = 4f;

        /// <summary>
        /// Pre-filtered sub-curves cover the whole span: a pure split. A gap past
        /// <see cref="CoverageGapTol"/> means part was consumed and the delete must replicate.
        /// </summary>
        public static bool CoverWholeSpan(List<Bezier4x3> pieces, Bezier4x3 whole)
        {
            if (pieces == null || pieces.Count == 0) return false;

            float length = math.max(MathUtils.Length(whole), 1f);
            int samples = math.clamp((int)math.ceil(length / CoverageStep) + 1, 9, 65);
            float spacing = length / (samples - 1);

            int uncoveredRun = 0;
            for (int i = 0; i < samples; i++)
            {
                float3 p = MathUtils.Position(whole, i / (float)(samples - 1));
                bool covered = false;
                for (int j = 0; j < pieces.Count && !covered; j++)
                {
                    covered = MathUtils.Distance(pieces[j].xz, p.xz, out float t) <= TolXZ
                           && math.abs(MathUtils.Position(pieces[j], t).y - p.y) <= TolY;
                }
                if (covered) { uncoveredRun = 0; continue; }
                if (++uncoveredRun * spacing > CoverageGapTol) return false;
            }
            return true;
        }

        private static bool HeightAt(Bezier4x3 whole, float3 p)
        {
            MathUtils.Distance(whole.xz, p.xz, out float t);
            return math.abs(MathUtils.Position(whole, t).y - p.y) <= TolY;
        }
    }
}
