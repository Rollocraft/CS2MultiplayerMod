using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace CS2MultiplayerMod.Game.Diagnostics
{
    /// <summary>
    /// The city zone a scope's work belongs to: one channel serves several zones and a zone several
    /// channels.
    /// </summary>
    public enum SyncZone
    {
        None = 0,
        Residential,
        Commercial,
        Industrial,
        Office,
    }

    /// <summary>
    /// Main-thread time spent by the mod's own systems, per scope. Scopes sit at system and pass level,
    /// never per entity, so this stays always on.
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
            public SyncZone Zone;
            public long Ticks;
            public long WorstTicks;
            public int Calls;
        }

        private static readonly string[] ZoneNames =
            { null, "residential", "commercial", "industrial", "office" };
        private static readonly long[] ZoneTicks = new long[5];

        /// <summary>Always with <c>using</c>, so an early return closes the scope.</summary>
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
        /// Nested scopes count in their outer scope too, so the total is not the sum; prefer siblings.
        /// </summary>
        public static Scope Measure(string name) => Measure(name, SyncZone.None);

        /// <summary>As <see cref="Measure(string)"/>, also adding to a zone total.</summary>
        public static Scope Measure(string name, SyncZone zone)
        {
            if (string.IsNullOrEmpty(name)) return default(Scope);
            if (!Samples.TryGetValue(name, out Sample sample))
            {
                if (Samples.Count >= MaxScopes) return default(Scope);
                sample = new Sample { Name = name, Zone = zone };
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
            for (int i = 0; i < ZoneTicks.Length; i++) ZoneTicks[i] = 0;
        }

        /// <summary>
        /// The window's report, or null when nothing was measured; <paramref name="windowMs"/> turns the total
        /// into a main-thread share.
        /// </summary>
        public static string Report(long windowMs)
        {
            Ordered.Clear();
            long totalTicks = 0;
            for (int i = 0; i < ZoneTicks.Length; i++) ZoneTicks[i] = 0;
            foreach (KeyValuePair<string, Sample> pair in Samples)
            {
                if (pair.Value.Calls == 0) continue;
                totalTicks += pair.Value.Ticks;
                int zone = (int)pair.Value.Zone;
                if (zone > 0 && zone < ZoneTicks.Length) ZoneTicks[zone] += pair.Value.Ticks;
                Ordered.Add(pair.Value);
            }
            if (Ordered.Count == 0) return null;

            Ordered.Sort(CompareDescendingByTicks);

            double totalMs = totalTicks * MillisecondsPerTick;
            var text = new StringBuilder(256);
            text.Append("SyncCost/").Append(windowMs / 1000).Append("s: total ")
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

            AppendZones(text, windowMs);

            Reset();
            return text.ToString();
        }

        /// <summary>The by-zone line; unattributed time is never folded into a zone.</summary>
        private static void AppendZones(StringBuilder text, long windowMs)
        {
            long attributed = 0;
            for (int i = 1; i < ZoneTicks.Length; i++) attributed += ZoneTicks[i];
            if (attributed == 0) return;

            text.Append(" By zone:");
            bool first = true;
            for (int i = 1; i < ZoneTicks.Length; i++)
            {
                double ms = ZoneTicks[i] * MillisecondsPerTick;
                text.Append(first ? " " : ", ").Append(ZoneNames[i]).Append(' ')
                    .Append(ms.ToString("F0")).Append(" ms");
                if (windowMs > 0)
                    text.Append(" (").Append((100.0 * ms / windowMs).ToString("F2")).Append("%)");
                first = false;
            }
            text.Append('.');
        }

        private static int CompareDescendingByTicks(Sample first, Sample second) =>
            second.Ticks.CompareTo(first.Ticks);
    }
}
