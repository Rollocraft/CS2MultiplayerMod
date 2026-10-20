# Core performance regressions

Run `dotnet run --project CS2MultiplayerMod.PerformanceTests -c Release` from the repository root.
This dependency-free harness compiles the production network writer and replay window.
It checks byte-for-byte float/UTF-8 compatibility, randomized replay-cache behavior,
deadline renewal, overflow, and allocation-free writes and idle pruning.

Timing output is informational; assertions use allocation counts and behavior, not
machine-dependent timing thresholds. `-- --baseline` reports measurements without
enforcing allocation budgets, for comparison against the previous implementation.
