using System.Collections.Generic;
using Colossal.Mathematics;
using Unity.Mathematics;

namespace CS2MultiplayerMod.Game.Sync.Infrastructure
{
    /// <summary>
    /// Objects and roads any player bulldozed in the last minute. An edit whose target never appears next
    /// to one of these lost a race with the bulldozer: its sender loses the target too, so the two cities
    /// agree and the edit is dropped instead of reloading the world. Game thread only.
    /// </summary>
    internal static class RecentRemovals
    {
        private const long WindowMs = 60000;
        private const int Capacity = 4096;

        private struct Removal
        {
            public Bezier4x2 Curve;
            public bool IsPoint;
            public long AtMs;
        }

        private static readonly List<Removal> Removals = new List<Removal>();

        public static void NotePoint(float3 position, long nowMs) =>
            Note(new Bezier4x2(position.xz, position.xz, position.xz, position.xz), true, nowMs);

        public static void NoteCurve(Bezier4x3 curve, long nowMs) => Note(curve.xz, false, nowMs);

        private static void Note(Bezier4x2 curve, bool isPoint, long nowMs)
        {
            Prune(nowMs);
            if (Removals.Count >= Capacity) Removals.RemoveRange(0, Capacity / 4);
            Removals.Add(new Removal { Curve = curve, IsPoint = isPoint, AtMs = nowMs });
        }

        public static bool Near(float3 position, float radius, long nowMs)
        {
            Prune(nowMs);
            for (int i = 0; i < Removals.Count; i++)
            {
                Removal removal = Removals[i];
                float distance = removal.IsPoint
                    ? math.distance(removal.Curve.a, position.xz)
                    : MathUtils.Distance(removal.Curve, position.xz, out float t);
                if (distance <= radius) return true;
            }
            return false;
        }

        public static void Clear() => Removals.Clear();

        private static void Prune(long nowMs)
        {
            int expired = 0;
            while (expired < Removals.Count && nowMs - Removals[expired].AtMs > WindowMs) expired++;
            if (expired > 0) Removals.RemoveRange(0, expired);
        }
    }
}
