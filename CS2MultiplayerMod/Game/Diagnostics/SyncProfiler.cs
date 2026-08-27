using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace CS2MultiplayerMod.Game.Diagnostics
{
    /// <summary>
    /// How much of the main thread the mod's own systems used, by scope.
    ///
    /// A performance complaint about a large city is otherwise unanswerable: the game is already
    /// slow at that size, so a frame-time histogram cannot say which side of the line a
    /// millisecond came from, and reading the code only produces candidates. This measures the
    /// mod's share directly and names the scope that spent it.
    ///
    /// Scopes are placed at system and pass level, never inside a per-entity loop. Two timestamp
    /// reads per scope at a few hundred entries a second is far below the noise floor of what it
    /// measures, so this stays on rather than hiding behind a setting nobody enables before the
    /// session that went wrong.
    /// </summary>
    public static class SyncProfiler
    {
        /// <summary>Scopes named after this many are ignored rather than growing the table.</summary>
        private const int MaxScopes = 64;

        /// <summary>Scopes listed in one report, largest first.</summary>
        private const int ReportedScopes = 10;

        private static readonly Dictionary<string, Sample> Samples =
            new Dictionary<string, Sample>(MaxScopes, StringComparer.Ordinal);
        private static readonly List<Sample> Ordered = new List<Sample>(MaxScopes);
        private static readonly double MillisecondsPerTick = 1000.0 / Stopwatch.Frequency;

        internal sealed class Sample
        {
            public string Name;
            public long Ticks;
            public long WorstTicks;
            public int Calls;
        }

        /// <summary>
        /// Accumulates into one scope for as long as it is alive. Always use it with
        /// <c>using</c>: an early return inside a measured pass must still close the scope.
        /// </summary>
        public struct Scope : IDisposable
        {
            private readonly Sample _sample;
            private readonly long _start;

            internal Scope(Sample sample)
            {
                _sample = sample;
                _start = sample != null ? Stopwatch.GetTimestamp() : 0L;
            }

            public void Dispose()
            {
                if (_sample == null) return;
                long elapsed = Stopwatch.GetTimestamp() - _start;
                _sample.Ticks += elapsed;
                if (elapsed > _sample.WorstTicks) _sample.WorstTicks = elapsed;
                _sample.Calls++;
            }
        }

        /// <summary>
        /// Nesting is allowed, but an inner scope's time is also counted in its outer one, so the
        /// reported total is not a sum of the listed scopes. Prefer siblings over nesting.
        /// </summary>
        public static Scope Measure(string name)
        {
            if (string.IsNullOrEmpty(name)) return default(Scope);
            Sample sample;
            if (!Samples.TryGetValue(name, out sample))
            {
                if (Samples.Count >= MaxScopes) return default(Scope);
                sample = new Sample { Name = name };
                Samples[name] = sample;
            }
            return new Scope(sample);
        }

        /// <summary>Drops the window without reporting it - a world load is not a steady state.</summary>
        public static void Reset()
        {
            foreach (KeyValuePair<string, Sample> pair in Samples)
            {
                pair.Value.Ticks = 0;
                pair.Value.WorstTicks = 0;
                pair.Value.Calls = 0;
            }
        }

        /// <summary>
        /// The window's report, or null when nothing was measured. <paramref name="windowMs"/> is
        /// the wall time the window covered, which turns the total into the only number that
        /// actually settles an argument: the share of the main thread the mod took.
        /// </summary>
        public static string Report(long windowMs)
        {
            Ordered.Clear();
            long totalTicks = 0;
            foreach (KeyValuePair<string, Sample> pair in Samples)
            {
                if (pair.Value.Calls == 0) continue;
                totalTicks += pair.Value.Ticks;
                Ordered.Add(pair.Value);
            }
            if (Ordered.Count == 0) return null;

            Ordered.Sort(CompareDescendingByTicks);

            double totalMs = totalTicks * MillisecondsPerTick;
            var text = new StringBuilder(256);
            text.Append("[MP] SyncCost/").Append(windowMs / 1000).Append("s: total ")
                .Append(totalMs.ToString("F0")).Append(" ms");
            if (windowMs > 0)
                text.Append(" (").Append((100.0 * totalMs / windowMs).ToString("F1"))
                    .Append("% of main thread)");
            text.Append(" -");

            int listed = Ordered.Count < ReportedScopes ? Ordered.Count : ReportedScopes;
            for (int i = 0; i < listed; i++)
            {
                Sample sample = Ordered[i];
                double ms = sample.Ticks * MillisecondsPerTick;
                // The worst single pass is what a player feels as a hitch; a mean hides it.
                text.Append(i == 0 ? " " : ", ").Append(sample.Name).Append(' ')
                    .Append(ms.ToString("F0")).Append(" ms/").Append(sample.Calls)
                    .Append(" (worst ")
                    .Append((sample.WorstTicks * MillisecondsPerTick).ToString("F1"))
                    .Append(" ms)");
            }
            if (Ordered.Count > listed)
                text.Append(", +").Append(Ordered.Count - listed).Append(" more");
            text.Append('.');

            Reset();
            return text.ToString();
        }

        private static int CompareDescendingByTicks(Sample first, Sample second) =>
            second.Ticks.CompareTo(first.Ticks);
    }
}
