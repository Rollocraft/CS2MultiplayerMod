using System;

namespace CS2MultiplayerMod.Core.Sync
{
    /// <summary>
    /// Retry counter; reset after a successful commit. Exhaustion is a recovery boundary, not a reason to
    /// keep rebuilding.
    /// </summary>
    public sealed class BoundedRetryBudget
    {
        public BoundedRetryBudget(int maximumAttempts)
        {
            if (maximumAttempts <= 0) throw new ArgumentOutOfRangeException(nameof(maximumAttempts));
            MaximumAttempts = maximumAttempts;
        }

        public int MaximumAttempts { get; }
        public int AttemptsUsed { get; private set; }

        public bool TryConsume()
        {
            if (AttemptsUsed >= MaximumAttempts) return false;
            AttemptsUsed++;
            return true;
        }

        public void Reset() => AttemptsUsed = 0;
    }
}
