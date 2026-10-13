namespace CS2MultiplayerMod.Game.Sync.Infrastructure
{
    /// <summary>Realized by <see cref="Systems.SyncRealizeSystem"/> in ToolUpdate; reads its own holds from <see cref="RealizeGate"/>.</summary>
    internal interface IRealizeStage
    {
        void RealizePending();
    }

    /// <summary>
    /// Per-frame holds written only by <see cref="Systems.SyncRealizeSystem"/>. Static because the
    /// systems that wait on them are not wired to the pipeline.
    /// </summary>
    internal static class RealizeGate
    {
        /// <summary>Remote terrain still queued: nothing new is placed at a sender-sampled height.</summary>
        public static bool TerrainBacklog;

        /// <summary>A placement is waiting on its road (or recovery froze net edits): bulldoze/replace wait.</summary>
        public static bool NetMutationHeld;

        /// <summary>Roads, zoning and zone-grown buildings are held. Implies <see cref="TerrainBacklog"/>.</summary>
        public static bool WorldBuildingHeld;

        public static void Reset() => TerrainBacklog = NetMutationHeld = WorldBuildingHeld = false;
    }

    /// <summary>
    /// Time a system could not make progress, so retry windows count attempts rather than wall-clock
    /// time. Observe once per frame and add the result to pending deadlines.
    /// </summary>
    internal sealed class HeldTime
    {
        private readonly CS2MultiplayerMod.Core.Sync.ActiveRetryClock _clock =
            new CS2MultiplayerMod.Core.Sync.ActiveRetryClock();
        private long _lastMs;
        private bool _initialized;

        /// <summary>The gap since the last call while held, else zero; zero on the first call.</summary>
        public long Observe(long nowMs, bool held)
        {
            long before = _clock.NowMs;
            _clock.Observe(nowMs, held);
            long delta = _initialized && nowMs > _lastMs ? nowMs - _lastMs : 0;
            _lastMs = nowMs;
            _initialized = true;
            return delta - (_clock.NowMs - before);
        }

        public void Reset()
        {
            _clock.Reset();
            _lastMs = 0;
            _initialized = false;
        }
    }
}
