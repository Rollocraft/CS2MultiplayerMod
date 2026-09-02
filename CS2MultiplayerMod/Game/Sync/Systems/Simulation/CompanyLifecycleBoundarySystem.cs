using Game;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Owns the native company-lifecycle boundary immediately before the move-away executor. On a
    /// client it removes the closure and property-seeking decisions local systems proposed, while
    /// leaving those systems running for the figures, resource orders and demand signals the rest
    /// of the simulation still needs from them. Closures the host roster asked for are whitelisted
    /// and pass through untouched.
    ///
    /// The main company system owns all retained state; this class is only its ordering point.
    /// </summary>
    public sealed partial class CompanyLifecycleBoundarySystem : GameSystemBase
    {
        private CompanyStatsSyncSystem _companies;

        protected override void OnCreate()
        {
            base.OnCreate();
            _companies = World.GetOrCreateSystemManaged<CompanyStatsSyncSystem>();
        }

        /// <summary>
        /// The native executor runs on a wide interval, so matching it here keeps this boundary
        /// off the frames where it would have nothing to do. A proposal made in between simply
        /// waits, because nothing consumes it until that executor runs either.
        /// </summary>
        public override int GetUpdateInterval(SystemUpdatePhase phase) =>
            phase == SystemUpdatePhase.GameSimulation ? 16 : 1;

        protected override void OnUpdate()
        {
            using (Diagnostics.SyncProfiler.Measure("CompanyStats.Lifecycle"))
            {
                if (_companies != null) _companies.CancelClientLifecycleDecisions();
            }
        }
    }
}
