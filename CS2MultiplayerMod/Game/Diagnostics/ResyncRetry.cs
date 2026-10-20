using CS2MultiplayerMod.Game.Sync.Infrastructure;

namespace CS2MultiplayerMod.Game.Diagnostics
{
    /// <summary>Keeps a repair alive during one arbitration hold without counting retries as new evidence.</summary>
    internal sealed class ResyncRetry
    {
        private ResyncReport _report;
        public bool HasReport => _report != null;
        public bool IsHeld => _report != null && ResyncArbiter.IsPending(_report);

        public bool KeepTrying(ResyncReport report)
        {
            if (_report != null) return IsHeld;
            _report = report;
            return SyncInbox.Settle(report) == ResyncVerdict.Held;
        }

        public void Resolve(long nowMs, string outcome)
        {
            if (_report != null)
                ResyncArbiter.Withdraw(_report.Subsystem, _report.Reason, _report.Subject,
                    nowMs, outcome);
            Reset();
        }

        public void Reset() => _report = null;
    }
}
