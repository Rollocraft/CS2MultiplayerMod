using System.Diagnostics;

namespace CS2MultiplayerMod.Game.Diagnostics
{
    /// <summary>
    /// Frame times, sampled from a system that runs once per rendered frame.
    ///
    /// Performance reports were previously reconstructed from whatever periodic log line happened
    /// to be gated per frame - a heartbeat's overshoot past its own deadline, a position send's
    /// rate limiter. Those are indirect, they disagree with each other, and neither distinguishes
    /// "slow every frame" from "one long frame a second", which is exactly the distinction that
    /// says which system is at fault. This reports the distribution directly.
    /// </summary>
    public static class FrameProbe
    {
        private const long ReportIntervalMs = 30000;

        /// <summary>Upper edge of each bucket, in milliseconds; the last one is everything above.</summary>
        private static readonly int[] BucketCeilingMs = { 17, 33, 50, 100, 250, 500, int.MaxValue };
        private static readonly int[] Buckets = new int[BucketCeilingMs.Length];

        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static long _lastFrameMs = -1;
        private static long _lastReportMs;
        private static long _totalMs;
        private static int _frames;
        private static long _worstMs;

        /// <summary>Call once per rendered frame while a session is live.</summary>
        public static void Sample()
        {
            long now = Clock.ElapsedMilliseconds;
            if (_lastFrameMs < 0)
            {
                _lastFrameMs = now;
                _lastReportMs = now;
                return;
            }

            long frame = now - _lastFrameMs;
            _lastFrameMs = now;

            _frames++;
            _totalMs += frame;
            if (frame > _worstMs) _worstMs = frame;
            for (int i = 0; i < BucketCeilingMs.Length; i++)
            {
                if (frame <= BucketCeilingMs[i]) { Buckets[i]++; break; }
            }

            if (now - _lastReportMs < ReportIntervalMs || _frames == 0) return;
            Report(now);
        }

        /// <summary>Drop the window without reporting it - a world load is not a frame time.</summary>
        public static void Reset()
        {
            SyncProfiler.Reset();
            _lastFrameMs = -1;
            _frames = 0;
            _totalMs = 0;
            _worstMs = 0;
            for (int i = 0; i < Buckets.Length; i++) Buckets[i] = 0;
        }

        private static void Report(long now)
        {
            long seconds = (now - _lastReportMs) / 1000;
            if (seconds <= 0) seconds = 1;

            string line = "[MP] Frames/" + seconds + "s: " + _frames +
                          " (" + (_frames / seconds) + "/s, mean " + (_totalMs / _frames) +
                          " ms, worst " + _worstMs + " ms) " + Histogram();

            Mod.Verbose(line);
            // The flight log keeps it regardless of the verbose setting: a performance report is
            // exactly the case where the log was already captured before anyone asked for detail.
            FlightRecorder.Note(line);

            // Immediately after the frame times, so a slow window and the mod's share of it are
            // always read together.
            string cost = SyncProfiler.Report(now - _lastReportMs);
            if (cost != null)
            {
                Mod.Verbose(cost);
                FlightRecorder.Note(cost);
            }

            _lastReportMs = now;
            _frames = 0;
            _totalMs = 0;
            _worstMs = 0;
            for (int i = 0; i < Buckets.Length; i++) Buckets[i] = 0;
        }

        private static string Histogram()
        {
            var text = new System.Text.StringBuilder(96);
            text.Append("<=17ms:").Append(Buckets[0]);
            text.Append(" <=33:").Append(Buckets[1]);
            text.Append(" <=50:").Append(Buckets[2]);
            text.Append(" <=100:").Append(Buckets[3]);
            text.Append(" <=250:").Append(Buckets[4]);
            text.Append(" <=500:").Append(Buckets[5]);
            text.Append(" >500:").Append(Buckets[6]);
            return text.ToString();
        }
    }
}
