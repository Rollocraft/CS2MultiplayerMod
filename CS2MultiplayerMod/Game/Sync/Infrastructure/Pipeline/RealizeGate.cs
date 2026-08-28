namespace CS2MultiplayerMod.Game.Sync.Infrastructure
{
    /// <summary>
    /// One frame-scoped fact that several systems need and none of them can see for themselves:
    /// whether remote world-building work is currently held back.
    ///
    /// <see cref="Systems.SyncRealizeSystem"/> already decides this each frame - terrain is behind,
    /// or the net pipeline still has placements queued - and holds roads, zoning and zone-grown
    /// buildings until it clears. Systems that merely WAIT on those things are not themselves
    /// gated, so they keep running and keep counting down retry windows for targets that cannot
    /// possibly arrive yet. When the window then expires they report a target that "did not
    /// resolve" and ask for a full world reload, over a building the mod was still holding back.
    ///
    /// Reading a flag is what those systems need rather than a reference to the pipeline, hence a
    /// static: they are constructed independently and are not wired to each other.
    /// </summary>
    internal static class RealizeGate
    {
        /// <summary>
        /// True while roads, zoning and zone-grown buildings cannot be applied. Written once per
        /// frame by the realize pipeline, read by anything that waits on one of them.
        /// </summary>
        public static bool WorldBuildingHeld;

        /// <summary>Clear on a world reload or a session end - nothing is held in a world that is gone.</summary>
        public static void Reset() => WorldBuildingHeld = false;
    }

    /// <summary>
    /// Measures how long a system has been unable to make progress, so a retry window can be spent
    /// on attempts rather than on wall-clock seconds. Call <see cref="Observe"/> once per frame and
    /// add the result to every pending deadline.
    /// </summary>
    internal sealed class HeldTime
    {
        private long _lastMs;

        /// <summary>
        /// Time to add to pending deadlines this frame: the gap since the previous call when
        /// <paramref name="held"/>, and zero otherwise. Returns zero on the first call, so a system
        /// that starts mid-session never back-dates its first window.
        /// </summary>
        public long Observe(long nowMs, bool held)
        {
            long delta = _lastMs == 0 ? 0 : nowMs - _lastMs;
            _lastMs = nowMs;
            return held && delta > 0 ? delta : 0;
        }

        public void Reset() => _lastMs = 0;
    }
}
