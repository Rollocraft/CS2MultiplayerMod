using System.Diagnostics;
using System.Globalization;
using CS2MultiplayerMod.Core.Diagnostics;

namespace CS2MultiplayerMod.Game.Diagnostics
{
    /// <summary>
    /// Frame time distribution, sampled once per rendered frame; separates "slow every frame" from
    /// "one long frame a second".
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
        private static uint _lastSimulationFrame;
        private static ulong _simulationTicks;
        private static float _minimumSpeed, _maximumSpeed, _lastSpeed;
        private static int _speedChanges;

        /// <summary>Call once per rendered frame while a session is live.</summary>
        public static void Sample(float selectedSpeed, uint simulationFrame)
        {
            long now = Clock.ElapsedMilliseconds;
            if (_lastFrameMs < 0)
            {
                _lastFrameMs = now;
                _lastReportMs = now;
                _lastSimulationFrame = simulationFrame;
                _minimumSpeed = _maximumSpeed = _lastSpeed = selectedSpeed;
                return;
            }

            uint ticks = unchecked(simulationFrame - _lastSimulationFrame);
            // World replacement can reset the simulation counter without resetting the UI world.
            if (ticks > int.MaxValue)
            {
                Reset();
                Sample(selectedSpeed, simulationFrame);
                return;
            }
            _simulationTicks += ticks;
            _lastSimulationFrame = simulationFrame;
            if (selectedSpeed != _lastSpeed) _speedChanges++;
            _lastSpeed = selectedSpeed;
            if (selectedSpeed < _minimumSpeed) _minimumSpeed = selectedSpeed;
            if (selectedSpeed > _maximumSpeed) _maximumSpeed = selectedSpeed;

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
            _simulationTicks = 0;
            _speedChanges = 0;
            _frames = 0;
            _totalMs = 0;
            _worstMs = 0;
            for (int i = 0; i < Buckets.Length; i++) Buckets[i] = 0;
        }

        private static void Report(long now)
        {
            long seconds = (now - _lastReportMs) / 1000;
            if (seconds <= 0) seconds = 1;

            string line = "Frames/" + seconds + "s: " + _frames +
                          " (" + (_frames / seconds) + "/s, mean " + (_totalMs / _frames) +
                          " ms, worst " + _worstMs + " ms) " + Histogram() +
                          " selectedSpeed=" + _minimumSpeed.ToString("0.##", CultureInfo.InvariantCulture) +
                          ".." + _maximumSpeed.ToString("0.##", CultureInfo.InvariantCulture) +
                          " speedChanges=" + _speedChanges +
                          " simulationTicksPerSecond=" +
                          (1000.0 * _simulationTicks / (now - _lastReportMs))
                              .ToString("F1", CultureInfo.InvariantCulture);

            // Trace: always in the flight log, which is already captured when a report arrives.
            SyncLog.Trace(LogTopic.Performance, line);

            // Right after the frame times, so a slow window and the mod's share read together.
            string cost = SyncProfiler.Report(now - _lastReportMs);
            if (cost != null) SyncLog.Trace(LogTopic.Performance, cost);

            _lastReportMs = now;
            _simulationTicks = 0;
            _speedChanges = 0;
            _minimumSpeed = _maximumSpeed = _lastSpeed;
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
