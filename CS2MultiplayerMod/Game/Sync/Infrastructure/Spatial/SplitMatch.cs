using System.Collections.Generic;
using Colossal.Mathematics;
using Unity.Mathematics;

namespace CS2MultiplayerMod.Game.Sync.Infrastructure
{
    /// <summary>
    /// Geometry tests shared by the net capture guards to tell a mid-span SPLIT from the same span
    /// REBUILT at a different height. When the player taps a road mid-span, the game deletes the
    /// edge and creates its halves - exact 3D sub-curves of the original. When the player redraws a
    /// road over an existing one at a DIFFERENT elevation, the game also commits delete + create - but
    /// the new pieces follow the old centreline only in XZ; their height differs. A placement can
    /// also CONSUME part of the edge (a roundabout swallows the stretch inside its circle): true
    /// sub-curves that no longer cover the whole span - <see cref="CoverWholeSpan"/> tells that apart.
    /// </summary>
    internal static class SplitMatch
    {
        // How close (XZ, metres) the sample points of a Created edge must sit to a Deleted edge's
        // centreline to count as following it. Split halves are exact sub-curves (~0 m); the tolerance
        // only absorbs float noise and stays well below where a separately-drawn road would land.
        public const float TolXZ = 1.0f;

        // Max height difference (metres) between a following piece and the deleted curve for the piece
        // to still count as a true split half. The slight height smoothing the game applies around a
        // fresh split node stays well under this; the smallest elevation step the road tools place
        // (1.25 m) exceeds it.
        public const float TolY = 1.0f;

        /// <summary>
        /// True when <paramref name="piece"/> follows <paramref name="whole"/>'s centreline in XZ:
        /// its endpoints AND midpoint all lie within <see cref="TolXZ"/> of the curve. The midpoint
        /// sample keeps a road that merely starts and ends ON the curve from matching.
        /// </summary>
        public static bool FollowsXZ(Bezier4x3 piece, Bezier4x3 whole)
        {
            float t;
            return MathUtils.Distance(whole.xz, piece.a.xz, out t) <= TolXZ
                && MathUtils.Distance(whole.xz, MathUtils.Position(piece, 0.5f).xz, out t) <= TolXZ
                && MathUtils.Distance(whole.xz, piece.d.xz, out t) <= TolXZ;
        }

        /// <summary>
        /// True when a <see cref="FollowsXZ"/> piece also matches <paramref name="whole"/>'s HEIGHT at
        /// its endpoints and midpoint - i.e. it is a true 3D sub-curve (a split half), not the same
        /// span rebuilt at another elevation.
        /// </summary>
        public static bool HeightMatches(Bezier4x3 piece, Bezier4x3 whole)
        {
            return HeightAt(whole, piece.a)
                && HeightAt(whole, MathUtils.Position(piece, 0.5f))
                && HeightAt(whole, piece.d);
        }

        /// <summary>
        /// True when <paramref name="piece"/> is a true 3D sub-curve of <paramref name="whole"/> -
        /// follows its centreline in XZ AND matches its height. The one-call form of
        /// <see cref="FollowsXZ"/> + <see cref="HeightMatches"/> used wherever a curve must be
        /// recognised as "already part of" another.
        /// </summary>
        public static bool IsSubCurve3D(Bezier4x3 piece, Bezier4x3 whole)
        {
            return FollowsXZ(piece, whole) && HeightMatches(piece, whole);
        }

        // Coverage sampling step and the longest uncovered stretch still counted as covered: split
        // halves meet at the split node (~0 m gap), the smallest consumed stretch is well past 4 m.
        private const float CoverageStep = 2f;
        private const float CoverageGapTol = 4f;

        /// <summary>
        /// True when <paramref name="pieces"/> (pre-filtered sub-curves of <paramref name="whole"/>)
        /// jointly cover its entire span - a pure split. A gap past <see cref="CoverageGapTol"/> means
        /// part of the span was consumed and its removal must replicate.
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
                    float t;
                    covered = MathUtils.Distance(pieces[j].xz, p.xz, out t) <= TolXZ
                           && math.abs(MathUtils.Position(pieces[j], t).y - p.y) <= TolY;
                }
                if (covered) { uncoveredRun = 0; continue; }
                if (++uncoveredRun * spacing > CoverageGapTol) return false;
            }
            return true;
        }

        private static bool HeightAt(Bezier4x3 whole, float3 p)
        {
            float t;
            MathUtils.Distance(whole.xz, p.xz, out t);
            return math.abs(MathUtils.Position(whole, t).y - p.y) <= TolY;
        }
    }
}
