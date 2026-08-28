using System;

namespace CS2MultiplayerMod.Core.Sync
{
    /// <summary>
    /// Small game-free retry counter shared by synchronization pipelines. A caller must explicitly
    /// reset it after a successful commit; exhausting it is a recovery boundary, never an invitation
    /// to keep rebuilding the same unsafe transaction forever.
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
