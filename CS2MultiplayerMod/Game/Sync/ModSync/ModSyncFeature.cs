namespace CS2MultiplayerMod.Game.Sync.ModSync
{
    /// <summary>Developer switches for mod-state replication; not player settings.</summary>
    internal static class ModSyncFeature
    {
        /// <summary>
        /// Replicate a third-party type only if the runtime saves it. Assembly filtering alone mostly
        /// replicates tool previews and overlays that churn while idle. Off only to measure that cost.
        /// </summary>
        public static bool RequireDurableTypes = true;

        /// <summary>Whether captured changes are sent; capture and logging alone are safe for observation.</summary>
        public static bool SendCaptured = true;

        /// <summary>Whether arriving changes are written into the world.</summary>
        public static bool ApplyReceived = true;

        /// <summary>Carriers per capture pass; bounds the worst frame, the prefilter handles the normal one.</summary>
        public static int MaxCarriersPerPass = 64;

        /// <summary>How far a closure walk follows references from its carrier.</summary>
        public static int MaxClosureDepth = 3;

        /// <summary>How long an arriving change waits for a carrier that is still being built.</summary>
        public static long UnresolvedHoldMs = 5000;
    }
}
