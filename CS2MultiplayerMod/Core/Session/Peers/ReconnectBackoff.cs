namespace CS2MultiplayerMod.Core.Session
{
    /// <summary>Finite reconnect schedule, driven by a monotonic clock.</summary>
    public sealed class ReconnectBackoff
    {
        public const int MaxAttempts = 6;
        public const long TimeoutMs = 120000;
        private static readonly long[] Delays = { 1000, 2000, 4000, 8000, 16000, 30000 };
        private long _startedMs;
        private long _nextMs;
        private long _attemptMs;
        public bool Active { get; private set; }
        public bool AttemptInFlight { get; private set; }
        public int Attempts { get; private set; }

        public void Begin(long nowMs)
        {
            Reset();
            Active = true;
            _startedMs = nowMs;
            _nextMs = nowMs + Delays[0];
        }

        public bool Expired(long nowMs) => Active &&
            (nowMs - _startedMs >= TimeoutMs || (!AttemptInFlight && Attempts >= MaxAttempts));

        public bool TryStartAttempt(long nowMs)
        {
            if (!Active || AttemptInFlight || Expired(nowMs) || nowMs < _nextMs) return false;
            Attempts++;
            AttemptInFlight = true;
            _attemptMs = nowMs;
            return true;
        }

        public bool AttemptTimedOut(long nowMs) => AttemptInFlight && nowMs - _attemptMs >= 15000;

        public void Failed(long nowMs)
        {
            AttemptInFlight = false;
            _nextMs = nowMs + Delays[System.Math.Min(Attempts, Delays.Length - 1)];
        }

        public int SecondsUntilAttempt(long nowMs) => !Active || AttemptInFlight ? 0 :
            (int)System.Math.Max(0, (_nextMs - nowMs + 999) / 1000);

        public void Reset()
        {
            Active = false;
            AttemptInFlight = false;
            Attempts = 0;
        }
    }
}
