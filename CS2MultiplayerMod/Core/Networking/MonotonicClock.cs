using System.Diagnostics;

namespace CS2MultiplayerMod.Core.Networking
{
    /// <summary>
    /// Millisecond clock for stamping transport events on the I/O thread. Never leaves Core: the
    /// session maps stamps onto the caller's clock when draining.
    /// </summary>
    public static class MonotonicClock
    {
        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        public static long NowMs => Clock.ElapsedMilliseconds;
    }
}
